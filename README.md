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

### How it works (V1)

V1 is **Windows-only**. The plugin manages records by opening a **WinRM remote PowerShell** session from the gateway host to the target DNS server and running the built-in `DnsServer` module cmdlets (`Get-DnsServerZone`, `Add-DnsServerResourceRecord`, `Get-DnsServerResourceRecord`, `Remove-DnsServerResourceRecord`) on that server. Because it relies on the Windows WS-Management client, the gateway must run on a Windows platform.

- **TXT staging** is additive: a new TXT value is added alongside any existing TXT values at the same name, so co-existing ACME challenges (a wildcard and the apex domain both producing `_acme-challenge` TXT values) coexist.
- **CNAME staging** replaces the record: a CNAME is singular per name by DNS rules, so any existing CNAME at the name is removed before the new one is added.
- **Cleanup** removes the managed record. For TXT, when a specific value is supplied only that value is removed and any co-existing values are preserved; a record that is already absent is treated as clean (success).

The owning zone for a given FQDN is resolved by listing the server's forward-lookup zones and selecting the one whose name is the longest matching suffix of the record name, unless `AD_Zone` overrides it.

> **Authentication mechanism (V1 decision):** V1 uses remote PowerShell over WinRM, which constrains the gateway to a Windows host. A future version may add a native Kerberos DNS-update client (RFC 2136 / GSS-TSIG) to remove the Windows-host requirement — tracked as a follow-up.

## Features

- Automated DNS TXT record creation and deletion in Microsoft Active Directory DNS

## Requirements

### Keyfactor Platform
- Keyfactor AnyCA Gateway REST **26.2 or later** (DNS validation support was added in AnyCA Gateway 26.2)
- A gateway product that supports DNS-01 domain validation (ACME REST Gateway, DigiCert, Sectigo, etc.)

### Microsoft Active Directory DNS Requirements

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

### Runtime Requirements
- .NET 10.0 runtime (provided by the gateway server)

## Installation

This plugin is installed alongside any Keyfactor gateway server that supports DNS-01 domain validation (ACME REST Gateway, DigiCert, Sectigo, etc.). The same DLL works with every supported gateway.

> See the official Keyfactor AnyCA Gateway REST installation documentation for the authoritative install instructions: **<TBD link from Sarah Duncan>**. The steps below are a general guide; defer to the official docs if they diverge.

### 1. Download the Plugin

Download the latest release from the [Releases](https://github.com/Keyfactor/microsoft-activedirectory-dnsplugin/releases) page.

### 2. Copy the plugin DLLs to the gateway's Extensions folder

On the server hosting your gateway, unzip the release and copy the contents of the `net10.0` directory into the gateway's `Extensions` folder.

**Windows** (example path — substitute the gateway product folder for your install):

```text
C:\Program Files\Keyfactor\<GatewayName>\AnyGatewayREST\net10.0\Extensions\
```

**Linux**:

```text
/opt/keyfactor/<gateway-name>/AnyGatewayREST/net10.0/Extensions/
```

Replace `<GatewayName>` (or `<gateway-name>` on Linux) with the gateway you are installing into (e.g. `AcmeGwDns`, `DigiCert`, `Sectigo`).

### 3. Restart the gateway service

Restart the AnyGatewayREST Windows service for the gateway you installed the plugin into so the Extensions folder is rescanned.

## Configuration

After installing the plugin DLL into the gateway's Extensions folder, configure a new DNS Provider entry in the AnyCA Gateway REST UI and select **Microsoft Active Directory** as the provider type. See the official Keyfactor AnyCA Gateway REST documentation for the canonical UI walkthrough: **<TBD link from Sarah Duncan>**.

### Configuration Parameters

| Parameter | Description | Required | Example |
|-----------|-------------|----------|---------|
| `AD_DnsServer` | Hostname or FQDN of the Windows DNS server (typically a domain controller), reached over WinRM remote PowerShell. | Yes | ` ` |
| `AD_Username` | User for the WinRM session, as DOMAIN\user or user@domain. Leave empty to use the gateway service account identity (Kerberos/Negotiate). | No | ` ` |
| `AD_Password` | Password for the username. Required only when a username is supplied. Stored as a secret. | No | ` ` |
| `AD_Zone` | Explicit forward-lookup zone to write records into. Leave empty to resolve the zone automatically by longest matching suffix of the record name. | No | ` ` |
| `AD_UseSSL` | Use WinRM over HTTPS (port 5986) instead of HTTP (port 5985). 'true' or 'false' (default 'false'). | No | `false` |

### Example Configuration

```json
{
  "AD_DnsServer": "",
  "AD_Username": "",
  "AD_Password": "",
  "AD_Zone": "",
  "AD_UseSSL": ""
}
```

## Usage

### Automatic Domain Validation

Once configured, the plugin automatically handles DNS validation during certificate enrollment and renewal:

1. **Record Creation**: Plugin creates a DNS TXT record with the validation challenge
2. **Propagation Wait**: Plugin waits for DNS propagation
3. **Verification**: Plugin verifies the record exists on Microsoft Active Directory DNS nameservers
4. **Cleanup**: Plugin deletes the validation record after successful validation

### Zone Discovery

The plugin automatically discovers the appropriate DNS zone for a domain:

- For `www.example.com`, searches for zones: `www.example.com`, `example.com`
- For `sub.example.com`, searches for zones: `sub.example.com`, `example.com`
- For `*.example.com`, searches for zones: `example.com`

## Troubleshooting

### Common Issues

- **Authentication Failures**: Verify Microsoft Active Directory DNS credentials are valid, not expired, and authorized for the target zone.
- **Insufficient Permissions**: Verify the account/role has the documented minimum permissions on the target DNS zone.
- **Zone Not Found**: Verify the target DNS zone exists in your Microsoft Active Directory DNS account and is reachable from the gateway server.
- **DNS Propagation Timeouts**: Check Microsoft Active Directory DNS service health; verify authoritative nameservers are responding.

### Logging

Enable debug logging in the gateway's logging configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Keyfactor.Extensions.DomainValidator.MicrosoftAD": "Debug"
    }
  }
}
```

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
