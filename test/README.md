# Testing the Microsoft AD DNS Provider

The part worth testing is the WinRM → `DnsServer` cmdlet logic in
`MicrosoftAdDnsProvider`. The two validator classes are thin glue over it. So the
setup here drives that provider against a **real Windows DNS server** over WinRM —
no public internet, no CA, no full gateway required.

> **Internal-only.** Testing the plugin needs only LAN reachability from the test
> machine to your DNS server (WinRM, TCP 5985). Whether AD is reachable "from the
> outside" matters only for a real CA to *see* the record during actual cert
> issuance — a separate concern from testing this code. You verify results by
> querying the DNS server directly (`nslookup ... <dnsserver>`).

## What you need (lab)

1. A Windows Server with the **DNS Server role** hosting a throwaway forward-lookup
   zone, e.g. `example.test`. A domain controller works; a standalone DNS server
   works too. **Use a test zone, not production** — the harness writes and deletes
   records.
2. **WinRM enabled** on that server and reachable from the test machine:
   ```powershell
   # On the DNS server (elevated):
   Enable-PSRemoting -Force
   ```
   If the test machine is not domain-joined / not in the same domain, on the
   *test* machine add the server to TrustedHosts (or use `-UseSsl`):
   ```powershell
   Set-Item WSMan:\localhost\Client\TrustedHosts -Value dc01.corp.example.test -Concatenate
   ```
3. An identity in **DnsAdmins** (or Domain Admins), or delegated DNS management on
   the zone — either your current logon, or an explicit `AD_Username`/`AD_Password`.

## Step 1 — Infra smoke test (no code)

Run this first from the test machine. It exercises the exact prerequisites the
plugin depends on, so a failure here is an environment problem, not a code problem.

```powershell
cd test
.\smoke-test.ps1 -DnsServer dc01.corp.example.test -Zone example.test
# cross-domain / explicit creds:
.\smoke-test.ps1 -DnsServer dc01.corp.example.test -Zone example.test -Credential (Get-Credential)
```

## Step 2 — Provider harness (the real code)

Drives `MicrosoftAdDnsProvider` directly: connection check, TXT create/delete,
TXT additive-coexistence, targeted-value delete, CNAME create/delete, and
idempotent cleanup of a missing record.

```powershell
$env:AD_DnsServer = "dc01.corp.example.test"
$env:TEST_DOMAIN  = "example.test"      # must be covered by a hosted zone (or set AD_Zone)
# optional:
# $env:AD_Username = "CORP\svc-keyfactor"
# $env:AD_Password = "..."
# $env:AD_Zone     = "example.test"
# $env:AD_UseSSL   = "true"

dotnet run --project test/ManualTestHarness
```

Each step prints `OK`/`FAIL`; the process exits non-zero if any step failed. The
output includes `nslookup` commands so you can independently confirm records
appear and disappear on the server.

## Step 3 — Full gateway integration (optional, end-to-end)

Two variants, depending on whether the zone you're testing is publicly resolvable:

- **3a** — real public CA, publicly-delegated zone.
- **3b** — private ACME CA (step-ca), internal-only zone (e.g. an AD `.local`/`.corp`
  zone). This is the one worth reading closely — several of the failures below look
  like DNS or plugin bugs but are actually environment/config gaps specific to
  testing against a private CA and an internal zone.

Common setup for both:

1. Build the plugin (`dotnet build -c Release`) and copy the `net10.0` output into
   the gateway's `Extensions` folder (see the root `README.md`).
2. Restart the AnyGatewayREST service.
3. In the gateway UI, add a Domain Validation entry, pick **Microsoft Active
   Directory DNS** (`MicrosoftAdDomainValidator` for TXT / `MicrosoftAdCnameDomainValidator`
   for CNAME), fill in the `AD_*` fields, and map it to the domain.

### Step 3a — Full gateway integration, public CA / publicly-resolvable zone

Only meaningful if the zone is actually publicly resolvable (real domain, real NS
delegation to a DNS server the CA can query). Point the gateway's CA connector at
the public CA's ACME directory (e.g. Let's Encrypt, Google Trust Services), then
enroll a cert for the domain and watch the gateway stage → CA validate → cleanup.

If you don't have a public domain to spare, you can carve out a throwaway
subdomain of one you own and delegate just that subdomain (via NS records at your
registrar) to the Windows DNS server under test, rather than exposing your whole
domain or a production DNS server. **Exposing DNS (port 53) publicly on a domain
controller is a real security tradeoff** — prefer a dedicated, non-DC standalone
DNS server for the delegated subdomain if you go this route, and close the port
again once you're done testing.

**Public CAs reject internal/non-public zones outright.** If you try to enroll for
a name under an internal zone (e.g. `bri.command.local`) against a public CA, the
order fails immediately at `CreateOrder`:

```
urn:ietf:params:acme:error:rejectedIdentifier — "Domain must end in a public suffix."
```

This is a CA-side policy check (ICANN public-suffix list), not a DNS or plugin
problem — no amount of DNS troubleshooting will fix it. If your zone is internal,
skip straight to 3b.

### Step 3b — Full gateway integration against an internal zone, with a private ACME CA (step-ca)

To exercise the real gateway → CA → DNS-01 challenge → this plugin → DNS server →
CA re-check → issuance flow against an internal-only zone, run a private ACME
server instead of a public CA. [step-ca](https://smallstep.com/docs/step-ca/) is a
good fit — it speaks real ACME and doesn't enforce the public-suffix check.

#### Set up step-ca

On a Linux box reachable from the gateway (same subnet is simplest):

```bash
step ca init --name "Lab ACME CA" --dns step-ca-host.example --address :8443 --provisioner acme
step ca provisioner add acme --type ACME
```

Run it persistently (a plain foreground run dies the moment your SSH session does
or you hit Ctrl+C — easy to lose an hour to before noticing the CA silently went
away and every subsequent enrollment gets "connection actively refused"):

```bash
nohup step-ca ~/.step/config/ca.json --password-file ~/.step/secrets/password.txt > ~/stepca.log 2>&1 &
disown
ss -tlnp | grep 8443          # confirm it's actually listening
curl -sk https://localhost:8443/health
```

For production-like persistence, prefer the `step-ca` systemd unit
(`sudo systemctl enable --now step-ca`) if your install provides one.

#### Wire the gateway to step-ca

1. Get the ACME directory URL: `https://<step-ca-host>:8443/acme/acme/directory`.
2. Trust step-ca's root cert on the gateway host (`step ca root` on the step-ca
   box, then import into `Cert:\LocalMachine\Root` on the gateway) — otherwise the
   gateway's ACME client fails TLS validation against the self-signed lab root.
3. In the gateway's Certificate Authorities config, add/edit a CA entry pointing
   `DirectoryUrl` at that address.
4. **Set `DnsVerificationServer`** on the same CA config to your internal DNS
   server's IP (e.g. the domain controller from Step 1/2). This field defaults to
   empty, which makes the gateway's own DNS-propagation pre-check fall back to
   public resolvers (8.8.8.8, 1.1.1.1, etc.) — which can never see an internal
   zone, so propagation "verification" always reports `0/N servers confirmed` and
   the gateway proceeds on a blind fallback delay instead of a real check. Setting
   this field to the internal DNS server fixes that pre-check.

#### The gotcha that actually blocks internal-zone testing: step-ca's own DNS resolution

Even with the gateway's propagation pre-check fixed, an enrollment can still hang:
`StageValidation` and `SubmitChallenge` succeed, but the ACME order sits at
`pending` forever and eventually times out with `CertificateNotReady`. This is
because **step-ca does its own, completely independent DNS lookup** when it
validates the challenge — the fact that the gateway (or this plugin, or your own
`dig`/`nslookup` from elsewhere) can resolve the internal zone says nothing about
whether the machine step-ca itself is running on can.

Diagnose on the step-ca host:

```bash
dig SOA command.local                 # through the system resolver, as step-ca would see it
dig @<internal-dns-server> SOA command.local   # direct query, bypassing the system resolver
```

If the direct query works but the plain `dig` doesn't, the step-ca host's own DNS
resolution — not network reachability — is the problem. A few things that can
cause this, roughly in the order we hit them testing this plugin:

- **systemd-resolved split-DNS routing domains didn't take effect.** Setting a
  per-link routing domain (`resolvectl domain eth0 "~command.local"` plus
  `resolvectl dns eth0 <internal-dns-server>`) looked correct in `resolvectl
  status` but queries still went out to the public resolver path and NXDOMAIN'd
  (`.local` isn't a delegated public TLD, so a leaked public lookup always fails
  this way — a giveaway that routing isn't actually being honored). Don't trust
  that the config "looks right"; verify with an actual `dig` for a name you know
  exists in the zone (e.g. the zone's own SOA).
- **A local caching resolver (BIND, `dnsmasq`, etc.) already running on the box for
  another purpose can be repurposed as a reliable fix.** Add a forward zone
  pointing the internal zone at the internal DNS server:
  ```
  zone "command.local" {
      type forward;
      forward only;
      forwarders { <internal-dns-server>; };
  };
  ```
  then `systemctl restart bind9` (or your resolver of choice) and test with
  `dig @127.0.0.1 SOA command.local`.
- **DNSSEC validation on the forwarder breaks unsigned internal zones.** BIND's
  default `dnssec-validation auto;`/`yes;` tries to build a trust chain for every
  forwarded response, including the internal zone — which almost certainly isn't
  DNSSEC-signed (typical for AD-integrated DNS) — so validation fails and BIND
  returns `SERVFAIL` instead of passing the answer through. The log line that
  confirms this specific cause: `insecurity proof failed resolving
  'command.local/SOA/IN'`. Fix by adding `dnssec-validation no;` to the resolver's
  options and restarting it. Acceptable for a lab; know what you're doing before
  doing this anywhere production-adjacent.
- **Getting the OS to actually use the fixed resolver can itself be unreliable.**
  Even after BIND was confirmed correct via `dig @127.0.0.1 ...`, re-pointing the
  interface's default resolver via `resolvectl dns <iface> 127.0.0.1` still didn't
  take effect for plain `dig SOA command.local` (queries kept leaking to the
  public path) — on this host, `eth0` had `-DefaultRoute` set, meaning it's only
  used for domains matching its own routing domains, not as the general fallback.
  The reliable fix was to bypass `systemd-resolved`'s stub resolver entirely:
  ```bash
  sudo unlink /etc/resolv.conf
  echo "nameserver 127.0.0.1" | sudo tee /etc/resolv.conf
  dig SOA command.local     # confirm it now resolves through the fixed local resolver
  ```
  This breaks `systemd-resolved`'s management of `/etc/resolv.conf` (a static file
  instead of its managed symlink) — fine for a disposable lab box, not something to
  do on a host you need standard `systemd-resolved` behavior on long-term.

Once `dig SOA <your internal zone>` resolves correctly through the plain system
resolver (no explicit `@server`) on the step-ca host, retry the enrollment. A
successful run's `[FLOW:Enroll:CN=...]` trace should show the DNS verification
step reporting `N/N servers confirmed record` (not `0/N`), and the order should
progress from `pending` through `ready` to issuance instead of timing out.
