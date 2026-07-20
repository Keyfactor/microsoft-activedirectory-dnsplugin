<h1 align="center" style="border-bottom: none">
    Microsoft Active Directory DNS Provider
</h1>

<p align="center">
  <!-- Badges -->
<img src="https://img.shields.io/badge/integration_status-pilot-3D1973?style=flat-square" alt="Integration Status: pilot" />
<a href="https://github.com/Keyfactor/microsoft-activedirectory-dnsplugin/actions/workflows/keyfactor-starter-workflow.yml"><img src="https://github.com/Keyfactor/microsoft-activedirectory-dnsplugin/actions/workflows/keyfactor-starter-workflow.yml/badge.svg" alt="Build" /></a>
<a href="https://github.com/Keyfactor/microsoft-activedirectory-dnsplugin/releases"><img src="https://img.shields.io/github/v/release/Keyfactor/microsoft-activedirectory-dnsplugin?style=flat-square" alt="Release" /></a>
<img src="https://img.shields.io/github/issues/Keyfactor/microsoft-activedirectory-dnsplugin?style=flat-square" alt="Issues" />
<img src="https://img.shields.io/github/downloads/Keyfactor/microsoft-activedirectory-dnsplugin/total?style=flat-square&label=downloads&color=28B905" alt="GitHub Downloads (all assets, all releases)" />
</p>

<p align="center">
  <!-- TOC -->
  <a href="#support">
    <b>Support</b>
  </a>
  ·
  <a href="#requirements">
    <b>Requirements</b>
  </a>
  ·
  <a href="#installation">
    <b>Installation</b>
  </a>
  ·
  <a href="#license">
    <b>License</b>
  </a>
  ·
  <a href="https://github.com/orgs/Keyfactor/repositories?q=dnsplugin">
    <b>Related Integrations</b>
  </a>
</p>

## Overview

The Microsoft Active Directory DNS Plugin is a Keyfactor Domain Validator implementation that manages DNS records on a Microsoft Windows Server DNS server (traditional or Active Directory-integrated). It plugs into the Keyfactor AnyCA Gateway as an `IDomainValidator` and is invoked automatically during certificate enrollment when DNS-based domain control validation is required.

The DLL ships **two validator types**:

| Validator class | Validation type | Record published | Use case |
| --- | --- | --- | --- |
| `MicrosoftAdDomainValidator` | `dns-01` | TXT | ACME DNS-01 challenges |
| `MicrosoftAdCnameDomainValidator` | `cname` | CNAME | CNAME-based DCV (e.g. CSC Global, SSL Store) |

When configuring a Domain Validation Configuration in the gateway UI, pick the validator type that matches the CA's requirement — TXT/`dns-01` for ACME, CNAME/`cname` for CAs that validate via CNAME. Both share the same connection and configuration fields.

**V1 is Windows-only.** Records are managed by opening a WinRM remote PowerShell session from the gateway host to the target DNS server and running the built-in `DnsServer` module cmdlets there. For TXT staging, existing values at the same name are preserved and the new value is appended, so co-existing ACME challenges (a wildcard and the apex domain both producing `_acme-challenge` TXT values) coexist. For CNAME staging, the record is replaced since a CNAME is singular per name. The owning zone for an FQDN is resolved by selecting the hosted forward-lookup zone whose name is the longest matching suffix of the record name (unless `AD_Zone` overrides it).

## Features

- Automated DNS TXT (dns-01) and CNAME (cname) record creation and deletion on Microsoft Windows Server DNS
- Runs the built-in `DnsServer` PowerShell cmdlets on the target server over WinRM remote PowerShell
- Authenticates with an explicit WinRM credential or the gateway service account identity (Kerberos/Negotiate); optional WinRM over HTTPS
- Automatic hosted-zone discovery by longest DNS-name suffix match, with an optional explicit zone override

## Requirements

### Keyfactor Platform
- Keyfactor AnyCA Gateway REST **26.2 or later** (DNS validation support was added in AnyCA Gateway 26.2)
- A gateway product that supports DNS-based domain validation (ACME REST Gateway, DigiCert, Sectigo, SSL Store, etc.)
- **The gateway must run on Windows** (V1 uses the Windows WinRM PowerShell client)

### Microsoft DNS Requirements

1. A Windows Server DNS server (typically a domain controller) hosting the forward-lookup zone(s) for the domains being validated, with the **DNS Server role** installed (provides the `DnsServer` PowerShell module the plugin invokes).
2. **WinRM (WS-Management) enabled** on the DNS server and reachable from the gateway host (TCP 5985 for HTTP, 5986 for HTTPS).
3. An identity with permission to manage DNS records — the gateway service account (via Kerberos/Negotiate, when no credentials are configured) or an explicit `AD_Username` / `AD_Password`. The account must be in **DnsAdmins** (or Domain Admins) or otherwise delegated DNS record management on the target zone.

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
* Zones are discovered from the target server's hosted forward-lookup zones only; a record not covered by a hosted zone (with no `AD_Zone` override) fails with `No DNS zone hosted on server ... covers record`.
* Each validator type manages only its own record type: `MicrosoftAdDomainValidator` reads/writes `TXT`, `MicrosoftAdCnameDomainValidator` reads/writes `CNAME`.

### Runtime Requirements
- .NET 8 runtime on a Windows gateway host (V1 targets `net8.0-windows`)

## Installation

This plugin is installed alongside any Keyfactor gateway server that supports DNS-based domain validation (ACME REST Gateway, DigiCert, Sectigo, SSL Store, etc.). The same DLL works with every supported gateway.

> See the official Keyfactor AnyCA Gateway REST installation documentation for the authoritative install instructions. The steps below are a general guide; defer to the official docs if they diverge.

### 1. Download the Plugin

Download the latest release from the [Releases](https://github.com/Keyfactor/microsoft-activedirectory-dnsplugin/releases) page.

### 2. Copy the plugin DLLs to the gateway's Extensions folder

On the Windows server hosting your gateway, unzip the release and copy the contents of the `net8.0` framework directory into the gateway's `Extensions` folder:

```text
C:\Program Files\Keyfactor\<GatewayName>\AnyGatewayREST\net8.0\Extensions\
```

Replace `<GatewayName>` with the gateway you are installing into (e.g. `AcmeGwDns`, `DigiCert`, `Sectigo`, `SslStoreGw`).

### 3. Restart the gateway service

Restart the AnyGatewayREST service for the gateway you installed the plugin into so the Extensions folder is rescanned.

## Configuration

After installing the plugin DLL into the gateway's Extensions folder, configure a new Domain Validation entry in the AnyCA Gateway REST UI and select **Microsoft Active Directory DNS** (`MicrosoftAdDomainValidator` for TXT/dns-01, or `MicrosoftAdCnameDomainValidator` for CNAME) as the provider type, then map it to the domain(s) it should manage.

### Configuration Parameters

| Parameter | Description | Required | Example |
|-----------|-------------|----------|---------|
| `AD_DnsServer` | Hostname or FQDN of the Windows DNS server. | Yes | `dc01.corp.example.com` |
| `AD_Username` | WinRM user (`DOMAIN\user` or `user@domain`). | No | `CORP\svc-keyfactor` |
| `AD_Password` | Password for the username (stored as a secret). | No | ` ` |
| `AD_Zone` | Explicit forward-lookup zone override. | No | `example.com` |
| `AD_UseSSL` | Use WinRM over HTTPS (5986). | No | `false` |

### Example Configuration

```json
{
  "AD_DnsServer": "dc01.corp.example.com",
  "AD_Username": "CORP\\svc-keyfactor",
  "AD_Password": "",
  "AD_Zone": "",
  "AD_UseSSL": "false"
}
```

## Usage

### Automatic Domain Validation

Once configured, the plugin automatically handles DNS validation during certificate enrollment and renewal:

1. **Record Creation**: the gateway calls the plugin to publish the validation record (TXT or CNAME) in the appropriate hosted zone on the DNS server.
2. **Validation**: the CA verifies the record and issues the certificate.
3. **Cleanup**: the gateway calls the plugin to remove the validation record once the order is issued.

### Zone Discovery

The plugin discovers the hosted zone for a domain by longest matching forward-lookup zone suffix on the target server:

- For `_acme-challenge.www.example.com`, a hosted zone for `example.com` is matched.
- For `*.example.com`, the `example.com` zone is matched.

Set `AD_Zone` to bypass discovery and write directly to a named zone.

## Troubleshooting

### Common Issues

- **WinRM connection failures**: verify WinRM is enabled on the DNS server (`Enable-PSRemoting`), the gateway host can reach TCP 5985/5986, and — for cross-domain or IP-address targets — that the target is in the gateway's WinRM `TrustedHosts` or HTTPS with a valid certificate is used.
- **Authentication/permission failures**: confirm the configured credential (or gateway service account) is in **DnsAdmins**/Domain Admins or is delegated DNS management on the zone.
- **Zone Not Found**: verify the target domain is covered by a forward-lookup zone hosted on `AD_DnsServer`, or set `AD_Zone` explicitly.
- **`DnsServer` module missing**: ensure the target server has the DNS Server role (which provides the `DnsServer` PowerShell module).

## Support

The Microsoft Active Directory DNS Provider plugin is open source and there is **no SLA**. Keyfactor will address issues as resources become available. Keyfactor customers may request escalation by opening a support ticket through their Keyfactor representative.

### Resources

- [Report Issues](https://github.com/Keyfactor/microsoft-activedirectory-dnsplugin/issues)
- [Discussions](https://github.com/Keyfactor/microsoft-activedirectory-dnsplugin/discussions)

> To report a problem or suggest a new feature, use the **[Issues](../../issues)** tab. If you want to contribute actual bug fixes or proposed enhancements, use the **[Pull requests](../../pulls)** tab.

## License

Apache License 2.0, see [LICENSE](LICENSE).

## Related Integrations

See all [Keyfactor DNS Provider plugins](https://github.com/orgs/Keyfactor/repositories?q=dnsplugin).
