#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][Alias('PackagePath')][string]$Package)

$ErrorActionPreference = 'Stop'
function Assert-Check {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw $Message }
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Expected)
    try { & $Action }
    catch {
        if ($_.Exception.Message -like "*$Expected*") { return }
        throw
    }
    throw "Expected rejection containing: $Expected"
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('cherished-model-tests-' + [Guid]::NewGuid().ToString('N'))
$process = $null
$catalogJunction = $null
try {
    $extracted = Join-Path $temporary 'extracted package with spaces'
    Expand-Archive -LiteralPath ([IO.Path]::GetFullPath($Package)) -DestinationPath $extracted
    $app = Join-Path $extracted 'app'
    foreach ($required in @('deploy-service.ps1', 'Service.Common.ps1', 'Install-Service.ps1',
        'Update-Service.ps1', 'Uninstall-Service.ps1', 'INSTALL.md', 'install-settings.example.json', 'package-manifest.json')) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $extracted $required) -PathType Leaf) "Missing package file: $required"
    }
    . (Join-Path $extracted 'Service.Common.ps1')
    $manifest = Test-PackageFiles $extracted
    Assert-Check ($manifest.TargetFramework -eq 'net11.0' -and $manifest.RuntimeVersion -match '^11\.0\.\d+$') 'Package must target stable .NET 11.'
    $files = @(Get-ChildItem -LiteralPath $extracted -File -Recurse -Force | Where-Object Name -ne 'package-manifest.json')
    Assert-Check ($files.Count -eq $manifest.Files.Count) 'Manifest must list every packaged file.'
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($extracted, $file.FullName)
        Assert-Check ($relative -notmatch 'appsettings|service-settings|deployment\.json|\.hidden|\.flagged|\.log$|\.pdb$|\.cs$|\.csproj$') "Local configuration/state/source leaked: $relative"
        if ($file.Extension -in @('.ps1', '.json', '.md', '.txt')) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            Assert-Check (!$text.Contains((Split-Path -Parent $PSScriptRoot), [StringComparison]::OrdinalIgnoreCase) -and
                !$text.Contains($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase)) "Build-machine path leaked: $relative"
        }
    }
    foreach ($script in Get-ChildItem -LiteralPath $extracted -Filter '*.ps1') {
        $errors = $null
        [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$errors) | Out-Null
        Assert-Check ($errors.Count -eq 0) "Invalid script syntax: $($script.Name)"
    }
    $runtime = Get-Content -LiteralPath (Join-Path $app 'cherished-server.runtimeconfig.json') -Raw | ConvertFrom-Json
    Assert-Check ($runtime.runtimeOptions.tfm -eq 'net11.0') 'Published runtime configuration must target net11.0.'
    Assert-Check (!$runtime.runtimeOptions.frameworks) 'Package requires a shared runtime.'
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        Assert-Check (@($runtime.runtimeOptions.includedFrameworks |
            Where-Object { $_.name -eq $framework -and $_.version -eq $manifest.RuntimeVersion }).Count -eq 1) "Missing stable runtime: $framework"
    }
    $scriptPath = Join-Path $app 'wwwroot\js\main.js'
    $originalScript = [IO.File]::ReadAllBytes($scriptPath)
    [IO.File]::AppendAllText($scriptPath, 'tampered')
    Assert-Rejected { Test-PackageFiles $extracted } 'hash mismatch'
    [IO.File]::WriteAllBytes($scriptPath, $originalScript)
    $untracked = Join-Path $app 'untracked.txt'
    [IO.File]::WriteAllText($untracked, 'extra')
    Assert-Rejected { Test-PackageFiles $extracted } 'Untracked application file'
    Remove-Item -LiteralPath $untracked
    Assert-Rejected { Assert-DeploymentPath 'C:\' } 'drive/share root'
    Assert-Rejected { Assert-DeploymentPath 'C:relative' } 'absolute local path'
    Assert-Rejected { Assert-DeploymentPath '\\server\share\state' } 'absolute local path'
    Assert-Rejected { Assert-SeparatePaths 'C:\service' 'C:\service\state' } 'non-nested'
    $cloudRoot = Join-Path $temporary 'cloud ancestor'
    $cloudPhotos = Join-Path $cloudRoot 'photos'
    New-Item -ItemType Directory -Path $cloudPhotos -Force | Out-Null
    Assert-Check ((Get-DirectoryReparseTag $cloudPhotos) -eq 0) 'Native reparse reader failed for ordinary directory.'
    & {
        function Get-Item {
            param($LiteralPath)
            if ($LiteralPath -eq $cloudRoot) { return [pscustomobject]@{ Attributes = [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint } }
            return Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force
        }
        function Get-DirectoryReparseTag { return $tag }
        for ($variant = 0; $variant -lt 16; $variant++) {
            $tag = [uint32]2415919130 -bor ($variant -shl 12)
            Assert-Check ((Assert-DeploymentPath $cloudPhotos -AllowCloudImages) -eq $cloudPhotos) 'Cloud-tagged image ancestor was rejected.'
            Assert-Rejected { Assert-DeploymentPath $cloudPhotos } 'reparse tag'
            Assert-Check (!(Test-PlainDirectoryPath $cloudPhotos)) 'Cloud paths must not be used for automatic ACL mutations.'
        }
        foreach ($tag in @([uint32]2684354563, [uint32]2684354572, [uint32]2952790042, [uint32]2415919105)) {
            Assert-Rejected { Assert-DeploymentPath $cloudPhotos -AllowCloudImages } 'reparse tag'
        }
    }
    $junction = Join-Path $temporary 'junction'
    New-Item -ItemType Junction -Path $junction -Target $cloudRoot | Out-Null
    try {
        Assert-Check ((Get-DirectoryReparseTag $junction) -eq [uint32]2684354563) 'Native tag reader must recognize an actual junction.'
        Assert-Rejected { Assert-DeploymentPath (Join-Path $junction 'photos') -AllowCloudImages } 'reparse tag'
    }
    finally { Remove-Item -LiteralPath $junction -Force }
    Assert-Check (Test-Path -LiteralPath $cloudPhotos) 'Junction cleanup affected its target.'
    Write-Output 'PASS all cloud tag variants, image-only allowance, strict state/deployment/ACL paths and actual junction rejection'
    Write-Output 'PASS archive layout/hashes, bundled stable runtimes, no workstation settings and unsafe-target rejection'

    $deployment = Join-Path $temporary 'installed\WebService'
    $data = Join-Path $temporary 'state'
    $photos = Join-Path $temporary 'photos'
    New-Item -ItemType Directory -Path $deployment, $data, $photos -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $photos 'fixture.jpg'), [byte[]]@(255, 216, 255, 217))
    $catalogJunction = Join-Path $photos 'directory loop'
    New-Item -ItemType Junction -Path $catalogJunction -Target $photos | Out-Null
    $serviceSettings = @{
        ServiceName = 'Cherished Photos'
        PoolSettings = @{ PoolFolder = $photos; StateFolder = $data; PoolFilter = '.jpg'; SlideshowIntervalMs = 3000; RefreshIntervalMs = 1000 }
        Kestrel = @{ Endpoints = @{ Http = @{ Url = 'http://127.0.0.1:18083' } } }
    }
    $settingsPath = Join-Path $deployment 'service-settings.json'
    $serviceSettings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $settingsPath
    $originalSettings = [IO.File]::ReadAllText($settingsPath)
    $hiddenPath = Join-Path $data '.hidden'
    [IO.File]::WriteAllText($hiddenPath, 'retain existing mark')
    $exe = Join-Path $deployment 'releases\old\cherished-server.exe'
    $record = @{
        ServiceName = 'Cherished Photos'; DeploymentRoot = $deployment; ExecutablePath = $exe; Url = 'http://127.0.0.1:18083'
        ImageDirectory = $photos; StateDirectory = $data; ServiceSid = 'S-1-5-80-123'; ImageAccessGranted = $true; PackageVersion = 'old'
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $deployment 'deployment.json')
    $fakeService = [pscustomobject]@{ PathName = Get-ServiceBinaryPath $exe $deployment; StartName = 'NT SERVICE\Cherished Photos'; ProcessId = 123 }
    $fakeTask = [pscustomobject]@{
        Actions = @([pscustomobject]@{
            Execute = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
            Arguments = '-NoLogo -NoProfile -NonInteractive -Command "Start-Service -Name ''Cherished Photos'' -ErrorAction Stop"'
        })
        Principal = [pscustomobject]@{ UserId = 'SYSTEM' }
    }
    & {
        function Get-CimInstance { param($Filter) if ($Filter -ne "Name='Cherished'") { return $fakeService } }
        function Get-ScheduledTask { param($TaskName) if ($TaskName -ne 'Cherished-StartAtBoot') { return $fakeTask } }
        function Get-NetTCPConnection {}
        function New-Service { throw 'Real service changes are forbidden.' }
        function Start-Service { throw 'Real service changes are forbidden.' }
        function Stop-Service { throw 'Real service changes are forbidden.' }
        function sc.exe { throw 'Real service changes are forbidden.' }
        function icacls.exe { throw 'Real ACL changes are forbidden.' }
        function Set-Acl { throw 'Real ACL changes are forbidden.' }
        function Register-ScheduledTask { throw 'Real task changes are forbidden.' }
        function Unregister-ScheduledTask { throw 'Real task changes are forbidden.' }
        & (Join-Path $extracted 'Update-Service.ps1') -DeploymentRoot $deployment -WhatIf
        & (Join-Path $extracted 'Uninstall-Service.ps1') -DeploymentRoot $deployment -WhatIf
        Assert-Rejected {
            & (Join-Path $extracted 'deploy-service.ps1') -DeploymentRoot $deployment -PublishedDirectory $app -Port 1234 -WhatIf
        } 'Updates preserve'
        $fakeService.StartName = 'LocalSystem'
        Assert-Rejected { & (Join-Path $extracted 'Uninstall-Service.ps1') -DeploymentRoot $deployment -WhatIf } 'not owned'
        $fakeService.StartName = 'NT SERVICE\Cherished Photos'
        $fakeTask.Principal.UserId = 'unexpected'
        Assert-Rejected { & (Join-Path $extracted 'Uninstall-Service.ps1') -DeploymentRoot $deployment -WhatIf } 'not owned'
        $fakeTask.Principal.UserId = 'SYSTEM'
        foreach ($key in @('PoolFilter', 'SlideshowIntervalMs', 'RefreshIntervalMs')) {
            $override = @{ $key = @{ PoolFilter = '.png'; SlideshowIntervalMs = 5000; RefreshIntervalMs = 30000 }[$key] }
            Assert-Rejected {
                & (Join-Path $extracted 'deploy-service.ps1') -DeploymentRoot $deployment -PublishedDirectory $app @override -WhatIf
            } 'Updates preserve'
        }
    }
    Assert-Check ([IO.File]::ReadAllText($settingsPath) -eq $originalSettings) 'Upgrade preview modified operator settings.'
    Assert-Check ([IO.File]::ReadAllText($hiddenPath) -eq 'retain existing mark') 'Preview modified mark files.'
    $firstSettings = Join-Path $temporary 'install-settings.json'
    @{ ImageDirectory = $photos; StateDirectory = $data; Port = 18084 } | ConvertTo-Json | Set-Content -LiteralPath $firstSettings
    & {
        function Get-CimInstance { return $null }
        function Get-ScheduledTask { return $null }
        function Get-NetTCPConnection {}
        $fresh = Join-Path $temporary 'new\WebService'
        & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -DeploymentRoot $fresh -WhatIf
        Assert-Check (!(Test-Path -LiteralPath $fresh)) 'First-install preview created directories.'
        Assert-Rejected { & (Join-Path $extracted 'Update-Service.ps1') -DeploymentRoot $fresh -WhatIf } 'requires an existing'
    }
    Write-Output 'PASS owned lifecycle previews, preserved settings/marks, virtual-account/task checks and first-install preview'
    & {
        function Get-CimInstance { param($Filter) if ($Filter -eq "Name='Cherished'") { return $fakeService } }
        function Get-ScheduledTask {}
        Assert-Rejected {
            & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -DeploymentRoot (Join-Path $temporary 'legacy\WebService') -WhatIf
        } 'legacy Cherished'
    }
    & {
        function Get-CimInstance {}
        function Get-ScheduledTask { param($TaskName) if ($TaskName -eq 'Cherished-StartAtBoot') { return $fakeTask } }
        Assert-Rejected {
            & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -DeploymentRoot (Join-Path $temporary 'legacy\WebService') -WhatIf
        } 'legacy Cherished'
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $base = "http://127.0.0.1:$port"
    $serviceSettings.Kestrel.Endpoints.Http.Url = $base
    $serviceSettings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $settingsPath
    $stdout = Join-Path $temporary 'stdout.log'
    $stderr = Join-Path $temporary 'stderr.log'
    $previousRoot = $env:DOTNET_ROOT
    $previousX64 = $env:DOTNET_ROOT_X64
    try {
        $env:DOTNET_ROOT = Join-Path $temporary 'no-runtime'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $process = Start-Process -FilePath (Join-Path $app 'cherished-server.exe') -ArgumentList @(
            '--contentRoot', "`"$app`"", '--environment', 'Production', '--ServiceSettings', "`"$settingsPath`"",
            '--PoolSettings:SlideshowIntervalMs', '1250'
        ) -WorkingDirectory (Join-Path $env:SystemRoot 'System32') -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    }
    finally { $env:DOTNET_ROOT = $previousRoot; $env:DOTNET_ROOT_X64 = $previousX64 }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($true) {
        try { $pool = Invoke-RestMethod "$base/image/loadpool"; break }
        catch {
            if ($process.HasExited -or [DateTime]::UtcNow -gt $deadline) {
                throw "Package failed to start: $(Get-Content $stdout,$stderr -Raw | Out-String)"
            }
            Start-Sleep -Milliseconds 100
        }
    }
    Assert-Check ($pool.poolSize -eq 1) 'External protected configuration was not loaded.'
    Assert-Check ($pool.slideshowIntervalMs -eq 1250) 'Explicit command-line configuration must override the service settings file.'
    Assert-Check ((Invoke-WebRequest $base).Content.Contains('ch-container')) 'Razor page missing.'
    Assert-Check ((Invoke-WebRequest "$base/js/main.js").Content.Contains('waiting-for-pool')) 'Static assets missing.'
    $key = $pool.images[0].id
    Invoke-RestMethod "$base/image/flag?key=$key" -Method Post | Out-Null
    Invoke-RestMethod "$base/image/hide?key=$key" -Method Post | Out-Null
    Assert-Check ((Get-Content (Join-Path $data '.flagged')) -contains (Join-Path $photos 'fixture.jpg')) 'Writable state not used.'
    Assert-Check ((Invoke-RestMethod "$base/image/loadpool").poolSize -eq 0) 'Hidden image remains selectable.'
    Write-Output 'PASS standalone packaged host from System32, external settings, HTTP/static assets and preserved writable state'
    Stop-Process -Id $process.Id
    $process.WaitForExit()
    $process = $null

    # Execute the lifecycle only against in-memory SCM/task/ACL adapters in the extracted copy.
    $commonPath = Join-Path $extracted 'Service.Common.ps1'
    $manifestPath = Join-Path $extracted 'package-manifest.json'
    $originalCommon = [IO.File]::ReadAllBytes($commonPath)
    $originalManifest = [IO.File]::ReadAllBytes($manifestPath)
    $adapters = @'
function Test-DeploymentAdministrator { return $true }
function Initialize-ServiceEventSource { param($Name) $model.EventSource = $Name }
function Get-ServiceSid { param($Name) return [Security.Principal.SecurityIdentifier]::new('S-1-5-80-123') }
function Set-PrivateDirectory { param($Path, $ServiceSid) $model.PrivatePaths.Add($Path) }
function Stop-OwnedService { param($Name) $model.Service.State = 'Stopped'; $model.Controller.Status = 'Stopped' }
function Invoke-Native { param($FilePath, $Arguments) Invoke-FakeNative $FilePath $Arguments }
function Test-PlainDirectoryPath { param($Path) return !$model.CloudImages }
'@
    [IO.File]::AppendAllText($commonPath, "`n$adapters")
    $modifiedManifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $modifiedManifest.Files['Service.Common.ps1'] = (Get-FileHash $commonPath -Algorithm SHA256).Hash
    $modifiedManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath
    $modelRoot = Join-Path $temporary 'modeled installation\WebService'
    $model = @{
        Service = $null; Task = $null; TaskInfo = $null; EventSource = $null; CloudImages = $false
        PrivatePaths = [Collections.Generic.List[string]]::new()
        NativeCalls = [Collections.Generic.List[object]]::new()
        Controller = [pscustomobject]@{ Status = 'Stopped' }
    }
    $model.Controller | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {
        param($Status, $Timeout)
        if ($this.Status -ne $Status) { throw "Unexpected modeled status: $($this.Status)" }
    }
    & {
        function Invoke-FakeNative {
            param($FilePath, $Arguments)
            $model.NativeCalls.Add(@{ FilePath = $FilePath; Arguments = $Arguments })
            if ($FilePath -eq 'sc.exe') {
                if ($Arguments[0] -eq 'create') {
                    Assert-Check ($Arguments[1] -eq 'Cherished Photos' -and $Arguments[9] -eq 'Cherished Photos') 'SCM internal/display name differs from requested name.'
                    $model.Service = [pscustomobject]@{ Name = 'Cherished Photos'; State = 'Stopped'; PathName = $Arguments[3]; StartName = $Arguments[7]; ProcessId = 456 }
                } elseif ($Arguments[0] -eq 'config') { $model.Service.PathName = $Arguments[3] }
                elseif ($Arguments[0] -eq 'delete') { $model.Service = $null }
            } elseif ($FilePath -ne 'icacls.exe') { throw "Unexpected native mutation: $FilePath" }
        }
        function Get-CimInstance { param($Filter) if ($Filter -ne "Name='Cherished'") { return $model.Service } }
        function Get-ScheduledTask { param($TaskName) if ($TaskName -ne 'Cherished-StartAtBoot') { return $model.Task } }
        function Get-ScheduledTaskInfo { return $model.TaskInfo }
        function Get-NetTCPConnection {}
        function Get-Service { return $model.Controller }
        function Start-Service { $model.Service.State = 'Running'; $model.Controller.Status = 'Running' }
        function Start-Sleep {}
        function Invoke-WebRequest { return [pscustomobject]@{ StatusCode = 200 } }
        function New-ScheduledTaskTrigger { return [pscustomobject]@{ Delay = $null } }
        function New-ScheduledTaskAction { param($Execute, $Argument) return [pscustomobject]@{ Execute = $Execute; Arguments = $Argument } }
        function New-ScheduledTaskPrincipal { param($UserId) return [pscustomobject]@{ UserId = $UserId } }
        function New-ScheduledTaskSettingsSet { return [pscustomobject]@{} }
        function Register-ScheduledTask {
            param($Action, $Principal)
            $model.Task = [pscustomobject]@{ Actions = @($Action); Principal = $Principal; State = 'Ready' }
        }
        function Start-ScheduledTask {
            $model.TaskInfo = [pscustomobject]@{ LastRunTime = [DateTime]::Now; LastTaskResult = 0 }
        }
        function Unregister-ScheduledTask { $model.Task = $null }
        function New-Object {
            param($ComObject)
            if ($ComObject -ne 'Schedule.Service') { throw "Unexpected COM mutation: $ComObject" }
            $folder = [pscustomobject]@{}
            $folder | Add-Member ScriptMethod GetFolders { param($Flags) return @([pscustomobject]@{ Path = '\CherishedServer' }) }
            $scheduler = [pscustomobject]@{ Folder = $folder }
            $scheduler | Add-Member ScriptMethod Connect {}
            $scheduler | Add-Member ScriptMethod GetFolder { param($Path) return $this.Folder }
            return $scheduler
        }
        function sc.exe { throw 'Real SCM mutation is forbidden.' }
        function icacls.exe { throw 'Real ACL mutation is forbidden.' }
        function Set-Acl { throw 'Real ACL mutation is forbidden.' }
        @{ ImageDirectory = $photos; StateDirectory = $data; Port = 18084
            PoolFilter = '.JPG, .png'; SlideshowIntervalMs = 5500; RefreshIntervalMs = 25000
        } | ConvertTo-Json | Set-Content -LiteralPath $firstSettings
        & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -DeploymentRoot $modelRoot
        Assert-Check ($model.Service.StartName -eq 'NT SERVICE\Cherished Photos' -and $model.Service.State -eq 'Running') 'Virtual-account installation failed.'
        Assert-Check ($model.Task -and $model.EventSource -eq 'Cherished Photos') 'Startup task/event source setup was omitted.'
        Assert-Check ($model.Task.Actions[0].Arguments.Contains("Start-Service -Name 'Cherished Photos'")) 'Task must quote the service name containing spaces.'
        $installedSettings = Join-Path $modelRoot 'service-settings.json'
        $installedPool = (Get-Content $installedSettings -Raw | ConvertFrom-Json).PoolSettings
        Assert-Check ($installedPool.PoolFilter -eq '.JPG, .png' -and $installedPool.SlideshowIntervalMs -eq 5500 -and
            $installedPool.RefreshIntervalMs -eq 25000) 'Custom installation pool settings were not persisted.'
        $extendedSettings = Get-Content $installedSettings -Raw | ConvertFrom-Json -AsHashtable
        $extendedSettings.HomeAssistant = @{
            Enabled = $true; Url = 'https://ha.example.invalid'
            TokenFile = 'C:\protected-config\ha-token.txt'
            InsideTemperature = 'sensor.inside_temperature'; InsideHumidity = 'sensor.inside_humidity'
            OutsideTemperature = 'sensor.outside_temperature'; OutsideHumidity = 'sensor.outside_humidity'
        }
        $extendedSettings.CustomOperatorSection = @{ Retain = 'operator extension' }
        $extendedSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $installedSettings
        $beforeUpdate = [IO.File]::ReadAllText($installedSettings)
        $firstRecord = Get-Content (Join-Path $modelRoot 'deployment.json') -Raw | ConvertFrom-Json
        Assert-Check (Test-Path -LiteralPath $firstRecord.ExecutablePath) 'Versioned release was not published.'
        & (Join-Path $extracted 'Update-Service.ps1') -DeploymentRoot $modelRoot
        $secondRecord = Get-Content (Join-Path $modelRoot 'deployment.json') -Raw | ConvertFrom-Json
        Assert-Check ($firstRecord.ExecutablePath -ne $secondRecord.ExecutablePath -and (Test-Path $firstRecord.ExecutablePath)) 'Update overwrote or removed the old release.'
        Assert-Check ([IO.File]::ReadAllText($installedSettings) -eq $beforeUpdate) 'Update rewrote operator settings.'
        Assert-Check ([IO.File]::ReadAllText($hiddenPath) -match 'retain existing mark') 'Update lost existing mark state.'
        & (Join-Path $extracted 'Uninstall-Service.ps1') -DeploymentRoot $modelRoot -Confirm:$false
        Assert-Check (!$model.Service -and !$model.Task) 'Removal did not delete the owned service/task.'
        Assert-Check ((Test-Path $installedSettings) -and (Test-Path $secondRecord.ExecutablePath) -and (Test-Path $hiddenPath)) 'Removal deleted retained configuration, release or mark files.'
        Assert-Check (@($model.NativeCalls | Where-Object { $_.FilePath -eq 'icacls.exe' -and $_.Arguments[1] -eq '/remove:g' }).Count -eq 2) 'Removal did not revoke image/state grants.'
        $model.CloudImages = $true
        $model.NativeCalls.Clear()
        $cloudModelRoot = Join-Path $temporary 'modeled cloud images\WebService'
        & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -DeploymentRoot $cloudModelRoot
        $cloudRecord = Get-Content (Join-Path $cloudModelRoot 'deployment.json') -Raw | ConvertFrom-Json
        Assert-Check (!$cloudRecord.ImageAccessGranted) 'Cloud image paths must not be recorded as automatic ACL targets.'
        Assert-Check (@($model.NativeCalls | Where-Object { $_.FilePath -eq 'icacls.exe' -and $_.Arguments[0] -eq $photos }).Count -eq 0) 'Cloud image permissions were mutated.'
        & (Join-Path $extracted 'Uninstall-Service.ps1') -DeploymentRoot $cloudModelRoot -Confirm:$false
        Assert-Check (@($model.NativeCalls | Where-Object { $_.FilePath -eq 'icacls.exe' -and $_.Arguments[1] -eq '/remove:g' }).Count -eq 1) 'Cloud uninstall must revoke only the state grant.'
    }
    [IO.File]::WriteAllBytes($commonPath, $originalCommon)
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
    Write-Output 'PASS fully modeled install/update/remove, retained settings/releases, owned grants and no cloud image ACL mutations'

    # Replace only the extracted test copy to capture wrapper arguments without touching SCM.
    $stub = @'
[CmdletBinding(SupportsShouldProcess)]
param([string]$Action, [string]$PublishedDirectory, [string]$DeploymentRoot, [string]$ImageDirectory,
    [string]$StateDirectory, [int]$Port, [string]$PoolFilter, [int]$SlideshowIntervalMs, [int]$RefreshIntervalMs,
    [switch]$RequireExisting, [switch]$Elevate)
$captured = @{}
foreach ($key in $PSBoundParameters.Keys) { $captured[$key] = $PSBoundParameters[$key].ToString() }
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'captured.json'), ($captured | ConvertTo-Json))
'@
    [IO.File]::WriteAllText((Join-Path $extracted 'deploy-service.ps1'), $stub)
    $capture = Join-Path $extracted 'captured.json'
    & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -DeploymentRoot $deployment -Elevate -WhatIf
    $captured = Get-Content $capture -Raw | ConvertFrom-Json -AsHashtable
    Assert-Check ($captured.Action -eq 'Install' -and $captured.PublishedDirectory -eq $app -and
        $captured.ImageDirectory -eq $photos -and $captured.StateDirectory -eq $data -and
        $captured.Port -eq '18084' -and $captured.Elevate -eq 'True' -and $captured.WhatIf -eq 'True') 'Install wrapper forwarding failed.'
    Assert-Check ($captured.PoolFilter -eq '.JPG, .png' -and $captured.SlideshowIntervalMs -eq '5500' -and
        $captured.RefreshIntervalMs -eq '25000') 'Custom pool settings not forwarded.'
    @{ ImageDirectory = $photos; StateDirectory = $data; Port = 18084 } | ConvertTo-Json | Set-Content -LiteralPath $firstSettings
    & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -WhatIf
    $captured = Get-Content $capture -Raw | ConvertFrom-Json -AsHashtable
    Assert-Check ($captured.PoolFilter -eq '.jpg,.jpeg' -and $captured.SlideshowIntervalMs -eq '3000' -and
        $captured.RefreshIntervalMs -eq '10000') 'Legacy installation file did not retain defaults.'
    foreach ($bounds in @(@{ SlideshowIntervalMs = 250; RefreshIntervalMs = 1000 },
        @{ SlideshowIntervalMs = 86400000; RefreshIntervalMs = 86400000 })) {
        @{ ImageDirectory = $photos; StateDirectory = $data; Port = 18084; PoolFilter = '.jpg,, .JPEG,'
            SlideshowIntervalMs = $bounds.SlideshowIntervalMs; RefreshIntervalMs = $bounds.RefreshIntervalMs
        } | ConvertTo-Json | Set-Content -LiteralPath $firstSettings
        & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -WhatIf
        $captured = Get-Content $capture -Raw | ConvertFrom-Json -AsHashtable
        Assert-Check ($captured.SlideshowIntervalMs -eq "$($bounds.SlideshowIntervalMs)" -and
            $captured.RefreshIntervalMs -eq "$($bounds.RefreshIntervalMs)") 'Valid timing boundaries rejected or changed.'
    }
    & (Join-Path $extracted 'Update-Service.ps1') -DeploymentRoot $deployment -WhatIf
    $captured = Get-Content $capture -Raw | ConvertFrom-Json -AsHashtable
    Assert-Check ($captured.RequireExisting -eq 'True' -and !$captured.ContainsKey('Port') -and !$captured.ContainsKey('ImageDirectory') -and
        !$captured.ContainsKey('StateDirectory') -and !$captured.ContainsKey('PoolFilter') -and
        !$captured.ContainsKey('SlideshowIntervalMs') -and !$captured.ContainsKey('RefreshIntervalMs')) 'Update wrapper overwrites installed settings.'
    & (Join-Path $extracted 'Uninstall-Service.ps1') -DeploymentRoot $deployment -WhatIf
    $captured = Get-Content $capture -Raw | ConvertFrom-Json -AsHashtable
    Assert-Check ($captured.Action -eq 'Remove' -and !$captured.ContainsKey('PublishedDirectory')) 'Remove wrapper forwarding failed.'
    $invalidSettings = @(
        @{ ImageDirectory = ''; StateDirectory = $data; Port = 8083 },
        @{ ImageDirectory = $photos; StateDirectory = $data; Port = 8083.5 },
        @{ ImageDirectory = $photos; StateDirectory = $data; Port = 80 },
        @{ ImageDirectory = $photos; StateDirectory = $data; Port = 8083; Unexpected = $true }
    )
    foreach ($value in @($null, '', '  ', ',, ', '.', 'jpg', '.jpg,png', 42, @('.jpg', '.png'))) {
        $invalidSettings += @{ ImageDirectory = $photos; StateDirectory = $data; Port = 8083; PoolFilter = $value }
    }
    foreach ($value in @($null, 249, 86400001, 250.5, '3000', $true)) {
        $invalidSettings += @{ ImageDirectory = $photos; StateDirectory = $data; Port = 8083; SlideshowIntervalMs = $value }
    }
    foreach ($value in @($null, 999, 86400001, 1000.5, '10000', $false)) {
        $invalidSettings += @{ ImageDirectory = $photos; StateDirectory = $data; Port = 8083; RefreshIntervalMs = $value }
    }
    foreach ($invalid in $invalidSettings) {
        if (Test-Path -LiteralPath $capture) { Remove-Item -LiteralPath $capture }
        $invalid | ConvertTo-Json | Set-Content -LiteralPath $firstSettings
        $rejected = $false
        try { & (Join-Path $extracted 'Install-Service.ps1') -SettingsPath $firstSettings -WhatIf }
        catch { $rejected = $true }
        Assert-Check ($rejected -and !(Test-Path -LiteralPath $capture)) 'Invalid settings reached deployment.'
    }
    Write-Output 'PASS pool settings forwarding/defaults/boundaries, update preservation and invalid settings'
    Write-Output 'All deployment checks passed. No real service/task/ACL/firewall mutation was performed.'
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if ($catalogJunction -and (Test-Path -LiteralPath $catalogJunction)) { Remove-Item -LiteralPath $catalogJunction -Force }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
