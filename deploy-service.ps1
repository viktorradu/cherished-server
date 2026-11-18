#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Install', 'Remove', 'Status')]
    [string]$Action = 'Install',
    [string]$DeploymentRoot = "$env:ProgramData\CherishedServer\WebService",
    [string]$ImageDirectory,
    [string]$StateDirectory,
    [ValidateRange(1024, 65535)]
    [int]$Port = 8083,
    [object]$PoolFilter = '.jpg,.jpeg',
    [ValidateRange(250, 86400000)]
    [int]$SlideshowIntervalMs = 3000,
    [ValidateRange(1000, 86400000)]
    [int]$RefreshIntervalMs = 10000,
    [string]$PublishedDirectory,
    [ValidatePattern('^11\.0\.\d+$')]
    [string]$RuntimeVersion,
    [switch]$RequireExisting,
    [switch]$Elevate
)

$ErrorActionPreference = 'Stop'
$common = Join-Path $PSScriptRoot 'Service.Common.ps1'
if (!(Test-Path -LiteralPath $common)) { $common = Join-Path $PSScriptRoot 'packaging\windows\Service.Common.ps1' }
. $common
if ($env:OS -ne 'Windows_NT') { throw 'Windows is required for service deployment.' }
$serviceName = 'Cherished Photos'
$taskName = 'Cherished Photos-StartAtBoot'
$taskPath = '\CherishedServer\'
$taskExecutable = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$taskArguments = "-NoLogo -NoProfile -NonInteractive -Command `"Start-Service -Name '$serviceName' -ErrorAction Stop`""
$DeploymentRoot = Assert-DeploymentPath $DeploymentRoot
if ((Split-Path -Leaf $DeploymentRoot) -ne 'WebService') { throw "Use a dedicated deployment directory ending in '\WebService'." }
$settingsPath = Join-Path $DeploymentRoot 'service-settings.json'
$manifestPath = Join-Path $DeploymentRoot 'deployment.json'

function Write-DeploymentJson {
    param([string]$Path, [object]$Value)
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding utf8
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Get-OwnedInstallation {
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath $taskPath -ErrorAction SilentlyContinue
    $record = if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
    if ($record) {
        if ($record.ServiceName -ne $serviceName -or $record.DeploymentRoot -ne $DeploymentRoot -or
            ![IO.Path]::GetFullPath($record.ExecutablePath).StartsWith("$DeploymentRoot\releases\", [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $record.ExecutablePath) -ne 'cherished-server.exe') {
            throw 'Deployment manifest does not describe this installation. Legacy Cherished installations require the documented explicit migration.'
        }
        Assert-DeploymentPath (Split-Path -Parent $record.ExecutablePath) | Out-Null
        foreach ($directory in @($record.StateDirectory, $record.ImageDirectory)) {
            Assert-DeploymentPath $directory -AllowUnc:($directory -eq $record.ImageDirectory) `
                -AllowCloudImages:($directory -eq $record.ImageDirectory) | Out-Null
            Assert-SeparatePaths $DeploymentRoot $directory
        }
        Assert-SeparatePaths $record.StateDirectory $record.ImageDirectory
    }
    if ($service -and (!$record -or $service.PathName -ne (Get-ServiceBinaryPath $record.ExecutablePath $DeploymentRoot) -or
        $service.StartName -ne "NT SERVICE\$serviceName")) { throw "Service $serviceName is not owned by this deployment. Nothing was changed." }
    if ($task -and (!$record -or $task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ne $taskExecutable -or
        $task.Actions[0].Arguments -ne $taskArguments -or
        $task.Principal.UserId -notin @('SYSTEM', 'NT AUTHORITY\SYSTEM', 'S-1-5-18'))) {
        throw 'Startup task is not owned by this deployment. Nothing was changed.'
    }
    return [pscustomobject]@{ Service = $service; Task = $task; Manifest = $record }
}

function Wait-ForEndpoint {
    param([string]$Url)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $failure = 'The endpoint has not responded.'
    do {
        if ((Get-Service -Name $serviceName).Status -eq 'Running') {
            try {
                $page = Invoke-WebRequest $Url -TimeoutSec 5 -SkipHttpErrorCheck
                $pool = Invoke-WebRequest "$Url/image/loadpool" -TimeoutSec 5 -SkipHttpErrorCheck
                if ($page.StatusCode -eq 200 -and $pool.StatusCode -in @(200, 503)) {
                    if ($pool.StatusCode -eq 503) { Write-Warning 'Service is responding but the image catalog is not ready; folder access is retried in the background.' }
                    return
                }
                $failure = "Page/catalog HTTP status: $($page.StatusCode)/$($pool.StatusCode)."
            }
            catch [System.Net.Http.HttpRequestException], [System.Threading.Tasks.TaskCanceledException] { $failure = $_.Exception.Message }
        }
        else { $failure = 'Windows service is not running.' }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$failure Check Application events from Cherished Photos and .NET Runtime. Deployment is not verified."
}

if ($Action -eq 'Install') {
    $legacyService = Get-CimInstance Win32_Service -Filter "Name='Cherished'"
    $legacyTask = Get-ScheduledTask -TaskName 'Cherished-StartAtBoot' -TaskPath $taskPath -ErrorAction SilentlyContinue
    if ($legacyService -or $legacyTask) {
        throw 'A legacy Cherished service or startup task exists. Follow the documented migration before installing Cherished Photos; nothing was changed.'
    }
}
$owned = Get-OwnedInstallation
if ($Action -eq 'Remove' -and $owned.Manifest.ImageAccessGranted -and
    (Test-Path -LiteralPath $owned.Manifest.ImageDirectory)) {
    Assert-DeploymentPath $owned.Manifest.ImageDirectory -AllowUnc | Out-Null
}
if ($Action -eq 'Status') {
    $owned.Service | Select-Object Name, State, StartMode, StartName, PathName
    $owned.Task | Select-Object TaskName, TaskPath, State
    $owned.Manifest | Select-Object Url, ImageDirectory, StateDirectory, ExecutablePath, PackageVersion
    return
}
if ($RequireExisting -and !$owned.Manifest) { throw 'Update requires an existing managed installation.' }
$settings = if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
}
if ($Action -eq 'Install') {
    Assert-PoolInstallationSettings $PoolFilter $SlideshowIntervalMs $RefreshIntervalMs
    if ($owned.Manifest) {
        if (!$settings) { throw 'Installed service-settings.json is missing; restore it before update.' }
        if ($settings.ServiceName -ne $serviceName) { throw 'Installed settings specify an unexpected service identity.' }
        if ($PSBoundParameters.ContainsKey('ImageDirectory') -or $PSBoundParameters.ContainsKey('StateDirectory') -or
            $PSBoundParameters.ContainsKey('Port') -or $PSBoundParameters.ContainsKey('PoolFilter') -or
            $PSBoundParameters.ContainsKey('SlideshowIntervalMs') -or $PSBoundParameters.ContainsKey('RefreshIntervalMs')) {
            throw 'Updates preserve installed paths, port and pool settings. Use Update-Service.ps1, not the initial settings template.'
        }
        $ImageDirectory = $settings.PoolSettings.PoolFolder
        $StateDirectory = $settings.PoolSettings.StateFolder
        if ($ImageDirectory -ne $owned.Manifest.ImageDirectory -or $StateDirectory -ne $owned.Manifest.StateDirectory) {
            throw 'Changing installed image/state directories requires an explicit migration of state and ACLs.'
        }
        $url = $settings.Kestrel.Endpoints.Http.Url
        $uri = $null
        if (![Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'http' -or
            $uri.Host -ne '127.0.0.1' -or $uri.Port -lt 1024 -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment) {
            throw 'Installed listener must remain a loopback http://127.0.0.1:<port> URL.'
        }
        $Port = $uri.Port
    }
    else {
        if ((Test-Path -LiteralPath $DeploymentRoot) -and @(Get-ChildItem -LiteralPath $DeploymentRoot -Force).Count) {
            throw 'Refusing to overwrite an unrecognized nonempty deployment directory.'
        }
        if (!$ImageDirectory -or !$StateDirectory) { throw 'Supply ImageDirectory and StateDirectory for first installation.' }
        $url = "http://127.0.0.1:$Port"
    }
    $ImageDirectory = Assert-DeploymentPath $ImageDirectory -AllowUnc -AllowCloudImages
    $StateDirectory = Assert-DeploymentPath $StateDirectory
    Assert-SeparatePaths $DeploymentRoot $ImageDirectory
    Assert-SeparatePaths $DeploymentRoot $StateDirectory
    Assert-SeparatePaths $ImageDirectory $StateDirectory
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -and (!$owned.Service -or
        @($listeners | Where-Object OwningProcess -ne $owned.Service.ProcessId).Count)) { throw "Port $Port is in use by another process. Nothing was stopped." }
    if ($PublishedDirectory) {
        $PublishedDirectory = Assert-DeploymentPath $PublishedDirectory
        $package = Test-PackageFiles (Split-Path -Parent $PublishedDirectory)
        if ((Split-Path -Leaf $PublishedDirectory) -ne 'app') { throw 'PublishedDirectory must be the app directory of a verified package.' }
    }
}
if (!$PSCmdlet.ShouldProcess($DeploymentRoot, "$Action owned service, startup task and versioned deployment")) { return }
if (!(Test-DeploymentAdministrator)) {
    if (!$Elevate) { throw 'Run elevated PowerShell 7 or add -Elevate to request UAC approval.' }
    $arguments = @('-NoLogo', '-NoProfile', '-File', "`"$PSCommandPath`"", '-Action', $Action, '-DeploymentRoot', "`"$DeploymentRoot`"")
    foreach ($name in @('ImageDirectory', 'StateDirectory', 'PublishedDirectory', 'RuntimeVersion', 'PoolFilter')) {
        if ($PSBoundParameters.ContainsKey($name)) {
            $value = Get-Variable $name -ValueOnly
            if ($value.Contains('"')) { throw "Quotes are not permitted in $name." }
            $arguments += @("-$name", "`"$value`"")
        }
    }
    if ($PSBoundParameters.ContainsKey('Port')) { $arguments += @('-Port', "$Port") }
    foreach ($name in @('SlideshowIntervalMs', 'RefreshIntervalMs')) {
        if ($PSBoundParameters.ContainsKey($name)) { $arguments += @("-$name", "$(Get-Variable $name -ValueOnly)") }
    }
    if ($RequireExisting) { $arguments += '-RequireExisting' }
    $process = Start-Process (Join-Path $PSHOME 'pwsh.exe') -Verb RunAs -ArgumentList $arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Elevated deployment failed ($($process.ExitCode))." }
    return
}
if ($Action -eq 'Remove') {
    if ($owned.Task) { Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Confirm:$false }
    if ($owned.Service) {
        Stop-OwnedService $serviceName
        Invoke-Native sc.exe @('delete', $serviceName)
    }
    if ($owned.Manifest) {
        if ($owned.Manifest.ServiceSid -match '^S-1-5-80-') {
            $directories = @($owned.Manifest.StateDirectory)
            if ($owned.Manifest.ImageAccessGranted) { $directories += $owned.Manifest.ImageDirectory }
            foreach ($directory in $directories) {
                if (Test-Path -LiteralPath $directory) {
                    if ($directory -eq $owned.Manifest.ImageDirectory) { Assert-DeploymentPath $directory -AllowUnc | Out-Null }
                    Invoke-Native icacls.exe @($directory, '/remove:g', "*$($owned.Manifest.ServiceSid)", '/q')
                }
            }
        }
        Set-PrivateDirectory $DeploymentRoot
    }
    Write-Host 'Service and startup task removed. Images, mark files, protected settings, releases and event history were retained.'
    return
}

$temporary = $null
try {
    if (!$PublishedDirectory) {
        if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'package-service.ps1'))) { throw 'Supply PublishedDirectory or deploy from the source checkout.' }
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('cherished-server-deploy-' + [Guid]::NewGuid().ToString('N'))
        $arguments = @{ OutputDirectory = $temporary }
        if ($RuntimeVersion) { $arguments.RuntimeVersion = $RuntimeVersion }
        $archive = & (Join-Path $PSScriptRoot 'package-service.ps1') @arguments
        $extracted = Join-Path $temporary 'extracted'
        Expand-Archive -LiteralPath $archive.FullName -DestinationPath $extracted
        $package = Test-PackageFiles $extracted
        $PublishedDirectory = Join-Path $extracted 'app'
    }
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
        throw 'This package requires Windows x64.'
    }
    New-Item -ItemType Directory -Path $DeploymentRoot, $StateDirectory -Force | Out-Null
    $previousSid = if ($owned.Service) { [Security.Principal.SecurityIdentifier]::new($owned.Manifest.ServiceSid) }
    Set-PrivateDirectory $DeploymentRoot $previousSid
    $release = Join-Path $DeploymentRoot ("releases\" + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory '*') -Destination $release -Recurse
    $installedScript = Join-Path $DeploymentRoot 'deploy-service.ps1'
    if ($PSCommandPath -ne $installedScript) { Copy-Item -LiteralPath $PSCommandPath -Destination $installedScript -Force }
    $installedCommon = Join-Path $DeploymentRoot 'Service.Common.ps1'
    if ($common -ne $installedCommon) { Copy-Item -LiteralPath $common -Destination $installedCommon -Force }
    if (!$settings) {
        $settings = @{
            ServiceName = $serviceName
            HomeAssistant = @{ Enabled = $false }
            PoolSettings = @{ PoolFolder = $ImageDirectory; StateFolder = $StateDirectory; PoolFilter = $PoolFilter; SlideshowIntervalMs = $SlideshowIntervalMs; RefreshIntervalMs = $RefreshIntervalMs }
            Kestrel = @{ Endpoints = @{ Http = @{ Url = $url } } }
            AllowedHosts = 'localhost;127.0.0.1'
            Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
        }
        Write-DeploymentJson $settingsPath $settings
    }
    $executable = Join-Path $release 'cherished-server.exe'
    $binaryPath = Get-ServiceBinaryPath $executable $DeploymentRoot
    $record = @{
        ServiceName = $serviceName; DeploymentRoot = $DeploymentRoot; ExecutablePath = $executable
        ImageDirectory = $ImageDirectory; StateDirectory = $StateDirectory; Url = $url
        ServiceSid = $owned.Manifest.ServiceSid; ImageAccessGranted = [bool]$owned.Manifest.ImageAccessGranted
        PackageVersion = $package.PackageVersion
    }
    if ($owned.Service) {
        Stop-OwnedService $serviceName
        Invoke-Native sc.exe @('config', $serviceName, 'binPath=', $binaryPath, 'start=', 'delayed-auto')
    }
    else {
        Write-DeploymentJson $manifestPath $record
        Invoke-Native sc.exe @('create', $serviceName, 'binPath=', $binaryPath, 'start=', 'delayed-auto',
            'obj=', "NT SERVICE\$serviceName", 'DisplayName=', $serviceName)
        Invoke-Native sc.exe @('description', $serviceName, 'Cherished photo slideshow and recoverable image catalog')
    }
    Write-DeploymentJson $manifestPath $record
    Invoke-Native sc.exe @('sidtype', $serviceName, 'unrestricted')
    Invoke-Native sc.exe @('privs', $serviceName, 'SeChangeNotifyPrivilege')
    $sid = Get-ServiceSid $serviceName
    $record.ServiceSid = $sid.Value
    Write-DeploymentJson $manifestPath $record
    Set-PrivateDirectory $DeploymentRoot $sid
    Invoke-Native icacls.exe @($StateDirectory, '/grant', "*$($sid.Value):(OI)(CI)M", '/q')
    if (!$ImageDirectory.StartsWith('\\') -and (Test-Path -LiteralPath $ImageDirectory -PathType Container)) {
        if (Test-PlainDirectoryPath $ImageDirectory) {
            Assert-DeploymentPath $ImageDirectory | Out-Null
            Invoke-Native icacls.exe @($ImageDirectory, '/grant', "*$($sid.Value):(OI)(CI)RX", '/q')
            $record.ImageAccessGranted = $true
            Write-DeploymentJson $manifestPath $record
        }
        else { Write-Warning 'Cloud-backed image path: no image ACL was changed. Provision service read access separately and keep photos locally available; online-only hydration under the service identity is not guaranteed.' }
    }
    else { Write-Warning 'Image folder is offline or remote. Provision read access separately; no image-folder ACL was changed.' }
    Initialize-ServiceEventSource $serviceName
    Invoke-Native sc.exe @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000')
    Invoke-Native sc.exe @('failureflag', $serviceName, '1')
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $trigger.Delay = 'PT30S'
    $taskAction = New-ScheduledTaskAction -Execute $taskExecutable -Argument $taskArguments
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Minutes 1) `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $schedulerRoot = $scheduler.GetFolder('\')
    if (!@($schedulerRoot.GetFolders(0) | Where-Object Path -eq $taskPath.TrimEnd('\')).Count) { $schedulerRoot.CreateFolder('CherishedServer') | Out-Null }
    Register-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Action $taskAction -Trigger $trigger -Principal $principal `
        -Settings $taskSettings -Description 'Idempotently starts the Cherished Photos Windows service at boot.' -Force | Out-Null
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    Wait-ForEndpoint $url
    $serviceProcessId = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").ProcessId
    $taskRequestedAt = [DateTime]::Now.AddSeconds(-1)
    Start-ScheduledTask -TaskName $taskName -TaskPath $taskPath
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Seconds 1
        $running = (Get-ScheduledTask -TaskName $taskName -TaskPath $taskPath).State -eq 'Running'
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -TaskPath $taskPath
        $finished = !$running -and $taskInfo.LastRunTime -ge $taskRequestedAt
    } while (!$finished -and [DateTime]::UtcNow -lt $deadline)
    if (!$finished -or $taskInfo.LastTaskResult -ne 0 -or
        (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").ProcessId -ne $serviceProcessId) {
        throw 'Startup task verification failed or changed the service process. Deployment is not verified.'
    }
    Write-Host "Verified: $serviceName at $url; idempotent startup task $taskPath$taskName. Deployment: $DeploymentRoot"
}
finally {
    if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
