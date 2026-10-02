param(
    [Parameter(Mandatory = $true)]
    [string]$AgentPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ServiceName = "FaceHubManagementAgent"
$DisplayName = "FaceHub Management Agent"
$Description = "Privileged local service manager and resource monitor for FaceHub Management."

$ProgramDataDirectory = Join-Path $env:ProgramData "FaceHub Management"
$LogDirectory = Join-Path $ProgramDataDirectory "logs"

function Invoke-Sc {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Null
    $exitCode = $LASTEXITCODE

    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "sc.exe $($Arguments -join ' ') failed with exit code $exitCode."
    }

    return $exitCode
}

function Wait-ServiceState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [System.ServiceProcess.ServiceControllerStatus]$Status,
        [int]$TimeoutSeconds = 30
    )

    $service = Get-Service -Name $Name -ErrorAction Stop
    $service.WaitForStatus($Status, [TimeSpan]::FromSeconds($TimeoutSeconds))
    $service.Refresh()

    if ($service.Status -ne $Status) {
        throw "Service '$Name' did not reach state '$Status'. Current state: '$($service.Status)'."
    }
}

if (-not (Test-Path -LiteralPath $AgentPath -PathType Leaf)) {
    throw "FaceHub Management Agent executable was not found: $AgentPath"
}

$AgentPath = (Resolve-Path -LiteralPath $AgentPath).Path
$BinaryPath = '"' + $AgentPath + '" --service'

New-Item -Path $ProgramDataDirectory -ItemType Directory -Force | Out-Null
New-Item -Path $LogDirectory -ItemType Directory -Force | Out-Null

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($null -eq $existing) {
    New-Service `
        -Name $ServiceName `
        -BinaryPathName $BinaryPath `
        -DisplayName $DisplayName `
        -Description $Description `
        -StartupType Automatic | Out-Null
}
else {
    if ($existing.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        Wait-ServiceState -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Stopped) -TimeoutSeconds 30
    }

    Invoke-Sc -Arguments @(
        "config",
        $ServiceName,
        "binPath=", $BinaryPath,
        "start=", "auto",
        "DisplayName=", $DisplayName
    ) | Out-Null
}

Invoke-Sc -Arguments @("description", $ServiceName, $Description) | Out-Null

# Restart the service automatically if it crashes.
Invoke-Sc -Arguments @(
    "failure",
    $ServiceName,
    "reset=", "86400",
    "actions=", "restart/5000/restart/10000/restart/30000"
) | Out-Null

Invoke-Sc -Arguments @("failureflag", $ServiceName, "1") | Out-Null

# Make sure the SCM configuration is Automatic even when upgrading an old install.
Set-Service -Name $ServiceName -StartupType Automatic

$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    Start-Service -Name $ServiceName -ErrorAction Stop
}

Wait-ServiceState -Name $ServiceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Running) -TimeoutSeconds 30

# Do not accept a service that only transitions through Running and immediately dies.
Start-Sleep -Seconds 2
$service = Get-Service -Name $ServiceName -ErrorAction Stop
$service.Refresh()

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    throw "FaceHub Management Agent stopped shortly after startup. Current state: '$($service.Status)'."
}

Write-Host "FaceHub Management Agent installed and running."
exit 0
