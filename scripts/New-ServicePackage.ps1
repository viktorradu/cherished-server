#requires -Version 7.0
[CmdletBinding()]
param(
    [Alias('PackageVersion')]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string]$Version = '1.0.0',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [Alias('RuntimeVersion')]
    [ValidatePattern('^11\.0\.\d+$')]
    [string]$RuntimeFrameworkVersion,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$arguments = @{ PackageVersion = $Version; OutputDirectory = $OutputDirectory; Force = $Force }
if ($RuntimeFrameworkVersion) { $arguments.RuntimeVersion = $RuntimeFrameworkVersion }
& (Join-Path $PSScriptRoot '..\package-service.ps1') @arguments
