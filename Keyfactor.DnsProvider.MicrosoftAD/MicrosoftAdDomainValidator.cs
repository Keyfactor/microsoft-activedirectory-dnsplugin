// Copyright 2025 Keyfactor
// Licensed under the Apache License, Version 2.0
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Keyfactor.AnyGateway.Extensions;
using Keyfactor.Logging;
using Microsoft.Extensions.Logging;

namespace Keyfactor.Extensions.DomainValidator.MicrosoftAD
{
    /// <summary>
    /// Shared Microsoft Windows Server DNS domain-validation logic. Concrete subclasses fix the
    /// validation type (e.g. "dns-01" → TXT, "cname" → CNAME) so a single plugin DLL can serve
    /// multiple validation flows (ACME DNS-01, CNAME-based DCV such as CSC Global / SSL Store, etc.).
    ///
    /// V1 targets traditional / Active Directory-integrated Windows DNS and manages records over
    /// WinRM remote PowerShell — the gateway must run on Windows and be able to reach the DNS server.
    /// </summary>
    public abstract class MicrosoftAdDomainValidatorBase : IDomainValidator
    {
        private static readonly ILogger _logger = LogHandler.GetClassLogger<MicrosoftAdDomainValidatorBase>();

        private MicrosoftAdDnsProvider _provider;
        private Dictionary<string, object> _configuration;

        /// <summary>The validation type this validator advertises to the gateway framework.</summary>
        protected abstract string ValidationType { get; }

        /// <summary>The DNS record type this validator publishes (e.g. "TXT" or "CNAME").</summary>
        protected abstract string RecordType { get; }

        public Dictionary<string, PropertyConfigInfo> GetDomainValidatorAnnotations()
        {
            return new Dictionary<string, PropertyConfigInfo>()
            {
                ["AD_DnsServer"] = new PropertyConfigInfo()
                {
                    Comments = "Hostname or FQDN of the Windows DNS server (typically a domain controller) to manage " +
                               "records on, reached over WinRM remote PowerShell (Required).",
                    Hidden = false,
                    DefaultValue = "",
                    Type = "String"
                },
                ["AD_Username"] = new PropertyConfigInfo()
                {
                    Comments = "User for the WinRM session, as DOMAIN\\user or user@domain. Optional — leave empty to " +
                               "use the gateway service account's identity (Kerberos/Negotiate).",
                    Hidden = false,
                    DefaultValue = "",
                    Type = "String"
                },
                ["AD_Password"] = new PropertyConfigInfo()
                {
                    Comments = "Password for AD_Username. Optional — required only when AD_Username is set. Stored as a secret.",
                    Hidden = true,
                    DefaultValue = "",
                    Type = "Secret"
                },
                ["AD_Zone"] = new PropertyConfigInfo()
                {
                    Comments = "Explicit forward-lookup zone to write records into. Optional — leave empty to resolve the " +
                               "zone automatically by longest matching suffix of the record name.",
                    Hidden = false,
                    DefaultValue = "",
                    Type = "String"
                },
                ["AD_UseSSL"] = new PropertyConfigInfo()
                {
                    Comments = "Use WinRM over HTTPS (port 5986) instead of HTTP (port 5985). Optional — 'true' or 'false' " +
                               "(default 'false').",
                    Hidden = false,
                    DefaultValue = "false",
                    Type = "String"
                }
            };
        }

        public string GetValidationType()
        {
            return ValidationType;
        }

        public void Initialize(IDomainValidatorConfigProvider configProvider)
        {
            _configuration = configProvider.DomainValidationConfiguration;

            var server = GetConfigValue("AD_DnsServer");
            if (string.IsNullOrWhiteSpace(server))
                throw new ArgumentException("AD_DnsServer is required");

            _provider = new MicrosoftAdDnsProvider(
                server,
                GetConfigValue("AD_Username"),
                GetConfigValue("AD_Password"),
                GetConfigValue("AD_Zone"),
                ParseBool(GetConfigValue("AD_UseSSL")));
        }

        public async Task<DomainValidationResult> StageValidation(string key, string value, CancellationToken cancellationToken)
        {
            try
            {
                var success = await _provider.CreateRecordAsync(key, value, RecordType);

                return new DomainValidationResult
                {
                    Success = success,
                    ErrorMessage = success ? null : $"Failed to create DNS {RecordType} record for {key}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Microsoft DNS StageValidation failed for {RecordType} record '{Key}'", RecordType, key);
                return new DomainValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to create {RecordType} record for {key}: {ex.Message}"
                };
            }
        }

        public async Task<DomainValidationResult> CleanupValidation(string key, CancellationToken cancellationToken)
        {
            try
            {
                var success = await _provider.DeleteRecordAsync(key, recordType: RecordType);

                return new DomainValidationResult
                {
                    Success = success,
                    ErrorMessage = success ? null : $"Failed to delete DNS {RecordType} record for {key}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Microsoft DNS CleanupValidation failed for {RecordType} record '{Key}'", RecordType, key);
                return new DomainValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to delete {RecordType} record for {key}: {ex.Message}"
                };
            }
        }

        public async Task ValidateConfiguration(Dictionary<string, object> configuration)
        {
            _configuration = configuration;

            if (string.IsNullOrWhiteSpace(GetConfigValue("AD_DnsServer")))
                throw new ArgumentException("AD_DnsServer is required");

            await Task.CompletedTask;
        }

        private string GetConfigValue(string key)
        {
            if (_configuration != null && _configuration.TryGetValue(key, out var value))
                return value?.ToString() ?? string.Empty;
            return string.Empty;
        }

        private static bool ParseBool(string value)
            => !string.IsNullOrWhiteSpace(value)
               && (value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Trim() == "1"
                   || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Microsoft Windows Server DNS validator for ACME DNS-01 challenges. Publishes TXT records.
    /// </summary>
    public class MicrosoftAdDomainValidator : MicrosoftAdDomainValidatorBase
    {
        protected override string ValidationType => "dns-01";
        protected override string RecordType => "TXT";
    }

    /// <summary>
    /// Microsoft Windows Server DNS validator for CNAME-based Domain Control Validation
    /// (e.g. CSC Global, SSL Store). Publishes CNAME records.
    /// </summary>
    public class MicrosoftAdCnameDomainValidator : MicrosoftAdDomainValidatorBase
    {
        protected override string ValidationType => "cname";
        protected override string RecordType => "CNAME";
    }
}
