# VRChat Visual Assistant

VRChat のデスクトップミラーに見えている英語を、明示的な SCAN 1回でローカルOCRし、日本語へ翻訳する外部Windowsアプリです。

現在は **Phase 1 MVPの途中** です。キャプチャとOCRは実装済みですが、翻訳バックエンドは比較・判断中のため既定では無効です。SteamVR Overlay、手首HUD、VRChat OSCトリガーは設計済みですが、MVPの実機評価後に進めます。

確認対象の実機環境は **Meta Quest 3SのPCVR + SteamVR + XSOverlay** です。XSOverlayが導入済みなので、専用Overlay実装を待たず、現在の結果ウィンドウをVR内へ表示するPhase 1.5を優先します。

> 非公式プロジェクトです。VRChat Inc.、Valve Corporation、OpenAIの承認・提携を示すものではありません。

## 現在できること

- `Ctrl+Shift+T`（既定）または SCAN ボタンで1回だけスキャン
- 表示中かつ最小化されていない `VRChat.exe` ウィンドウを取得
- Windows内蔵OCRでローカル文字認識
- 翻訳バックエンド未選定時は外部送信せず、設定不足として明示
- 明示的に選んだ場合だけ、OCRテキストをOpenAI Responses APIで翻訳
- 英語OCR、翻訳、処理時間、失敗段階、相関IDをWPF画面に表示
- 任意のローカル画像からOCR/翻訳を診断
- VRChatなしでキャプチャ→OCRを検証できる開発用fixture

## プライバシー

- 常時録画・定期キャプチャ・テレメトリはありません。
- ユーザーがクリック、ホットキー、診断コマンドを実行した瞬間だけ取得します。
- キャプチャはメモリ内で処理し、通常動作では保存しません。処理後は画像バッファをゼロ化します。
- 既定では翻訳バックエンド未選定のため、OCR済みテキストも外部へ送りません。
- 任意のOpenAIアダプターを明示選択した場合も、送るのはOCR済みテキストだけです。画像は送りません。
- OpenAI要求は `store: false` です。ただしOCRテキストが外部サービスへ送信される点は変わりません。
- ログは画像、OCR本文、翻訳本文、APIキー、HTTP本文を記録しません。寸法、文字数、時間、エラー種別だけです。
- VRChatへのDLL注入、ファイル改変、メモリ読み取り、非公開API利用は行いません。

詳細は [DESIGN.md](DESIGN.md) の「Security, privacy, and public-repository policy」を参照してください。

## 費用

- アプリ本体、Windows画面取得、Windows OCR、ローカルログには利用回数に応じた料金はありません。
- 既に導入済みのXSOverlayへこのウィンドウを表示することについて、本アプリから追加料金は発生しません。
- **既定状態では翻訳サービスを呼ばないため、API料金は発生しません。** 採用サービスはまだ決定していません。
- OpenAI翻訳は任意の従量課金フォールバックです。`VRCVA_TRANSLATION_PROVIDER=openai`と専用の`VRCVA_OPENAI_API_KEY`を両方設定しない限り呼ばれません。
- アプリは一般的な`OPENAI_API_KEY`を自動利用しません。別ツール用のキーで意図せず課金されることを防ぎます。
- OpenAIを選んだ場合の価格は変わり得るため、[公式モデルページ](https://developers.openai.com/api/docs/models/gpt-5.6-luna)を確認してください。

OpenAIを明示選択した場合の使用量は[OpenAI Usage Dashboard](https://platform.openai.com/usage)で確認します。

## 必要環境

- Windows 10 version 2004 / build 19041 以降（Windows 11推奨）
- .NET 8 Desktop Runtime（開発時は .NET 8 SDK）
- PC版VRChat。MVPではデスクトップミラーが画面上に見えている必要があります
- 翻訳バックエンドは未選定。現段階ではキャプチャとOCRだけを無料で検証可能
- WindowsのOCR言語機能（この開発PCでは日本語OCRだけでも英語fixtureを認識できましたが、英語OCR追加を推奨）
- VR内表示にはSteamVRとXSOverlay（デスクトップだけで使う場合は不要）

このリポジトリの確認環境は、Windows build 26200 + WSL2 Ubuntu 24.04.4 + Windows .NET SDK 8.0.422です。Visual Studioは不要です。

## ビルドとテスト

Windows PowerShellでリポジトリルートから実行します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

このスクリプトは restore、Release build、ネットワーク不要の単体テストを順に実行します。出力は次です。

```text
src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.exe
```

WSLのシェルからは、Windows側のPowerShellと .NET SDKを使います。

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w scripts/build.ps1)"
```

### 配布用フォルダを作る

.NET 8 Desktop Runtimeを利用するframework-dependent版は、Windows PowerShellで次のように生成できます。

```powershell
dotnet publish .\src\VrcVa.Windows\VrcVa.Windows.csproj `
  --configuration Release `
  --output .\artifacts\publish\win-framework-dependent
```

起動ファイルは `artifacts\publish\win-framework-dependent\VrcVa.exe` です。`artifacts/` はGit管理対象外です。

## 調査済みの無料候補（未採用）

| 手段 | 無料範囲 | PCVR負荷 | 注意点 | 現在の扱い |
| --- | --- | --- | --- | --- |
| Azure Translator F0 | 月200万文字 | GPU負荷なし | Azureアカウント、F0リソース、キーが必要。OCRテキストを送信 | 候補 |
| DeepL API Developer | 合計100万文字 | GPU負荷なし | 月次回復しない。上限後はGrowthへの変更が必要。キーが必要 | 候補 |
| Google Cloud Translation | 毎月50万文字相当のクレジット | GPU負荷なし | 課金アカウントが必要で、超過分は有料 | 自動課金防止設計が必要 |
| Amazon Translate | 月200万文字を12か月 | GPU負荷なし | 期間終了または超過後は従量課金 | MVPには不向き |
| Argos Translate / OPUS-MT | 回数制限なし、オフライン | CPU・メモリを使用 | Python/モデル配布、品質と速度の実機評価が必要 | 将来の完全ローカル候補 |

無料Web翻訳ページの自動操作や非公開APIの利用は、安定性・利用規約・公開アプリの安全性に問題があるため採用しません。

## 任意: OpenAI翻訳を明示的に使う

OpenAIは既定では無効です。利用する場合だけ、キーをファイルやシェル履歴に書かず、起動するPowerShellプロセスへ設定します。

```powershell
$secureKey = Read-Host "OpenAI API key" -AsSecureString
$env:VRCVA_OPENAI_API_KEY = [System.Net.NetworkCredential]::new("", $secureKey).Password
$env:VRCVA_TRANSLATION_PROVIDER = "openai"
```

次に、同じPowerShellから起動します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

終了後、必要なら現在のPowerShellからキーを消します。

```powershell
Remove-Item Env:VRCVA_OPENAI_API_KEY
Remove-Item Env:VRCVA_TRANSLATION_PROVIDER
```

`.env`、`appsettings.Local.json`、ログ、キャプチャ、ビルド出力はGit管理対象外です。アプリは `.env` を自動読込しません。

## 通常の使い方

1. VRChatを起動し、デスクトップミラーを表示したままにします。
2. VRChat Visual Assistantを起動します。
3. VR内で英語を見て、`Ctrl+Shift+T` を押します。
4. デスクトップ結果窓に英語OCRと日本語訳が表示されます。

SCANボタンをクリックした場合、アプリ自身が写り込まないよう結果窓を一時的に隠します。グローバルホットキーではVRChatからフォーカスを奪いません。

## Meta Quest 3S + XSOverlayでVR内表示する

XSOverlayはOpenVR/SteamVR上で特定のWindowsアプリをWindow Captureとして表示できます。現在のMVPでは、まずこの機能で専用HUDの代わりにします。

1. Quest 3SをPCVR接続し、SteamVR、XSOverlay、VRChat、VRChat Visual Assistantを起動します。
2. XSOverlayで新しいWindow Captureを作り、`VRChat Visual Assistant`を選択します。
3. 読みやすい大きさと位置に調整し、必要なら配置を保存します。
4. VRコントローラーで結果窓のSCANボタンを押します。ボタン経由なら取得直前に窓が一時的に隠れるため、自分自身の写り込みを避けられます。
5. 翻訳完了後、同じXSOverlay窓で日本語訳を確認します。

グローバルホットキーを使う場合は、Windowsデスクトップ上で結果窓がVRChatミラーに重ならないよう配置してください。XSOverlayでの位置固定、視認性、Questコントローラー操作は実機テストで調整します。

### 設定用環境変数

| Variable | Default | Meaning |
| --- | --- | --- |
| `VRCVA_TRANSLATION_PROVIDER` | `none` | 未選定。現在実装済みの任意値は`openai`のみ |
| `VRCVA_OPENAI_API_KEY` | none | OpenAIを明示選択した場合だけ読む専用APIキー |
| `VRCVA_OPENAI_MODEL` | `gpt-5.6-luna` | 翻訳モデル |
| `VRCVA_OPENAI_ENDPOINT` | `https://api.openai.com/v1/responses` | Responses API endpoint |
| `VRCVA_OPENAI_TIMEOUT_SECONDS` | `25` | 1〜120秒 |
| `VRCVA_HOTKEY` | `Ctrl+Shift+T` | 修飾キーを1つ以上含むグローバルホットキー |

例:

```powershell
$env:VRCVA_HOTKEY = "Ctrl+Alt+T"
```

## OCRだけを診断する

診断は翻訳APIを呼びません。WinExeを `dotnet` ホストで起動すると、結果を同じコンソールで確認できます。

### 任意の画像

```powershell
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll `
  --ocr-file C:\path\to\sign.png
```

GUIの「画像で診断」も利用できます。選んだファイルを読みますが、別の画像ファイルは生成しません。

### キャプチャ→OCR（VRChat不要のfixture）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-capture.ps1
```

このスクリプトは開発用の英語テスト窓（プロセス名 `VRChat.exe`）だけを一時起動し、実際のWin32キャプチャとWindows OCRを通してから終了します。実際のVRChatが起動中なら、安全のため実行を拒否します。

明示的にキャプチャ内容を調べるときだけ、次の診断オプションでPNGを保存できます。これは通常動作では使いません。保存先の画像には画面内容が含まれるため、確認後に自分で削除してください。

```powershell
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll `
  --capture-vrchat-ocr --save-capture C:\Temp\vrcva-debug.png
```

## 失敗時の切り分け

| 表示/症状 | 段階 | 確認すること |
| --- | --- | --- |
| `CaptureTargetNotFound` | Capture | Windows版 `VRChat.exe` が起動し、通常ウィンドウがあるか |
| 最小化エラー | Capture | VRChatミラーを復元する |
| 黒い/別窓が写る | Capture | ミラーが見えているか、他の窓が覆っていないか、HDRを一時的に切ると変わるか |
| 一部だけ写る | Capture | 最新ビルドか。DWM物理ピクセル境界を使うため、DPI修正前のビルドでは150%表示などで誤クロップする |
| `OcrUnavailable` | OCR | Windowsの言語オプションにOCR機能があるか。診断コマンドが列挙する言語タグを確認 |
| `NoTextDetected` | OCR | 文字を大きくする、ミラー解像度を上げる、画像診断で近い画像を試す |
| 翻訳バックエンド未選定 | Translation | 現在の既定状態。候補決定まではキャプチャ/OCR診断を使用する |
| OpenAI専用キー未設定 | Translation | OpenAIを使う場合だけ、同じPowerShellに`VRCVA_TRANSLATION_PROVIDER=openai`と`VRCVA_OPENAI_API_KEY`があるか |
| 認証/レート制限 | Translation | APIキー権限、課金状態、利用上限。キー値自体はログに出ない |
| タイムアウト | Translation | ネットワークと `VRCVA_OPENAI_TIMEOUT_SECONDS` |
| ホットキー登録失敗 | Trigger | 他アプリとの競合。`VRCVA_HOTKEY` を変更して再起動 |

ログは次にあります。

```text
%LOCALAPPDATA%\VrcVa\logs\vrcva-YYYYMMDD.log
```

相関IDで1回のSCANを追跡できます。ログには画面・テキスト・キーを記録しないため、OCR精度の確認は明示的な画像診断を使います。

## 現在の制約

- 取得対象はVRChatのHMD eye textureではなく、Windowsデスクトップ上のVRChatウィンドウ枠です。
- GDIの可視画面コピーなので、最小化・画面外・他ウィンドウによる遮蔽に弱いです。実測で問題なら `Windows.Graphics.Capture` アダプタへ置き換えます。
- 現在の結果表示はデスクトップだけです。VR内HUDではありません。
- VRコントローラー/OSCトリガーはまだありません。
- ローカルOCRは小さい文字、遠近、装飾フォント、発光、低コントラストで精度が下がります。
- 翻訳バックエンドは未選定で、既定状態では日本語訳を生成しません。
- OpenAI APIの実通信は、リポジトリやCIに秘密を置かないため利用者が明示選択した場合だけ行います。自動テストは偽HTTP応答を使います。

## 設計とロードマップ

- [DESIGN.md](DESIGN.md): 課題、方式比較、アーキテクチャ、プライバシー、将来拡張
- [TASKS.md](TASKS.md): 実装順、成功条件、実機テスト、OSC/OpenVR以降のタスク

次の判断ゲートは翻訳バックエンドの選定です。決定後に実装し、代表的なVRChatワールドで5回以上計測してから、公式VRChat OSCトリガー、OpenVR Overlay、手首相対HUDの順に進みます。

## ライセンス

まだ選択していません。公開リポジトリであっても、ライセンスファイルが追加されるまでは再利用許諾を意味しません。所有者が選択した後に追加します。
