param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root "artifacts\windows"

Remove-Item $Artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Artifacts -ItemType Directory -Force | Out-Null

dotnet restore (Join-Path $Root "FaceHubManagement.sln")
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

dotnet build (Join-Path $Root "FaceHubManagement.sln") -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

dotnet publish (Join-Path $Root "src\FaceHubManagement.Desktop\FaceHubManagement.Desktop.csproj") `
    -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false `
    -o (Join-Path $Artifacts "desktop")
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed with exit code $LASTEXITCODE." }

dotnet publish (Join-Path $Root "src\FaceHubManagement.Agent\FaceHubManagement.Agent.csproj") `
    -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false `
    -o (Join-Path $Artifacts "agent")
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed with exit code $LASTEXITCODE." }

# dotnet publish (Join-Path $Root "src\FaceHubManagement.SmokeTest\FaceHubManagement.SmokeTest.csproj") `
    # -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false `
    # -o (Join-Path $Artifacts "smoketest")
	
Get-ChildItem `
    ".\artifacts\windows" `
    -Recurse `
    -Filter *.pdb |
Remove-Item -Force

Write-Host "Windows artifacts created at $Artifacts"
