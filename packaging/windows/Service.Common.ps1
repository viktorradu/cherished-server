#requires -Version 7.0

function Assert-PoolInstallationSettings {
    param([object]$PoolFilter, [object]$SlideshowIntervalMs, [object]$RefreshIntervalMs)
    if ($PoolFilter -isnot [string] -or [string]::IsNullOrWhiteSpace($PoolFilter)) {
        throw 'PoolFilter must be a nonempty comma-separated extension string.'
    }
    $extensions = $PoolFilter.Split(',', [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)
    if (!$extensions.Length -or @($extensions | Where-Object { !$_.StartsWith('.') -or $_.Length -le 1 }).Count) {
        throw 'PoolFilter must contain extensions beginning with a dot, for example .jpg,.jpeg.'
    }
    if (($SlideshowIntervalMs -isnot [int] -and $SlideshowIntervalMs -isnot [long]) -or
        $SlideshowIntervalMs -lt 250 -or $SlideshowIntervalMs -gt 86400000) {
        throw 'SlideshowIntervalMs must be an integer between 250 and 86400000.'
    }
    if (($RefreshIntervalMs -isnot [int] -and $RefreshIntervalMs -isnot [long]) -or
        $RefreshIntervalMs -lt 1000 -or $RefreshIntervalMs -gt 86400000) {
        throw 'RefreshIntervalMs must be an integer between 1000 and 86400000.'
    }
}

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed ($LASTEXITCODE)." }
}

function Test-DeploymentAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Initialize-ServiceEventSource {
    param([string]$Name)
    if (![Diagnostics.EventLog]::SourceExists($Name)) { [Diagnostics.EventLog]::CreateEventSource($Name, 'Application') }
}

function Get-ServiceSid {
    param([string]$Name)
    return [Security.Principal.NTAccount]::new('NT SERVICE', $Name).Translate([Security.Principal.SecurityIdentifier])
}

function Test-CloudReparseTag {
    param([uint32]$Tag)
    # IO_REPARSE_TAG_CLOUD through CLOUD_F differ only in bits 12..15 (CLOUD_MASK).
    return ($Tag -band [uint32]4294905855) -eq [uint32]2415919130
}

function Test-PlainDirectoryPath {
    param([string]$Path)
    $current = $Path
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $true
}

function Get-DirectoryReparseTag {
    param([string]$Path)
    if (!('CherishedDeployment.ReparseInfo' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
namespace CherishedDeployment {
    public static class ReparseInfo {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint SizeHigh, SizeLow, Tag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }
        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string path, out FindData data);
        [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr handle);
        public static uint GetTag(string path) {
            FindData data;
            var handle = FindFirstFile(path, out data);
            if (handle == new IntPtr(-1))
                throw new IOException("Cannot inspect reparse tag: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
            try { return data.Tag; } finally { FindClose(handle); }
        }
    }
}
'@
    }
    return [CherishedDeployment.ReparseInfo]::GetTag($Path)
}

function Assert-DeploymentPath {
    param([string]$Path, [switch]$AllowUnc, [switch]$AllowCloudImages)
    if (![IO.Path]::IsPathFullyQualified($Path) -or $Path.Contains('"') -or
        (!$AllowUnc -and $Path.StartsWith('\\'))) { throw "An absolute local path without quotes is required: $Path" }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw "Use a dedicated directory, not a drive/share root: $Path" }
    $current = $full
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            $tag = Get-DirectoryReparseTag $current
            if (!$AllowCloudImages -or !(Test-CloudReparseTag $tag)) {
                throw ("Unsupported directory reparse tag 0x{0:X8} at {1}. Only known cloud tags are allowed for image paths; deployment/state/ACL paths must not traverse reparse points." -f $tag, $current)
            }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

function Assert-SeparatePaths {
    param([string]$First, [string]$Second)
    if ($First -eq $Second -or $First.StartsWith("$Second\", [StringComparison]::OrdinalIgnoreCase) -or
        $Second.StartsWith("$First\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Deployment, image and writable state directories must be separate, non-nested paths: $First; $Second"
    }
}

function Get-ServiceBinaryPath {
    param([string]$Executable, [string]$DeploymentRoot)
    $content = Split-Path -Parent $Executable
    $settings = Join-Path $DeploymentRoot 'service-settings.json'
    return "`"$Executable`" --contentRoot `"$content`" --environment Production --ServiceSettings `"$settings`""
}

function Set-PrivateDirectory {
    param([string]$Path, [Security.Principal.SecurityIdentifier]$ServiceSid)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    if ($ServiceSid) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $ServiceSid, 'ReadAndExecute', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Stop-OwnedService {
    param([string]$Name)
    $processId = (Get-CimInstance Win32_Service -Filter "Name='$Name'").ProcessId
    Stop-Service -Name $Name
    (Get-Service -Name $Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
    if ($processId -gt 0) {
        try { $process = [Diagnostics.Process]::GetProcessById($processId) }
        catch [ArgumentException] { $process = $null }
        if ($process) {
            try { if (!$process.WaitForExit(30000)) { throw "Service process $processId has not exited." } }
            finally { $process.Dispose() }
        }
    }
}

function Test-PackageFiles {
    param([string]$PackageRoot)
    $manifestPath = Join-Path $PackageRoot 'package-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if (!$manifest.SelfContained -or $manifest.RuntimeIdentifier -ne 'win-x64' -or !$manifest.Files.Count -or
        $manifest.TargetFramework -ne 'net11.0' -or $manifest.RuntimeVersion -notmatch '^11\.0\.\d+$') {
        throw 'A hashed self-contained win-x64 .NET 11 service package is required.'
    }
    foreach ($relative in $manifest.Files.Keys) {
        if ([IO.Path]::IsPathRooted($relative) -or ($relative -split '[\\/]') -contains '..') { throw "Unsafe package path: $relative" }
        $path = Join-Path $PackageRoot $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $manifest.Files[$relative]) {
            throw "Package file hash mismatch: $relative"
        }
    }
    $app = Join-Path $PackageRoot 'app'
    foreach ($required in @('cherished-server.exe', 'cherished-server.dll', 'coreclr.dll', 'wwwroot\js\main.js')) {
        if (!$manifest.Files.Contains("app\$required")) { throw "Package manifest is missing app\$required." }
    }
    foreach ($file in Get-ChildItem -LiteralPath $app -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($PackageRoot, $file.FullName)
        if (!$manifest.Files.Contains($relative)) { throw "Untracked application file in package: $relative" }
    }
    return $manifest
}
