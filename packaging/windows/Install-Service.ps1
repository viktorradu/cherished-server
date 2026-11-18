#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SettingsPath = (Join-Path $PSScriptRoot 'install-settings.json'),
    [string]$DeploymentRoot = "$env:ProgramData\CherishedServer\WebService",
    [switch]$Elevate
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Service.Common.ps1')
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json -AsHashtable
if ($settings -isnot [Collections.IDictionary] -or
    @($settings.Keys | Where-Object { $_ -notin @('ImageDirectory', 'StateDirectory', 'Port',
        'PoolFilter', 'SlideshowIntervalMs', 'RefreshIntervalMs') }).Count) {
    throw 'Installation settings must contain only ImageDirectory, StateDirectory, Port, PoolFilter, SlideshowIntervalMs and RefreshIntervalMs.'
}
foreach ($key in @('ImageDirectory', 'StateDirectory')) {
    if ($settings[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($settings[$key]) -or
        ![IO.Path]::IsPathFullyQualified($settings[$key])) { throw "Set $key to an absolute path in $SettingsPath." }
}
if (($settings.Port -isnot [int] -and $settings.Port -isnot [long]) -or $settings.Port -lt 1024 -or $settings.Port -gt 65535) {
    throw 'Port must be an integer between 1024 and 65535.'
}
$pool = @{ PoolFilter = '.jpg,.jpeg'; SlideshowIntervalMs = 3000; RefreshIntervalMs = 10000 }
foreach ($key in @('PoolFilter', 'SlideshowIntervalMs', 'RefreshIntervalMs')) {
    if ($settings.ContainsKey($key)) { $pool[$key] = $settings[$key] }
}
Assert-PoolInstallationSettings @pool
& (Join-Path $PSScriptRoot 'deploy-service.ps1') -Action Install -PublishedDirectory (Join-Path $PSScriptRoot 'app') `
    -DeploymentRoot $DeploymentRoot -ImageDirectory $settings.ImageDirectory -StateDirectory $settings.StateDirectory `
    -Port $settings.Port @pool -Elevate:$Elevate -WhatIf:$WhatIfPreference
