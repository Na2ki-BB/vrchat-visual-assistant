[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot "VRChatVisualAssistant.sln"

Push-Location $repositoryRoot
try {
    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

    dotnet build $solution --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

    dotnet test $solution --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
}
finally {
    Pop-Location
}

