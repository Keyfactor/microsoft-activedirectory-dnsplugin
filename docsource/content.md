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

1. A Windows Server DNS server (typically a domain controller) hosting the forward-lookup zone(s) for the domains being validated, with the **DNS Server role** installed (this provides the `DnsServer` PowerShell module the plugin invokes on the server).
2. **WinRM (WS-Management) enabled** on the DNS server and reachable from the gateway host (TCP 5985 for HTTP, 5986 for HTTPS).
3. An identity with permission to manage DNS records on the server — either the gateway service account (via Kerberos/Negotiate, when no credentials are configured) or an explicit `AD_Username` / `AD_Password`. The account must be a member of **DnsAdmins** (or Domain Admins) or otherwise delegated DNS record management on the target zone.

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
