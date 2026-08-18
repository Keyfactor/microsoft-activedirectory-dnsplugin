## Overview

The Microsoft Active Directory DNS Plugin is a Keyfactor Domain Validator implementation that manages DNS records on a Microsoft Windows Server DNS server (traditional or Active Directory-integrated). It plugs into the Keyfactor AnyCA Gateway as an `IDomainValidator` and is invoked automatically during certificate enrollment when DNS-based domain control validation is required.

The DLL ships **two validator types**:

| Validator class | Validation type | Record published | Use case |
| --- | --- | --- | --- |
| `MicrosoftAdDomainValidator` | `dns-01` | TXT | ACME DNS-01 challenges |
| `MicrosoftAdCnameDomainValidator` | `cname` | CNAME | CNAME-based DCV (e.g. CSC Global, SSL Store) |

When configuring a Domain Validation Configuration in the gateway UI, pick the validator type that matches the CA's requirement — TXT/`dns-01` for ACME, CNAME/`cname` for CAs that validate via CNAME. Both share the same connection and configuration fields.

### How it works (V1)

V1 is **Windows-only**. The plugin manages records by opening a **WinRM remote PowerShell** session from the gateway host to the target DNS server and running the built-in `DnsServer` module cmdlets (`Get-DnsServerZone`, `Add-DnsServerResourceRecord`, `Get-DnsServerResourceRecord`, `Remove-DnsServerResourceRecord`) on that server. Because it relies on the Windows WS-Management client, the gateway must run on a Windows platform.

- **TXT staging** is additive: a new TXT value is added alongside any existing TXT values at the same name, so co-existing ACME challenges (a wildcard and the apex domain both producing `_acme-challenge` TXT values) coexist.
- **CNAME staging** replaces the record: a CNAME is singular per name by DNS rules, so any existing CNAME at the name is removed before the new one is added.
- **Cleanup** removes the managed record. For TXT, when a specific value is supplied only that value is removed and any co-existing values are preserved; a record that is already absent is treated as clean (success).

The owning zone for a given FQDN is resolved by listing the server's forward-lookup zones and selecting the one whose name is the longest matching suffix of the record name, unless `AD_Zone` overrides it.

> **Authentication mechanism (V1 decision):** V1 uses remote PowerShell over WinRM, which constrains the gateway to a Windows host. A future version may add a native Kerberos DNS-update client (RFC 2136 / GSS-TSIG) to remove the Windows-host requirement — tracked as a follow-up.

## Requirements

### Keyfactor platform

- Keyfactor AnyCA Gateway REST **26.2 or later** (DNS validation support was added in AnyCA Gateway 26.2)
- A gateway product that supports DNS-based domain validation (ACME REST Gateway, DigiCert, Sectigo, SSL Store, etc.)
- **The gateway must run on Windows** (V1 uses the Windows WinRM PowerShell client)

### Microsoft DNS requirements

1. **A Windows Server DNS server** (typically a domain controller) hosting the forward-lookup zone(s) for the domains being validated, with the **DNS Server role** installed. This is what provides the `DnsServer` PowerShell module (`Add-DnsServerResourceRecord`, `Get-DnsServerResourceRecord`, `Remove-DnsServerResourceRecord`, `Get-DnsServerZone`) that the plugin invokes remotely. Confirm the module is present:
   ```powershell
   Get-Module -ListAvailable DnsServer
   ```
   If it's missing even though the DNS role appears installed, reinstall with management tools included:
   ```powershell
   Install-WindowsFeature DNS -IncludeManagementTools
   ```
   **PowerShell version on the DNS server:** the built-in **Windows PowerShell 5.1** WinRM endpoint (the default remoting endpoint on every supported Windows Server release) is all that's required — this is what ships the `DnsServer` module, and no separate PowerShell 7/pwsh install is needed on the DNS server. If the endpoint has been reconfigured to something non-default (a custom PowerShell 7 remoting endpoint, JEA-constrained endpoint, etc.), confirm the `DnsServer` module is actually importable in that session, since a constrained/alternate endpoint may not expose it.

   **PowerShell on the gateway host:** none required. The plugin doesn't shell out to `powershell.exe`/`pwsh` — it uses the bundled `System.Management.Automation` PowerShell SDK (currently v7.4.6, referenced as a NuGet package in the plugin's `.csproj`) to open the WSMan session in-process. The gateway host only needs the .NET runtime the gateway itself requires (see Runtime Requirements below) and outbound WinRM connectivity.

2. **WinRM (WS-Management) enabled** on the DNS server, reachable from the gateway host over TCP **5985** (HTTP, default) or **5986** (HTTPS, when `AD_UseSSL=true`). On the DNS server:
   ```powershell
   Enable-PSRemoting -Force
   Set-NetFirewallRule -Name "WINRM-HTTP-In-TCP" -Enabled True
   ```
   If the gateway host is **not** domain-joined to the same domain as the DNS server, WinRM's default Negotiate authentication requires the target to be trusted explicitly. On the *gateway* host:
   ```powershell
   Set-Item WSMan:\localhost\Client\TrustedHosts -Value "<dns-server>" -Concatenate -Force
   ```
   `TrustedHosts` disables mutual authentication (the client trusts whatever answers at that address) — acceptable on a private/lab network, but don't wildcard it or point it at anything you don't control. Prefer domain-joined hosts and Kerberos, or `AD_UseSSL=true` with a real cert, where possible.

3. **Two separate permission concerns** — both are required, and having one without the other produces different failures:
   - **WinRM session access**: the identity must be allowed to open a remote PowerShell session on the DNS server at all. This normally requires membership in the local **Remote Management Users** group (or local Administrators) on that server. Missing this fails at the WinRM layer with an `Access is denied` error when opening the session — before the plugin ever gets to run a DNS cmdlet.
   - **DNS record management**: the identity must additionally be authorized to manage records on the target zone — membership in **DnsAdmins** (or Domain Admins), or an equivalent delegated ACL on the zone. Missing this lets the WinRM session open successfully, but DNS cmdlets themselves fail with access-denied errors.

   Either the **gateway service account** (via Kerberos/Negotiate, when `AD_Username`/`AD_Password` are left empty — note this is the account the gateway *service* runs as, which may differ from whatever account you're logged in as interactively) or an explicit `AD_Username`/`AD_Password` must satisfy both.

### Configuration fields

| Field | Required | Description |
| --- | --- | --- |
| `AD_DnsServer` | Yes | Hostname or FQDN of the Windows DNS server to manage records on. |
| `AD_Username` | No | WinRM user as `DOMAIN\user` or `user@domain`. Leave empty to use the gateway service account. |
| `AD_Password` | No | Password for `AD_Username`. Stored as a secret. Required only when a username is supplied. |
| `AD_Zone` | No | Explicit forward-lookup zone. Leave empty to resolve automatically by longest suffix match. |
| `AD_UseSSL` | No | `true` to use WinRM over HTTPS (5986); default `false` (HTTP, 5985). |

### Notes and limitations

* V1 is **Windows-only** — the gateway host must be Windows because record management uses the WinRM PowerShell client.
* The plugin sets the record TTL to 60 seconds.
* Zones are discovered from the target server's hosted forward-lookup zones only; a record whose domain is not covered by a hosted zone (and no `AD_Zone` override is set) fails with `No DNS zone hosted on server ... covers record`.
* Each validator type manages only its own record type: `MicrosoftAdDomainValidator` reads/writes `TXT`, `MicrosoftAdCnameDomainValidator` reads/writes `CNAME`. Neither touches other record types.
* The DNS server must have the `DnsServer` PowerShell module available (installed with the DNS Server role).

## Usage

**Testing.** There are three levels of testing, each isolating a different layer of the stack. See [test/README.md](test/README.md) for full details; summarized here:

1. **Infra smoke test** (`test/smoke-test.ps1`) — pure PowerShell, no plugin code. Confirms WinRM reachability, the `DnsServer` module, and the target zone from the machine that will host the gateway.
2. **Provider harness** (`test/ManualTestHarness`) — drives `MicrosoftAdDnsProvider` directly (no gateway, no CA). Exercises TXT create/delete, additive multi-value TXT, targeted delete, CNAME create/delete, and idempotent cleanup against a real DNS server.
3. **Full gateway + CA integration** — a real enrollment through the gateway, a CA, and this plugin together. This is the only level that proves the domain validator is wired up correctly end-to-end (gateway config → CA → DNS-01 challenge → this plugin → DNS server → CA re-check → issuance).

**Testing against an internal-only zone (e.g. Active Directory `.local` / `.corp`).**

Public ACME CAs (Let's Encrypt, Google Trust Services, etc.) **reject internal/non-public zones outright** — the order fails at `CreateOrder` with `rejectedIdentifier` / `"Domain must end in a public suffix"` before DNS validation is ever attempted, because `.local`-style names aren't ICANN-delegated public suffixes. This is a CA-side policy check, not a DNS or plugin problem, and it means a public CA can never be used to test this plugin against an internal AD zone.

To test level 3 against an internal zone (e.g. `command.local`), point the gateway's CA connector at a **private ACME server** instead — [step-ca](https://smallstep.com/docs/step-ca/) works well and doesn't enforce public-suffix rules. See [test/README.md](test/README.md#step-3b--full-gateway-integration-against-an-internal-zone-with-a-private-acme-ca-step-ca) for a full step-ca setup and DNS-resolution troubleshooting walkthrough, including two gotchas that are easy to lose time to:

* The gateway's own DNS-propagation pre-check defaults to public resolvers (8.8.8.8, 1.1.1.1, etc.), which can never see an internal zone. Point it at an internal DNS server via the CA connector's `DnsVerificationServer` setting.
* The ACME server itself (step-ca) does its **own independent** DNS lookup when validating the challenge — it must be able to resolve the internal zone through its own OS-level resolver, entirely separately from whether the gateway or this plugin can. A DNS-01 order can appear to stage and submit correctly and still hang at `pending` forever if the ACME server's host can't resolve the internal zone.
