[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Apply,
    [string]$ConfigPath = (Join-Path $env:APPDATA "AdvancedSettings-Team\OVR Advanced Settings.ini")
)

$ErrorActionPreference = "Stop"
$interactionSequence = "^>i"
$scanSequence = "^>t"
$modelSequence = "^>g"

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
$interactionSettingMatch = [regex]::Match($body, '(?m)^keyboardOne=(?<value>.*)$')
$scanSettingMatch = [regex]::Match($body, '(?m)^keyboardTwo=(?<value>.*)$')
$modelSettingMatch = [regex]::Match($body, '(?m)^keyboardThree=(?<value>.*)$')
$currentInteractionSequence = if ($interactionSettingMatch.Success) {
    $interactionSettingMatch.Groups["value"].Value.TrimEnd("`r")
} else { "<not set>" }
$currentScanSequence = if ($scanSettingMatch.Success) {
    $scanSettingMatch.Groups["value"].Value.TrimEnd("`r")
} else { "<not set>" }
$currentModelSequence = if ($modelSettingMatch.Success) {
    $modelSettingMatch.Groups["value"].Value.TrimEnd("`r")
} else { "<not set>" }

Write-Host "OVR Advanced Settings config: $ConfigPath"
Write-Host "Keyboard Shortcut One / result interaction: $currentInteractionSequence"
Write-Host "Keyboard Shortcut Two / SCAN: $currentScanSequence"
Write-Host "Keyboard Shortcut Three / model toggle: $currentModelSequence"
Write-Host "VRCVA targets: Ctrl+Shift+I ($interactionSequence), Ctrl+Shift+T ($scanSequence), Ctrl+Shift+G ($modelSequence)"

if (-not $Apply) {
    Write-Host "No changes made. Close SteamVR/OVR Advanced Settings, then rerun with -Apply."
    exit 0
}

$running = Get-Process -Name "AdvancedSettings" -ErrorAction SilentlyContinue
if ($null -ne $running -and -not $WhatIfPreference) {
    throw "OVR Advanced Settings is running. Close SteamVR first so it does not overwrite the edited setting."
}

if ($currentInteractionSequence -eq $interactionSequence `
    -and $currentScanSequence -eq $scanSequence `
    -and $currentModelSequence -eq $modelSequence) {
    Write-Host "All VRCVA shortcuts are already configured."
    exit 0
}

$newline = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
$updatedBody = $body
foreach ($setting in @(
    @{ Name = "keyboardOne"; Value = $interactionSequence },
    @{ Name = "keyboardTwo"; Value = $scanSequence },
    @{ Name = "keyboardThree"; Value = $modelSequence }
)) {
    $pattern = "(?m)^$([regex]::Escape($setting.Name))=.*$"
    if ([regex]::IsMatch($updatedBody, $pattern)) {
        $updatedBody = [regex]::Replace(
            $updatedBody,
            $pattern,
            "$($setting.Name)=$($setting.Value)",
            1)
    }
    else {
        $updatedBody = "$($setting.Name)=$($setting.Value)$newline$updatedBody"
    }
}

$updatedSection = $sectionMatch.Groups[1].Value + $updatedBody
$updatedContent = $content.Remove($sectionMatch.Index, $sectionMatch.Length).Insert(
    $sectionMatch.Index,
    $updatedSection)
$backupPath = "$ConfigPath.vrcva-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

if ($PSCmdlet.ShouldProcess($ConfigPath, "Back up the file and set VRCVA shortcuts")) {
    [System.IO.File]::Copy($ConfigPath, $backupPath, $false)
    [System.IO.File]::WriteAllText(
        $ConfigPath,
        $updatedContent,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Updated VRCVA shortcuts. Backup: $backupPath"
    Write-Host "Restart SteamVR, then bind OVRAS actions 'Keyboard Shortcut One' (result interaction), 'Keyboard Shortcut Two' (SCAN), and 'Keyboard Shortcut Three' (model toggle)."
}
