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
$localFixtureDirectory = Join-Path `
    $env:TEMP `
    ("vrcva-capture-fixture-" + [Guid]::NewGuid().ToString("N"))
$fixtureExecutable = Join-Path $localFixtureDirectory "VRChat.exe"
$appDll = Join-Path $repositoryRoot "src\VrcVa.Windows\bin\$Configuration\$appFramework\VrcVa.dll"

$existingVrChat = Get-Process VRChat -ErrorAction SilentlyContinue
if ($null -ne $existingVrChat) {
    throw "A VRChat process is already running. Close it before running the isolated capture fixture."
}

dotnet build $solution --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

$fixtureProcess = $null
try {
    New-Item -ItemType Directory -Path $localFixtureDirectory | Out-Null
    Copy-Item -Path (Join-Path $fixtureDirectory "*") -Destination $localFixtureDirectory
    $fixtureProcess = Start-Process `
        -FilePath $fixtureExecutable `
        -WorkingDirectory $localFixtureDirectory `
        -PassThru
    $fixtureReady = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 250
        $fixtureProcess.Refresh()
        if ($fixtureProcess.MainWindowHandle -ne [IntPtr]::Zero `
            -and $fixtureProcess.MainWindowTitle -eq "VRChat Capture Fixture") {
            $fixtureReady = $true
            break
        }
    }
    if (-not $fixtureReady) {
        throw "Capture fixture window did not become ready."
    }
    Start-Sleep -Seconds 1

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class VrcVaFixtureWindowGeometry
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr windowHandle, out Rect rectangle);
}
"@
    $clientRect = New-Object VrcVaFixtureWindowGeometry+Rect
    if (-not [VrcVaFixtureWindowGeometry]::GetClientRect(
        $fixtureProcess.MainWindowHandle,
        [ref]$clientRect)) {
        throw "Could not read the capture fixture client rectangle."
    }
    $expectedWidth = $clientRect.Right - $clientRect.Left
    $expectedHeight = $clientRect.Bottom - $clientRect.Top

    $diagnosticOutput = @(dotnet $appDll --capture-vrchat-ocr)
    $diagnosticExitCode = $LASTEXITCODE
    $diagnosticOutput | Write-Output
    if ($diagnosticExitCode -ne 0) {
        throw "Capture/OCR diagnostic failed with exit code $diagnosticExitCode."
    }

    $expectedFrameLine = "Frame: ${expectedWidth}x${expectedHeight},"
    if (-not ($diagnosticOutput | Where-Object { $_.StartsWith($expectedFrameLine) })) {
        throw "Capture did not match the fixture client rectangle. Expected ${expectedWidth}x${expectedHeight}."
    }
}
finally {
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force
        $fixtureProcess.WaitForExit(5000) | Out-Null
    }

    if (Test-Path -LiteralPath $localFixtureDirectory) {
        Remove-Item -LiteralPath $localFixtureDirectory -Recurse -Force
    }
}
