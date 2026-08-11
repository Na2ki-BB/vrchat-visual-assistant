[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot "VRChatVisualAssistant.sln"
$fixtureFramework = "net8.0-windows"
$appFramework = "net8.0-windows10.0.19041.0"
$fixtureDirectory = Join-Path $repositoryRoot "tools\VrcVa.CaptureFixture\bin\$Configuration\$fixtureFramework"
$fixtureExecutable = Join-Path $fixtureDirectory "VRChat.exe"
$appDll = Join-Path $repositoryRoot "src\VrcVa.Windows\bin\$Configuration\$appFramework\VrcVa.dll"
$fixtureOutput = Join-Path $env:TEMP "vrcva-fixture-out.txt"
$fixtureError = Join-Path $env:TEMP "vrcva-fixture-err.txt"

$existingVrChat = Get-Process VRChat -ErrorAction SilentlyContinue
if ($null -ne $existingVrChat) {
    throw "A VRChat process is already running. Close it before running the isolated capture fixture."
}

dotnet build $solution --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

$fixtureProcess = $null
try {
    $fixtureProcess = Start-Process `
        -FilePath $fixtureExecutable `
        -WorkingDirectory $fixtureDirectory `
        -RedirectStandardOutput $fixtureOutput `
        -RedirectStandardError $fixtureError `
        -PassThru
    Start-Sleep -Seconds 2

    dotnet $appDll --capture-vrchat-ocr
    if ($LASTEXITCODE -ne 0) {
        throw "Capture/OCR diagnostic failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force
        $fixtureProcess.WaitForExit(5000) | Out-Null
    }

    foreach ($path in @($fixtureOutput, $fixtureError)) {
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            if (-not (Test-Path -LiteralPath $path)) { break }

            try {
                Remove-Item -LiteralPath $path -Force
                break
            }
            catch [System.IO.IOException] {
                if ($attempt -eq 9) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    }
}
