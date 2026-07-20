// Copyright 2025 Keyfactor
// Licensed under the Apache License, Version 2.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Threading.Tasks;
using Keyfactor.Logging;
using Microsoft.Extensions.Logging;

namespace Keyfactor.Extensions.DomainValidator.MicrosoftAD
{
    /// <summary>
    /// Microsoft Windows Server DNS (Active Directory-integrated / traditional) provider.
    /// Publishes and removes the DNS records the gateway's domain validation framework asks for
    /// (TXT for ACME dns-01, CNAME for CNAME-based DCV) by running the built-in <c>DnsServer</c>
    /// PowerShell module cmdlets against the target DNS server over WinRM remote PowerShell.
    ///
    /// V1 is Windows-only: the gateway host must be able to open a WinRM (WS-Management) session to
    /// the DNS server, and the DNS server (typically a domain controller) must have the DnsServer
    /// module available (it ships with the DNS Server role). Zones are resolved from the server's
    /// hosted zones by longest DNS-name suffix match unless an explicit zone override is configured.
    /// </summary>
    internal class MicrosoftAdDnsProvider
    {
        private static readonly ILogger _logger = LogHandler.GetClassLogger<MicrosoftAdDnsProvider>();
        private static readonly StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        private const int DefaultTtlSeconds = 60;

        private readonly string _dnsServer;
        private readonly string _zoneOverride;
        private readonly bool _useSsl;
        private readonly PSCredential _credential;

        public MicrosoftAdDnsProvider(string dnsServer, string username, string password, string zoneOverride, bool useSsl)
        {
            if (string.IsNullOrWhiteSpace(dnsServer)) throw new ArgumentNullException(nameof(dnsServer));
            _dnsServer = dnsServer.Trim();
            _zoneOverride = string.IsNullOrWhiteSpace(zoneOverride) ? null : zoneOverride.Trim();
            _useSsl = useSsl;
            _credential = BuildCredential(username, password);
        }

        /// <summary>
        /// Creates a validation record. TXT records are additive — a new value is added alongside any
        /// existing TXT values at the same name so co-existing ACME challenges (wildcard + apex) survive.
        /// A CNAME is singular per name, so any existing CNAME at the name is removed first (replace).
        /// </summary>
        public Task<bool> CreateRecordAsync(string recordName, string recordValue, string recordType = "TXT")
        {
            _logger.LogDebug("Creating {RecordType} record for {RecordName} on {Server}", recordType, recordName, _dnsServer);

            return Task.Run(() =>
            {
                using var rs = OpenRunspace();
                var zone = ResolveZone(rs, recordName);
                var rel = ToRelativeName(recordName, zone);
                var isCname = string.Equals(recordType, "CNAME", OIC);

                if (isCname)
                {
                    // CNAME is singular per name; clear any existing before adding.
                    RemoveRecords(rs, zone, rel, "CName", value: null, swallowMissing: true);

                    Run(rs, $"add CNAME {recordName}", ps => ps
                        .AddCommand("Add-DnsServerResourceRecord")
                        .AddParameter("ZoneName", zone)
                        .AddParameter("Name", rel)
                        .AddParameter("CName", true)
                        .AddParameter("HostNameAlias", EnsureTrailingDot(recordValue))
                        .AddParameter("TimeToLive", TimeSpan.FromSeconds(DefaultTtlSeconds)));
                }
                else
                {
                    Run(rs, $"add TXT {recordName}", ps => ps
                        .AddCommand("Add-DnsServerResourceRecord")
                        .AddParameter("ZoneName", zone)
                        .AddParameter("Name", rel)
                        .AddParameter("Txt", true)
                        .AddParameter("DescriptiveText", recordValue)
                        .AddParameter("TimeToLive", TimeSpan.FromSeconds(DefaultTtlSeconds)));
                }

                _logger.LogInformation(
                    "Created {RecordType} record '{RecordName}' (name '{Rel}') in zone '{Zone}' on DNS server '{Server}'",
                    recordType, recordName, rel, zone, _dnsServer);
                return true;
            });
        }

        /// <summary>
        /// Removes a validation record. When <paramref name="recordValue"/> is supplied for a TXT record,
        /// only that value is removed (co-existing values preserved); otherwise every record of the given
        /// type at the name is removed. Missing records are treated as already-clean (success).
        /// </summary>
        public Task<bool> DeleteRecordAsync(string recordName, string recordValue = null, string recordType = "TXT")
        {
            _logger.LogDebug("Deleting {RecordType} record for {RecordName} on {Server}", recordType, recordName, _dnsServer);

            return Task.Run(() =>
            {
                using var rs = OpenRunspace();
                var zone = ResolveZone(rs, recordName);
                var rel = ToRelativeName(recordName, zone);
                var rrType = string.Equals(recordType, "CNAME", OIC) ? "CName" : "Txt";

                RemoveRecords(rs, zone, rel, rrType, string.Equals(rrType, "Txt", OIC) ? recordValue : null, swallowMissing: true);

                _logger.LogInformation(
                    "Deleted {RecordType} record '{RecordName}' (name '{Rel}') from zone '{Zone}' on DNS server '{Server}'",
                    recordType, recordName, rel, zone, _dnsServer);
                return true;
            });
        }

        /// <summary>Verifies connectivity/permissions by listing the server's zones. Throws on failure.</summary>
        public Task ValidateConnectionAsync()
        {
            return Task.Run(() =>
            {
                using var rs = OpenRunspace();
                Run(rs, "list DNS zones (connection test)", ps => ps.AddCommand("Get-DnsServerZone"));
            });
        }

        private void RemoveRecords(Runspace rs, string zone, string rel, string rrType, string value, bool swallowMissing)
        {
            try
            {
                if (!string.IsNullOrEmpty(value) && string.Equals(rrType, "Txt", OIC))
                {
                    // Remove only the matching TXT value; preserve any co-existing values at the same name.
                    Run(rs, $"remove TXT value at '{rel}' in '{zone}'", ps => ps
                        .AddCommand("Remove-DnsServerResourceRecord")
                        .AddParameter("ZoneName", zone)
                        .AddParameter("Name", rel)
                        .AddParameter("RRType", "Txt")
                        .AddParameter("RecordData", value)
                        .AddParameter("Force", true)
                        .AddParameter("Confirm", false));
                }
                else
                {
                    // Remove every record of this type at the name: Get-... | Remove-... (pipeline is remoting-safe).
                    Run(rs, $"remove {rrType} record(s) at '{rel}' in '{zone}'", ps =>
                    {
                        ps.AddCommand("Get-DnsServerResourceRecord")
                            .AddParameter("ZoneName", zone)
                            .AddParameter("Name", rel)
                            .AddParameter("RRType", rrType)
                            .AddParameter("ErrorAction", "SilentlyContinue");
                        ps.AddCommand("Remove-DnsServerResourceRecord")
                            .AddParameter("ZoneName", zone)
                            .AddParameter("Force", true)
                            .AddParameter("Confirm", false);
                    });
                }
            }
            catch (Exception ex) when (swallowMissing && IsNotFound(ex))
            {
                _logger.LogInformation(
                    "No matching {RRType} record to remove at '{Rel}' in zone '{Zone}'; treating cleanup as complete",
                    rrType, rel, zone);
            }
        }

        /// <summary>Resolves the hosted zone that owns a record by longest matching DNS-name suffix.</summary>
        private string ResolveZone(Runspace rs, string recordName)
        {
            if (_zoneOverride != null) return _zoneOverride.TrimEnd('.');
            if (string.IsNullOrWhiteSpace(recordName))
                throw new ArgumentException("Record name is required to resolve a DNS zone", nameof(recordName));

            var clean = recordName.TrimEnd('.');
            var zones = Run(rs, "list DNS zones", ps => ps.AddCommand("Get-DnsServerZone"));

            var names = zones
                .Select(z => z.Properties["ZoneName"]?.Value?.ToString()?.TrimEnd('.'))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            var match = names
                .Where(zn => clean.Equals(zn, OIC) || clean.EndsWith("." + zn, OIC))
                .OrderByDescending(zn => zn.Length)
                .FirstOrDefault();

            if (match == null)
            {
                _logger.LogError(
                    "No DNS zone hosted on server '{Server}' covers record '{RecordName}'. Hosted zones: {Zones}",
                    _dnsServer, recordName, string.Join(", ", names));

                throw new InvalidOperationException(
                    $"No DNS zone hosted on server '{_dnsServer}' covers record '{recordName}'. " +
                    "Ensure a forward lookup zone whose name is a suffix of the record exists on the server, " +
                    "or set AD_Zone to the target zone explicitly.");
            }

            _logger.LogDebug("Resolved DNS zone '{Zone}' for record '{RecordName}'", match, recordName);
            return match;
        }

        private Runspace OpenRunspace()
        {
            var connInfo = new WSManConnectionInfo
            {
                ComputerName = _dnsServer,
                Port = _useSsl ? 5986 : 5985,
                Scheme = _useSsl ? "https" : "http",
                ShellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                AuthenticationMechanism = AuthenticationMechanism.Negotiate,
                OpenTimeout = 3 * 60 * 1000,
                OperationTimeout = 3 * 60 * 1000
            };
            if (_credential != null) connInfo.Credential = _credential;

            try
            {
                var rs = RunspaceFactory.CreateRunspace(connInfo);
                rs.Open();
                return rs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open a WinRM PowerShell session to DNS server '{Server}'", _dnsServer);
                throw new InvalidOperationException(
                    $"Failed to open a remote PowerShell (WinRM) session to DNS server '{_dnsServer}' on port " +
                    $"{(_useSsl ? 5986 : 5985)}: {ex.Message}. Verify WinRM is enabled on the DNS server, the gateway " +
                    "host can reach it, and the supplied credentials (or the gateway service account) have permission " +
                    "to manage DNS.", ex);
            }
        }

        private static Collection<PSObject> Run(Runspace rs, string op, Action<PowerShell> build)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            build(ps);

            var results = ps.Invoke();
            if (ps.HadErrors && ps.Streams.Error.Count > 0)
            {
                var msg = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                throw new InvalidOperationException($"Microsoft DNS operation failed ({op}): {msg}");
            }
            return results;
        }

        private static bool IsNotFound(Exception ex)
            => ex.Message.IndexOf("not found", OIC) >= 0
               || ex.Message.IndexOf("does not exist", OIC) >= 0
               || ex.Message.IndexOf("was not found", OIC) >= 0;

        private static PSCredential BuildCredential(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            var secure = new SecureString();
            foreach (var c in password ?? string.Empty) secure.AppendChar(c);
            secure.MakeReadOnly();
            return new PSCredential(username, secure);
        }

        /// <summary>Computes the record's owner name relative to its zone (e.g. "_acme-challenge.www", or "@" for the apex).</summary>
        private static string ToRelativeName(string recordName, string zone)
        {
            var clean = recordName.TrimEnd('.');
            var z = zone.TrimEnd('.');
            if (clean.Equals(z, OIC)) return "@";
            if (clean.EndsWith("." + z, OIC)) return clean.Substring(0, clean.Length - z.Length - 1);
            return clean;
        }

        private static string EnsureTrailingDot(string name)
            => string.IsNullOrEmpty(name) ? name : (name.EndsWith(".") ? name : name + ".");
    }
}
