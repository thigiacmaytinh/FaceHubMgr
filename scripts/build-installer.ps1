param(
    [string]$InnoCompiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "publish-windows.ps1")

if (-not (Test-Path $InnoCompiler)) {
    throw "Inno Setup compiler not found at '$InnoCompiler'. Install Inno Setup 6 or pass -InnoCompiler."
}

& $InnoCompiler (Join-Path $Root "packaging\windows\FaceHubManagement.iss")
