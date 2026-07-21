// Copyright 2025 Keyfactor
// Licensed under the Apache License, Version 2.0
//
// Manual test harness for the Microsoft AD DNS provider. Drives the internal
// MicrosoftAdDnsProvider directly (no gateway required) so you can exercise the
// real WinRM + DnsServer-cmdlet code path against a live Windows DNS server.
//
// This is a LIVE test: it creates and then deletes records in a real zone.
// Point it at a lab/test zone, not production.
//
// Configure with environment variables (see test/README.md), then:
//   dotnet run --project test/ManualTestHarness

using Keyfactor.Extensions.DomainValidator.MicrosoftAD;

static string Env(string name, string fallback = null) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

var dnsServer = Env("AD_DnsServer");
var username = Env("AD_Username");   // optional; empty => gateway/current identity via Negotiate
var password = Env("AD_Password");   // optional
var zone = Env("AD_Zone");           // optional; empty => auto suffix-match
var useSsl = string.Equals(Env("AD_UseSSL", "false"), "true", StringComparison.OrdinalIgnoreCase);

// A record name that must be covered by a hosted zone on the server (or by AD_Zone).
// Default assumes a lab zone "example.test" exists on the server.
var testDomain = Env("TEST_DOMAIN", "example.test");

if (string.IsNullOrWhiteSpace(dnsServer))
{
    Console.Error.WriteLine("AD_DnsServer is required. Set it (and optionally AD_Username/AD_Password/AD_Zone/AD_UseSSL/TEST_DOMAIN).");
    return 2;
}

var txtName = $"_acme-challenge.{testDomain}";
var txtValue = "harness-txt-" + Guid.NewGuid().ToString("N")[..16];
var cnameName = $"_dcv-test.{testDomain}";
var cnameValue = $"{Guid.NewGuid():N}.dcv.example-ca.test";

Console.WriteLine($"DNS server : {dnsServer} (SSL={useSsl})");
Console.WriteLine($"Identity   : {(string.IsNullOrWhiteSpace(username) ? "<current/gateway account>" : username)}");
Console.WriteLine($"Zone       : {(string.IsNullOrWhiteSpace(zone) ? "<auto suffix-match>" : zone)}");
Console.WriteLine($"Test domain: {testDomain}");
Console.WriteLine();

var provider = new MicrosoftAdDnsProvider(dnsServer, username, password, zone, useSsl);
var failures = 0;

async Task Step(string label, Func<Task> action)
{
    Console.Write($"  {label,-42} ");
    try { await action(); Console.WriteLine("OK"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL\n      -> {ex.Message}"); }
}

// 1) Connectivity / permissions: lists zones on the server.
await Step("ValidateConnection (list zones)", () => provider.ValidateConnectionAsync());

// 2) TXT (dns-01) lifecycle.
await Step($"Create TXT {txtName}", () => provider.CreateRecordAsync(txtName, txtValue, "TXT"));
Console.WriteLine($"      value: {txtValue}");
Console.WriteLine($"      verify: nslookup -type=TXT {txtName} {dnsServer}");
await Step("Delete TXT (exact value)", () => provider.DeleteRecordAsync(txtName, txtValue, "TXT"));

// 3) TXT additive behavior: two values coexist at the same name, then targeted delete.
var txtValue2 = "harness-txt-" + Guid.NewGuid().ToString("N")[..16];
await Step("Create TXT value #1", () => provider.CreateRecordAsync(txtName, txtValue, "TXT"));
await Step("Create TXT value #2 (additive)", () => provider.CreateRecordAsync(txtName, txtValue2, "TXT"));
Console.WriteLine($"      expect BOTH via: nslookup -type=TXT {txtName} {dnsServer}");
await Step("Delete only TXT value #1", () => provider.DeleteRecordAsync(txtName, txtValue, "TXT"));
Console.WriteLine($"      expect ONLY #2 remains: {txtValue2}");
await Step("Delete remaining TXT (all)", () => provider.DeleteRecordAsync(txtName, null, "TXT"));

// 4) CNAME (cname DCV) lifecycle.
await Step($"Create CNAME {cnameName}", () => provider.CreateRecordAsync(cnameName, cnameValue, "CNAME"));
Console.WriteLine($"      alias: {cnameValue}");
Console.WriteLine($"      verify: nslookup -type=CNAME {cnameName} {dnsServer}");
await Step("Delete CNAME", () => provider.DeleteRecordAsync(cnameName, null, "CNAME"));

// 5) Idempotent cleanup: deleting a non-existent record is treated as success.
await Step("Delete missing TXT (idempotent)", () => provider.DeleteRecordAsync($"_nope.{testDomain}", null, "TXT"));

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL STEPS PASSED" : $"{failures} STEP(S) FAILED");
return failures == 0 ? 0 : 1;
