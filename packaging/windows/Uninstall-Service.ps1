#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$DeploymentRoot = "$env:ProgramData\CherishedServer\WebService",
    [switch]$Elevate
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'deploy-service.ps1') -Action Remove `
    -DeploymentRoot $DeploymentRoot -Elevate:$Elevate -WhatIf:$WhatIfPreference
