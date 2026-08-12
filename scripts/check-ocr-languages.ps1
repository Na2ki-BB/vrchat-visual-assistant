[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}
catch {
    # Language detection still works if the host does not allow encoding changes.
}

[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null

$languageTags = @(
    [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages |
        ForEach-Object { $_.LanguageTag } |
        Sort-Object
)

if ($languageTags.Count -eq 0) {
    Write-Warning "利用可能なWindows OCR認識器がありません。"
}
else {
    Write-Host "利用可能なWindows OCR認識器: $($languageTags -join ', ')"
}

$hasEnglishRecognizer = @(
    $languageTags | Where-Object { $_ -match '^en(?:-|$)' }
).Count -gt 0

if ($hasEnglishRecognizer) {
    Write-Host "英語OCR: 利用可能"
}
else {
    Write-Warning "英語OCR: 未導入。英語が漢字や全角記号に化ける場合があります。"
    Write-Host "導入手順: 設定 → 時刻と言語 → 言語と地域 → Englishを追加 → 言語のオプション → オプション機能 → 文字認識 (OCR)"
    Write-Host "Windowsの表示言語を英語に変更する必要はありません。"
}
