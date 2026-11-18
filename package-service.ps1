#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string]$PackageVersion = [DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [ValidatePattern('^11\.0\.\d+$')]
    [string]$RuntimeVersion,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$fileName = "CherishedServer-WebService-$PackageVersion-win-x64.zip"
$outputPath = Join-Path $OutputDirectory $fileName
if ((Test-Path -LiteralPath $outputPath) -and !$Force) { throw "Package exists: $outputPath. Use another -PackageVersion or -Force." }
if (Test-Path -LiteralPath $outputPath -PathType Container) { throw "Output is a directory: $outputPath" }
if (!$RuntimeVersion) {
    $releases = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json'
    $RuntimeVersion = $releases.'latest-runtime'
    if ($RuntimeVersion -notmatch '^11\.0\.\d+$') { throw 'Cannot determine a stable .NET 11 runtime. Supply -RuntimeVersion.' }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('cherished-server-package-' + [Guid]::NewGuid().ToString('N'))
try {
    $root = Join-Path $temporary 'package'
    $app = Join-Path $root 'app'
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    & dotnet publish (Join-Path $PSScriptRoot 'cherished-server.csproj') --configuration Release `
        --runtime win-x64 --self-contained true --output $app --nologo --verbosity quiet `
        '-p:WindowsServicePublish=true' "-p:RuntimeFrameworkVersion=$RuntimeVersion" `
        '-p:UseAppHost=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:CopyOutputSymbolsToPublishDirectory=false' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Service publish failed ($LASTEXITCODE)." }
    foreach ($required in @(
        'cherished-server.exe', 'cherished-server.dll', 'cherished-server.deps.json', 'cherished-server.runtimeconfig.json',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Microsoft.AspNetCore.dll',
        'Microsoft.Extensions.Hosting.WindowsServices.dll', 'wwwroot\js\main.js', 'wwwroot\css\site.css'
    )) {
        if (!(Test-Path -LiteralPath (Join-Path $app $required) -PathType Leaf)) { throw "Publication is missing $required." }
    }
    $unexpected = @(Get-ChildItem -LiteralPath $app -File -Recurse -Force | Where-Object {
        $_.Name -match '^(appsettings.*\.json|service-settings\.json|deployment\.json|\.hidden|\.flagged|\.env.*)$' -or
        $_.Extension -in @('.pdb', '.cs', '.csproj', '.ps1', '.log', '.db', '.zip') -or
        [IO.Path]::GetRelativePath($app, $_.FullName) -match '(^|\\)(tests|packaging|deployment|scripts|obj|bin|data|logs)\\'
    })
    if ($unexpected.Count) { throw "Publication contains local configuration, state, symbols or source: $($unexpected.Name -join ', ')" }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $app 'cherished-server.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.tfm -ne 'net11.0') { throw 'Publication must target net11.0.' }
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        if (@($runtimeConfig.runtimeOptions.includedFrameworks |
            Where-Object { $_.name -eq $name -and $_.version -eq $RuntimeVersion }).Count -ne 1) {
            throw "Publication does not bundle $name $RuntimeVersion."
        }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'deploy-service.ps1') -Destination $root
    foreach ($name in @('Install-Service.ps1', 'Update-Service.ps1', 'Uninstall-Service.ps1',
        'Service.Common.ps1', 'install-settings.example.json', 'INSTALL.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "packaging\windows\$name") -Destination $root
    }
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName) {
        $hashes[[IO.Path]::GetRelativePath($root, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    [ordered]@{
        PackageVersion = $PackageVersion; RuntimeIdentifier = 'win-x64'; TargetFramework = $runtimeConfig.runtimeOptions.tfm
        RuntimeVersion = $RuntimeVersion; SelfContained = $true; CreatedUtc = [DateTime]::UtcNow.ToString('O'); Files = $hashes
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'package-manifest.json') -Encoding utf8
    $archive = Join-Path $temporary $fileName
    Compress-Archive -Path (Join-Path $root '*') -DestinationPath $archive -CompressionLevel Optimal
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Move-Item -LiteralPath $archive -Destination $outputPath -Force:$Force
    Write-Host "Created $outputPath"
    Write-Host "SHA256: $((Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash)"
    Get-Item -LiteralPath $outputPath
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
