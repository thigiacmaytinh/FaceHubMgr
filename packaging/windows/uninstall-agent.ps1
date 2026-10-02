$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ServiceName = "FaceHubManagementAgent"
$ScExe = "$env:SystemRoot\System32\sc.exe"

function Test-ServiceExists {
    & $ScExe query $ServiceName *> $null
    return $LASTEXITCODE -eq 0
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Write-Host "FaceHub Management Agent service is already absent."
    exit 0
}

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop

    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30)
    )
    $service.Refresh()

    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw "FaceHub Management Agent could not be stopped. Current state: '$($service.Status)'."
    }
}

& $ScExe delete $ServiceName | Out-Null
$deleteExitCode = $LASTEXITCODE

# 0 = deleted/marked for deletion. 1060 = already gone.
if ($deleteExitCode -ne 0 -and $deleteExitCode -ne 1060) {
    throw "Failed to delete FaceHub Management Agent service. sc.exe exit code: $deleteExitCode"
}

# Service deletion can be deferred by SCM until all handles are closed.
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 250
    $exists = Test-ServiceExists
} while ($exists -and [DateTime]::UtcNow -lt $deadline)

if ($exists) {
    throw "FaceHub Management Agent service is still registered after waiting 30 seconds."
}

Write-Host "FaceHub Management Agent service removed."
exit 0
