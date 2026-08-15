[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-SafeFileNamePart {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $safeValue = $Value.Trim() -replace '[^A-Za-z0-9._-]+', '-'
    $safeValue = $safeValue -replace '\.{2,}', '.'
    $safeValue = $safeValue.Trim([char[]]'.-_')

    if ([string]::IsNullOrWhiteSpace($safeValue)) {
        throw "Version must contain at least one ASCII letter or number."
    }

    if ($safeValue.Length -gt 64) {
        $safeValue = $safeValue.Substring(0, 64).TrimEnd([char[]]'.-_')
    }

    return $safeValue
}

function Assert-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Publish validation failed: $Description was not found at '$Path'."
    }
}

function New-PortableZipArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $sourceParent = Split-Path -Parent $SourceDirectory
    $zipArchive = [System.IO.Compression.ZipFile]::Open(
        $DestinationPath,
        [System.IO.Compression.ZipArchiveMode]::Create)

    try {
        $sourceFiles = Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse | Sort-Object -Property FullName
        foreach ($sourceFile in $sourceFiles) {
            $entryName = $sourceFile.FullName.Substring($sourceParent.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zipArchive,
                $sourceFile.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zipArchive.Dispose()
    }
}

function Assert-ZipLayout {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,

        [Parameter(Mandatory = $true)]
        [string]$GettingStartedFileName
    )

    $requiredEntries = @(
        "VRCVA/VrcVa.exe"
        "VRCVA/OpenVr/Assets/actions.json"
        "VRCVA/OpenVr/Assets/bindings_oculus_touch.json"
        "VRCVA/coreclr.dll"
        "VRCVA/$GettingStartedFileName"
    )

    $zipArchive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entryNames = @($zipArchive.Entries | ForEach-Object { $_.FullName })
        $unexpectedEntry = $entryNames | Where-Object { -not $_.StartsWith("VRCVA/", [System.StringComparison]::Ordinal) } | Select-Object -First 1
        if ($null -ne $unexpectedEntry) {
            throw "Archive validation failed: '$unexpectedEntry' is outside the VRCVA/ top-level directory."
        }

        foreach ($requiredEntry in $requiredEntries) {
            if ($entryNames -notcontains $requiredEntry) {
                throw "Archive validation failed: '$requiredEntry' was not found."
            }
        }
    }
    finally {
        $zipArchive.Dispose()
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $repositoryRoot "src\VrcVa.Windows\VrcVa.Windows.csproj"
$artifactRoot = Join-Path $repositoryRoot "artifacts\beta"
$safeVersion = ConvertTo-SafeFileNamePart -Value $Version
$workDirectoryName = ".work-publish-$([Guid]::NewGuid().ToString('N'))"
$workDirectory = Join-Path $artifactRoot $workDirectoryName
$packageRoot = Join-Path $workDirectory "VRCVA"
$temporaryArchive = Join-Path $workDirectory "VRCVA-beta-$safeVersion-win-x64.zip"
$finalArchive = Join-Path $artifactRoot "VRCVA-beta-$safeVersion-win-x64.zip"
$previousArchive = $null

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found. Install the .NET 8 SDK before publishing."
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
New-Item -ItemType Directory -Path $packageRoot | Out-Null

try {
    $publishArguments = @(
        "publish"
        $project
        "--configuration", "Release"
        "--runtime", "win-x64"
        "--self-contained", "true"
        "--output", $packageRoot
        "-p:PublishSingleFile=false"
        "-p:PublishTrimmed=false"
    )

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $requiredFiles = @(
        @{ Path = (Join-Path $packageRoot "VrcVa.exe"); Description = "VrcVa.exe" }
        @{ Path = (Join-Path $packageRoot "OpenVr\Assets\actions.json"); Description = "SteamVR action manifest" }
        @{ Path = (Join-Path $packageRoot "OpenVr\Assets\bindings_oculus_touch.json"); Description = "Oculus Touch binding manifest" }
        @{ Path = (Join-Path $packageRoot "coreclr.dll"); Description = "self-contained .NET runtime" }
    )

    foreach ($requiredFile in $requiredFiles) {
        Assert-RequiredFile -Path $requiredFile.Path -Description $requiredFile.Description
    }

    # Keep this script ASCII so Windows PowerShell 5.1 reads it consistently.
    $gettingStartedBase64 = "VlJDVkEg5bCR5Lq65pWw44OZ44O844K/CgoxLiDjgZPjga4gWklQIOOCkuWxlemWi+OBl+OBpuOBj+OBoOOBleOBhOOAggoyLiDlsZXplovjgZfjgZ8gVlJDVkEg44OV44Kp44Or44OA44O844Gv44CB5Yid5pyf6Kit5a6a5b6M44Gr56e75YuV44GX44Gq44GE44Gn44GP44Gg44GV44GE44CCCjMuIFN0ZWFtVlIg44KS6LW35YuV44GX44Gm44GL44KJIFZyY1ZhLmV4ZSDjgpLlrp/ooYzjgZfjgIHnlLvpnaLjga7moYjlhoXjgavlvpPjgaPjgabjgY/jgaDjgZXjgYTjgIIKCuOBk+OBrumFjeW4g+eJqeOBoOOBkeOBp+WLleS9nOOBmeOCi+OBn+OCgeOAgS5ORVQgUnVudGltZSDjga7ov73liqDjgqTjg7Pjgrnjg4jjg7zjg6vjga/kuI3opoHjgafjgZnjgIIK"
    $gettingStarted = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($gettingStartedBase64))
    $gettingStartedFileName = (-join @([char]0x306f, [char]0x3058, [char]0x3081, [char]0x306b)) + ".txt"
    Set-Content -LiteralPath (Join-Path $packageRoot $gettingStartedFileName) -Value $gettingStarted -Encoding UTF8

    New-PortableZipArchive -SourceDirectory $packageRoot -DestinationPath $temporaryArchive
    Assert-ZipLayout -ArchivePath $temporaryArchive -GettingStartedFileName $gettingStartedFileName

    if (Test-Path -LiteralPath $finalArchive -PathType Leaf) {
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $previousArchive = Join-Path $artifactRoot "VRCVA-beta-$safeVersion-win-x64.previous-$timestamp-$([Guid]::NewGuid().ToString('N').Substring(0, 8)).zip"
        Move-Item -LiteralPath $finalArchive -Destination $previousArchive
    }

    try {
        Move-Item -LiteralPath $temporaryArchive -Destination $finalArchive
    }
    catch {
        if (($null -ne $previousArchive) -and
            (Test-Path -LiteralPath $previousArchive -PathType Leaf) -and
            (-not (Test-Path -LiteralPath $finalArchive))) {
            Move-Item -LiteralPath $previousArchive -Destination $finalArchive
        }

        throw
    }

    $archiveInfo = Get-Item -LiteralPath $finalArchive
    Write-Host "Beta package created: $($archiveInfo.FullName)"
    Write-Host "Archive size: $($archiveInfo.Length) bytes"
    if ($null -ne $previousArchive) {
        Write-Host "Previous package preserved: $previousArchive"
    }
}
finally {
    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedWorkDirectory = [System.IO.Path]::GetFullPath($workDirectory)

    if ($resolvedWorkDirectory.StartsWith($resolvedArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        ([System.IO.Path]::GetFileName($resolvedWorkDirectory) -like ".work-publish-*")) {
        if (Test-Path -LiteralPath $resolvedWorkDirectory -PathType Container) {
            Remove-Item -LiteralPath $resolvedWorkDirectory -Recurse -Force
        }
    }
    else {
        throw "Refusing to clean an unexpected work directory: '$resolvedWorkDirectory'."
    }
}
