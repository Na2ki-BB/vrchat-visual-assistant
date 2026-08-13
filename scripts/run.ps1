[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$targetFramework = "net8.0-windows10.0.19041.0"
$executable = Join-Path $repositoryRoot "src\VrcVa.Windows\bin\$Configuration\$targetFramework\VrcVa.exe"

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Build output was not found. Run scripts\build.ps1 first."
}

Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
