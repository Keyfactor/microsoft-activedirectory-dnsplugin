# Copyright 2025 Keyfactor
# Licensed under the Apache License, Version 2.0
#
# Pre-flight smoke test for the Microsoft AD DNS provider.
# Verifies the SAME environment the plugin needs - WinRM remote PowerShell to the
# DNS server plus the DnsServer module cmdlets - WITHOUT the plugin or the gateway.
# Run this from the machine that will host the gateway (or your dev box) BEFORE the
# .NET harness, so you can tell infrastructure problems apart from code problems.
#
# Example:
#   .\smoke-test.ps1 -DnsServer dc01.corp.example.test -Zone example.test
#   .\smoke-test.ps1 -DnsServer dc01 -Zone example.test -Credential (Get-Credential)

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $DnsServer,
    [Parameter(Mandatory = $true)] [string] $Zone,
    [System.Management.Automation.PSCredential] $Credential,
    [switch] $UseSsl
)

$ErrorActionPreference = 'Stop'
$sessionArgs = @{ ComputerName = $DnsServer }
if ($Credential) { $sessionArgs.Credential = $Credential }
if ($UseSsl)     { $sessionArgs.UseSSL = $true }

Write-Host "1. Opening WinRM session to $DnsServer ..." -NoNewline
$session = New-PSSession @sessionArgs
Write-Host " OK"

try {
    Write-Host "2. DnsServer module present on target ..." -NoNewline
    $hasModule = Invoke-Command -Session $session { [bool](Get-Module -ListAvailable DnsServer) }
    if (-not $hasModule) { throw "DnsServer module NOT found - install the DNS Server role/RSAT on $DnsServer." }
    Write-Host " OK"

    Write-Host "3. Zone '$Zone' is hosted on target ..." -NoNewline
    $zoneOk = Invoke-Command -Session $session -ArgumentList $Zone {
        param($z) [bool](Get-DnsServerZone -Name $z -ErrorAction SilentlyContinue)
    }
    if (-not $zoneOk) { throw "Zone '$Zone' is not hosted on $DnsServer." }
    Write-Host " OK"

    $rel = "_smoketest"
    $val = "smoke-$([guid]::NewGuid().ToString('N').Substring(0,12))"

    Write-Host "4. Add TXT $rel.$Zone ..." -NoNewline
    Invoke-Command -Session $session -ArgumentList $Zone, $rel, $val {
        param($z, $r, $v)
        Add-DnsServerResourceRecord -ZoneName $z -Name $r -Txt -DescriptiveText $v -TimeToLive ([TimeSpan]::FromSeconds(60))
    }
    Write-Host " OK"

    Write-Host "5. Read it back ..." -NoNewline
    $read = Invoke-Command -Session $session -ArgumentList $Zone, $rel {
        param($z, $r)
        (Get-DnsServerResourceRecord -ZoneName $z -Name $r -RRType Txt).RecordData.DescriptiveText
    }
    if ($read -notcontains $val) { throw "TXT value not found after add (got: $read)." }
    Write-Host " OK ($read)"

    Write-Host "6. Remove TXT ..." -NoNewline
    Invoke-Command -Session $session -ArgumentList $Zone, $rel, $val {
        param($z, $r, $v)
        Remove-DnsServerResourceRecord -ZoneName $z -Name $r -RRType Txt -RecordData $v -Force -Confirm:$false
    }
    Write-Host " OK"

    Write-Host "`nSMOKE TEST PASSED - the plugin's environment prerequisites are satisfied." -ForegroundColor Green
}
finally {
    Remove-PSSession $session
}
