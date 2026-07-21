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

Only needed to prove issuance with a real CA, and only meaningful if the zone is
publicly resolvable:

1. Build the plugin (`dotnet build -c Release`) and copy the `net8.0` output into
   the gateway's `Extensions` folder (see the root `README.md`).
2. Restart the AnyGatewayREST service.
3. In the gateway UI, add a Domain Validation entry, pick **Microsoft Active
   Directory DNS** (`MicrosoftAdDomainValidator` for TXT / `MicrosoftAdCnameDomainValidator`
   for CNAME), fill in the `AD_*` fields, and map it to the domain.
4. Enroll a cert for that domain and watch the gateway stage → CA validate →
   cleanup.
