#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$DeploymentRoot = "$env:ProgramData\CherishedServer\WebService",
    [switch]$Elevate
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'deploy-service.ps1') -Action Install -RequireExisting `
    -PublishedDirectory (Join-Path $PSScriptRoot 'app') -DeploymentRoot $DeploymentRoot -Elevate:$Elevate -WhatIf:$WhatIfPreference
