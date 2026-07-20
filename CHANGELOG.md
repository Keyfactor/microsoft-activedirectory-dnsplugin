1.0.0
* Initial release of the Microsoft Active Directory DNS plugin for Keyfactor AnyCA Gateway domain validation
* Implements `IDomainValidator` against Microsoft Windows Server DNS via the built-in `DnsServer` PowerShell cmdlets, run on the target server over WinRM remote PowerShell (Windows-only)
* Ships two validator types: `MicrosoftAdDomainValidator` (dns-01 / TXT) and `MicrosoftAdCnameDomainValidator` (cname / CNAME)
* Authenticates with an explicit WinRM username/password or the gateway service account identity (Kerberos/Negotiate); optional WinRM over HTTPS
* TXT staging preserves co-existing values at the same name (wildcard + apex); CNAME staging replaces the singular record
* Cleanup removes the managed record (for TXT, only the matching value when supplied), treating missing records as already clean
* Zone resolution by longest matching hosted forward-lookup zone suffix, with an optional explicit `AD_Zone` override
