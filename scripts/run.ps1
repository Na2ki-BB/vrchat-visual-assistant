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

$translationProvider = if ([string]::IsNullOrWhiteSpace($env:VRCVA_TRANSLATION_PROVIDER)) {
    "none"
} else {
    $env:VRCVA_TRANSLATION_PROVIDER.Trim().ToLowerInvariant()
}

if ($translationProvider -eq "none") {
    Write-Warning "No translation backend is selected. Capture and OCR can be tested, but translation is intentionally disabled."
} elseif ($translationProvider -eq "openai" -and
    [string]::IsNullOrWhiteSpace($env:VRCVA_OPENAI_API_KEY)) {
    Write-Warning "OpenAI was explicitly selected, but VRCVA_OPENAI_API_KEY is not configured."
}

Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
