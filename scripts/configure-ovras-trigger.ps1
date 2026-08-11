[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Apply,
    [string]$ConfigPath = (Join-Path $env:APPDATA "AdvancedSettings-Team\OVR Advanced Settings.ini")
)

$ErrorActionPreference = "Stop"
$targetSequence = "^>t"

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "OVR Advanced Settings configuration was not found: $ConfigPath"
}

$content = [System.IO.File]::ReadAllText($ConfigPath)
$sectionMatch = [regex]::Match(
    $content,
    '(?ms)(^\[keyboardShortcuts\]\r?\n)(?<body>.*?)(?=^\[|\z)')
if (-not $sectionMatch.Success) {
    throw "The [keyboardShortcuts] section was not found: $ConfigPath"
}

$body = $sectionMatch.Groups["body"].Value
$settingMatch = [regex]::Match($body, '(?m)^keyboardTwo=(?<value>.*)$')
$currentSequence = if ($settingMatch.Success) {
    $settingMatch.Groups["value"].Value.TrimEnd("`r")
}
else {
    "<not set>"
}

Write-Host "OVR Advanced Settings config: $ConfigPath"
Write-Host "Keyboard Shortcut Two: $currentSequence"
Write-Host "VRCVA target: Ctrl+Shift+T ($targetSequence)"

if (-not $Apply) {
    Write-Host "No changes made. Close SteamVR/OVR Advanced Settings, then rerun with -Apply."
    exit 0
}

$running = Get-Process -Name "AdvancedSettings" -ErrorAction SilentlyContinue
if ($null -ne $running) {
    throw "OVR Advanced Settings is running. Close SteamVR first so it does not overwrite the edited setting."
}

if ($currentSequence -eq $targetSequence) {
    Write-Host "Keyboard Shortcut Two is already configured for Ctrl+Shift+T."
    exit 0
}

$newline = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
$updatedBody = if ($settingMatch.Success) {
    [regex]::Replace(
        $body,
        '(?m)^keyboardTwo=.*$',
        "keyboardTwo=$targetSequence",
        1)
}
else {
    "keyboardTwo=$targetSequence$newline$body"
}

$updatedSection = $sectionMatch.Groups[1].Value + $updatedBody
$updatedContent = $content.Remove($sectionMatch.Index, $sectionMatch.Length).Insert(
    $sectionMatch.Index,
    $updatedSection)
$backupPath = "$ConfigPath.vrcva-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

if ($PSCmdlet.ShouldProcess($ConfigPath, "Back up the file and set Keyboard Shortcut Two to Ctrl+Shift+T")) {
    [System.IO.File]::Copy($ConfigPath, $backupPath, $false)
    [System.IO.File]::WriteAllText(
        $ConfigPath,
        $updatedContent,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Updated Keyboard Shortcut Two. Backup: $backupPath"
    Write-Host "Restart SteamVR, then bind the OVR Advanced Settings action 'Keyboard Shortcut Two' to an unused controller gesture."
}
