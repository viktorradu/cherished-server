[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Destination = "C:\tools\cherished-server",
    [string]$TaskName = "PictureServer"
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "cherished-server.csproj"
$publishPath = Join-Path ([System.IO.Path]::GetTempPath()) "cherished-server-$([guid]::NewGuid())"
$serverWasRunning = $false

try {
    & dotnet publish $projectPath --configuration $Configuration --output $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $scheduledTasks = @(Get-ScheduledTask -TaskName $TaskName)
    if ($scheduledTasks.Count -ne 1) {
        throw "Expected exactly one scheduled task named '$TaskName', but found $($scheduledTasks.Count)."
    }

    $scheduledTask = $scheduledTasks[0]
    $serverWasRunning = $scheduledTask.State -eq "Running"
    if ($serverWasRunning) {
        Write-Host "Stopping scheduled task '$TaskName'..."
        Stop-ScheduledTask -InputObject $scheduledTask

        $stopDeadline = (Get-Date).AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 500
            $scheduledTask = Get-ScheduledTask -TaskName $TaskName -TaskPath $scheduledTask.TaskPath
        } while ($scheduledTask.State -eq "Running" -and (Get-Date) -lt $stopDeadline)

        if ($scheduledTask.State -eq "Running") {
            throw "Scheduled task '$TaskName' did not stop within 30 seconds."
        }
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    & robocopy $publishPath $Destination /E /XF .hidden .flagged /R:2 /W:1 /NP
    if ($LASTEXITCODE -ge 8) {
        throw "Deployment failed with robocopy exit code $LASTEXITCODE."
    }

    Write-Host "Deployed cherished-server to '$Destination'."
}
finally {
    if (Test-Path $publishPath) {
        Remove-Item $publishPath -Recurse -Force
    }

    if ($serverWasRunning) {
        Write-Host "Starting scheduled task '$TaskName'..."
        Start-ScheduledTask -InputObject $scheduledTask
    }
}
