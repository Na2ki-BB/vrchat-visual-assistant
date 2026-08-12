# VRChat Visual Assistant

VRChat のデスクトップミラーに見えている英語を、明示的な SCAN 1回でローカルOCRし、日本語へ翻訳する外部Windowsアプリです。

現在は **Phase 1.7の実機改善中** です。キャプチャ、OCR、操作可能なSteamVR結果パネルは実装済みですが、翻訳バックエンドは比較・判断中のため既定では無効です。既定状態でもOCRした英語は結果として表示します。手首追従とVRChat OSCトリガーは後続です。

確認対象の実機環境は **Meta Quest 3SのPCVR + SteamVR + XSOverlay + OVR Advanced Settings** です。WPF窓をXSOverlayのWindow Captureとして常設する案は、実機で「作成手順が長い、表示が大きい、コントローラークリックが機能しない」という問題が確認されたため不採用に変更しました。XSOverlayは短いSCAN開始・エラー通知だけに使い、OCR/翻訳結果はVRCVA自身のSteamVRパネルへ表示します。

> 非公式プロジェクトです。VRChat Inc.、Valve Corporation、OpenAIの承認・提携を示すものではありません。

## 現在できること

- `Ctrl+Shift+T`（既定）または SCAN ボタンで1回だけスキャン
- 結果パネルは既定で非インタラクティブ。`Ctrl+Shift+I`またはOVRAS操作でレーザー操作をON/OFF
- Windows Graphics Captureで`VRChat.exe`の描画領域だけを1回取得。タイトルバーや別のPCウィンドウは混ざらない
- 最小化中なら撮影時だけ自動復元し、直後に元の最小化状態へ戻す
- Windows内蔵OCRでローカル文字認識。通常認識が弱いときは画面全体を横帯に分けて自動再認識
- 翻訳バックエンド未選定時は外部送信せず、英語OCR結果を表示
- 明示的に選んだ場合だけ、OCRテキストをOpenAI Responses APIで翻訳
- 約1秒のSCAN中通知と失敗段階をXSOverlay通知としてVR内表示
- OCR/翻訳結果をSteamVR内の操作可能なパネルへ表示し、閉じるまで保持
- 長い結果をVRコントローラーで上下スクロール
- OpenAI利用時は`Ctrl+Shift+G`、またはOVR Advanced Settingsに割り当てたVR操作で`GPT-5.4 nano`と`GPT-5.6 Luna`を切り替え
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
- 既に導入済みのXSOverlayへローカル通知を出すことについて、本アプリから追加料金は発生しません。
- **既定状態では翻訳サービスを呼ばないため、API料金は発生しません。** 採用サービスはまだ決定していません。
- OpenAI翻訳は任意の従量課金フォールバックです。`VRCVA_TRANSLATION_PROVIDER=openai`と専用の`VRCVA_OPENAI_API_KEY`を両方設定しない限り呼ばれません。
- アプリは一般的な`OPENAI_API_KEY`を自動利用しません。別ツール用のキーで意図せず課金されることを防ぎます。
- OpenAI利用時は、低料金の[`gpt-5.4-nano`](https://developers.openai.com/api/docs/models/gpt-5.4-nano)と標準の[`gpt-5.6-luna`](https://developers.openai.com/api/docs/models/gpt-5.6-luna)を画面から選べます。価格は変わり得るため、利用前に各公式ページを確認してください。

OpenAIを明示選択した場合の使用量は[OpenAI Usage Dashboard](https://platform.openai.com/usage)で確認します。

## 必要環境

- Windows 10 version 2004 / build 19041 以降（Windows 11推奨）
- .NET 8 Desktop Runtime（開発時は .NET 8 SDK）
- PC版VRChat。デスクトップミラーは他のウィンドウに隠れていても構いません。最小化した場合だけSCAN中に短時間、自動復元されます
- 翻訳バックエンドは未選定。現段階ではキャプチャとOCRだけを無料で検証可能
- Windowsの英語OCR言語機能。未導入でもアプリは動作しますが、日本語認識器が英語を漢字や全角記号へ誤認識し、結果がほぼ読めなくなる場合があります
- VR内通知にはSteamVRとXSOverlay（デスクトップだけで使う場合は不要）
- VRコントローラーからSCANする暫定経路にはOVR Advanced Settings（キーボードなら不要）

このリポジトリの確認環境は、Windows build 26200 + WSL2 Ubuntu 24.04.4 + Windows .NET SDK 8.0.422です。Visual Studioは不要です。

### 英語OCR言語機能を導入する

英語の看板を読むには、Windows側に英語の文字認識（OCR）が必要です。Windowsの表示言語を英語へ変更する必要はありません。

1. Windowsの`設定`を開きます。
2. `時刻と言語` → `言語と地域`を開きます。
3. `English`を追加します。
4. Englishの`言語のオプション`を開きます。
5. `オプション機能`から`文字認識 (OCR)`を追加します。
6. VRChat Visual Assistantを再起動し、画面の`利用可能なOCR認識器`に`en`または`en-US`などが出ることを確認します。

現在Windows OCRが認識している言語タグは、Windows PowerShellからも確認できます。管理者権限は不要です。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-ocr-languages.ps1
```

PowerShellからのインストール方法はWindowsの版や導入状態に依存し、管理者権限も必要になるため、本READMEでは未検証のCapability名を指定して自動導入しません。

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

起動後は画面上部の「翻訳モデル」で`GPT-5.4 nano`または`GPT-5.6 Luna`を選びます。変更は次回のSCANから反映され、SCAN処理中は切り替えられません。選択はAPIキーと一緒に保存されず、アプリを再起動すると`VRCVA_OPENAI_MODEL`（未設定ならLuna）へ戻ります。

終了後、必要なら現在のPowerShellからキーを消します。

```powershell
Remove-Item Env:VRCVA_OPENAI_API_KEY
Remove-Item Env:VRCVA_TRANSLATION_PROVIDER
```

`.env`、`appsettings.Local.json`、ログ、キャプチャ、ビルド出力はGit管理対象外です。アプリは `.env` を自動読込しません。

## 通常の使い方

1. VRChatを起動します。PC側のVRChat窓は最小化して構いません。
2. VRChat Visual Assistantを起動します。
3. アプリのデスクトップ窓はそのままでも、最小化しても構いません。VRChat窓と重なってもキャプチャへ混ざりません。
4. VR内で英語を見て、`Ctrl+Shift+T`を押します。
5. XSOverlay起動中は約1秒の「SCAN中…」が表示され、その後に結果がVRCVAのSteamVRパネルへ表示されます。翻訳未設定なら英語OCR、OpenAIを明示設定した場合は日本語訳です。
6. 結果は自動では消えません。コントローラーでスクロールし、読み終えたら右上の「閉じる」を選びます。次のSCANを始めると古い結果は閉じます。

SCAN中もVRCVAの窓は消えたり再表示されたりしません。対象ウィンドウを直接取得するため、VRChatを前面化する必要もありません。

## Meta Quest 3S + SteamVRで結果パネルを使う

**XSOverlayのCreate OverlayやWindow Captureは作成しません。** 既に作った`VRChat Visual Assistant`の大きなオーバーレイは削除して構いません。結果パネルはVRCVAがSteamVRのOpenVR Overlay APIで直接表示します。XSOverlayのローカルExternal Message API（`127.0.0.1:42069/UDP`）へ送るのは、短い進行通知とエラーだけです。

1. Quest 3SをPCVR接続し、SteamVR、XSOverlay、VRChat、VRChat Visual Assistantを起動します。
2. `Ctrl+Shift+T`を1回押します。
3. 約1秒の「SCAN中…」に続いて「OCR結果（翻訳未設定）」または「日本語訳」が出ることを確認します。

開始通知は結果を待たせないよう1秒・小型です。結果パネルは閉じるまで残り、長文を省略しません。SteamVRパネルを初期化できない場合もSCANは失敗せず、結果はデスクトップ窓に残り、XSOverlayが起動中ならエラー通知を出します。

### 結果パネルだけを診断する

SteamVRを起動した状態で、Windows PowerShellから次を実行します。固定ダミー文だけを使い、キャプチャ、OCR、翻訳、外部API送信は行いません。

```powershell
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll --steamvr-overlay-check
```

ヘッドセット内でパネルを上下にスクロールし、右上の「閉じる」を選びます。PowerShellに`Overlay close event received.`と出れば、表示と閉じるイベントは成功です。2分以内に閉じられない場合は自動終了します。

### OpenVRアイミラー取得を診断する（第1段階スパイク）

これは開発用診断であり、通常のSCANはまだ従来のVRChatウィンドウを取得します。SteamVRとVRChatを起動した状態で次を実行すると、左右のアイミラーをメモリ上で1回ずつ取得し、寸法、DXGI形式、取得時間、現行ウィンドウとの解像度差を表示します。SteamVRを自動起動せず、画像も自動保存しません。

```powershell
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll `
  --openvr-eye-mirror-check
```

診断中は識別色のテストパネルが短時間だけ表示されます。診断は、パネル非表示を確認してコンポジタ境界を跨ぎ、遅延して返る旧合成フレームを破棄してから、採用フレームに識別色が残っていないことを自動判定します。

Questコントローラーで撮影タイミングを決める場合は`--wait-for-scan`を追加します。初期化後は最大3分待機し、OVR Advanced Settingsへ設定済みのSCAN操作（既定では`Ctrl+Shift+T`）を受信した瞬間から診断します。完了後はXSOverlayへ「ヘッドセットを外して構いません」と通知します。完了通知はキャプチャ後に送るため、取得画像へは写り込みません。

```powershell
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll `
  --openvr-eye-mirror-check --wait-for-scan
```

視野や左右差を目視するときだけ、空の保存先フォルダーを明示します。`eye-left.png`、`eye-right.png`、`window-current.png`が作成されます。既存ファイルは上書きしません。画像にはVRChatの表示内容が含まれるため、確認後に削除し、Gitへ追加しないでください。

```powershell
New-Item -ItemType Directory C:\Temp\vrcva-eye-check
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll `
  --openvr-eye-mirror-check --wait-for-scan `
  --save-eye-mirror C:\Temp\vrcva-eye-check
```

### 一度だけ: QuestコントローラーへSCANを割り当てる

導入済みのOVR Advanced Settingsには、VRコントローラー操作からキーボードショートカットを送る公式機能があります。VRCVAはこれを暫定のコントローラートリガーとして利用し、VRChatやXSOverlayへ入力を注入しません。以下を一度設定すれば、VRプレイ中に物理キーボードへ触れる必要はありません。

1. SteamVRを終了し、タスクマネージャー上の`AdvancedSettings.exe`も終了したことを確認します。
2. Windows PowerShellで、まず変更なしの確認を実行します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ovras-trigger.ps1
```

3. 表示内容を確認後、バックアップ付きでShortcut Oneを結果パネル操作、Shortcut TwoをSCAN、Shortcut Threeをモデル切替へ設定します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\configure-ovras-trigger.ps1 -Apply
```

4. SteamVRを再起動した後、Windowsのブラウザで次を開きます。`localhost`では白画面になった実機があるため、必ず`127.0.0.1`を使います。

```text
http://127.0.0.1:27062/dashboard/controllerbinding.html?desktop=1&app=steam.overlay.1009850
```

5. `OVR Advanced Settings`の現在のバインドを編集し、`Misc`アクションへ進みます。
6. 結果パネルの操作切替には`KeyboardOne`（内部出力`/actions/misc/in/keyboardone`）を未使用の操作へ割り当てます。結果は既定で非インタラクティブなので、表示したままVRChat内を移動できます。読む位置を動かす、または閉じるときだけ1回押してレーザー操作をONにし、読み終わったらもう1回押してOFFへ戻します。パネルを閉じた場合も自動的にOFFへ戻ります。
7. SCANには`KeyboardTwo`（内部出力`/actions/misc/in/keyboardtwo`）を割り当てます。この実機では左グリップと右グリップのChordを作り、両方とも`Button Single`にして、左右同時グリップでSCANできることを確認済みです。`Button Click`へ変える必要はありません。
8. OpenAI利用時だけ、別の未使用操作へ`KeyboardThree`を割り当てます。これがnano/Luna切替です。
9. 保存表示が「アップロード中」のままでもローカルバインドが自動保存されている場合があります。ページを閉じ、VR内で操作して反応するかを先に確認してください。GitHub等の再認証は関係ありません。

OVR Advanced SettingsのTouch既定バインドではB/YがSpace Turn/Dragに使われるため、既存操作を上書きせず、Long HoldやChordなどの空いている操作を選んでください。補助スクリプトはINI内の3値だけを変更し、同じフォルダーに日時付きバックアップを作ります。この実機では変更前の`KeyboardOne`が`Ctrl+Shift+M`なので、`-Apply`によりVRCVA用の`Ctrl+Shift+I`へ置き換わります。SteamVR側のコントローラーバインドは自動変更しません。現在の左右同時グリップを別操作へ変える場合も、上の編集画面で`KeyboardTwo`の入力だけを変更します。

### 設定用環境変数

| Variable | Default | Meaning |
| --- | --- | --- |
| `VRCVA_TRANSLATION_PROVIDER` | `none` | 未選定。現在実装済みの任意値は`openai`のみ |
| `VRCVA_OPENAI_API_KEY` | none | OpenAIを明示選択した場合だけ読む専用APIキー |
| `VRCVA_OPENAI_MODEL` | `gpt-5.6-luna` | 起動時の翻訳モデル。OpenAI利用中は画面からnano/Lunaへ一時変更可能 |
| `VRCVA_OPENAI_ENDPOINT` | `https://api.openai.com/v1/responses` | Responses API endpoint |
| `VRCVA_OPENAI_TIMEOUT_SECONDS` | `25` | 1〜120秒 |
| `VRCVA_HOTKEY` | `Ctrl+Shift+T` | 修飾キーを1つ以上含むグローバルホットキー |
| `VRCVA_PANEL_INTERACTION_HOTKEY` | `Ctrl+Shift+I` | 結果パネルのレーザー操作をON/OFFするグローバルホットキー |
| `VRCVA_MODEL_TOGGLE_HOTKEY` | `Ctrl+Shift+G` | OpenAI利用中にnano/Lunaを交互に切り替えるホットキー |

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

実際のVRChatからウィンドウ単体キャプチャが利用できるか、画像保存・OCR本文表示なしで確認できます。

```powershell
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll `
  --capture-vrchat-check
```

成功時は寸法、PNGのバイト数、`source: windows-graphics-capture-client-area`だけを表示します。

明示的にキャプチャ内容を調べるときだけ、次の診断オプションでPNGを保存できます。これは通常動作では使いません。保存先の画像には画面内容が含まれるため、確認後に自分で削除してください。

```powershell
dotnet .\src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.dll `
  --capture-vrchat-ocr --save-capture C:\Temp\vrcva-debug.png
```

## 英語の看板なのに結果に漢字が混ざる

英語OCR言語機能が未導入で、日本語認識器が英語を読んでいる可能性が高い症状です。例えば英単語の途中に`代`や`取`などの漢字、`「`や`・`などの全角記号が混ざります。テキスト領域の検出やSCAN自体は成功扱いになるため、失敗コードは表示されません。

アプリ上部の`使用するOCR認識器 / 利用可能`を確認してください。`ja`や`ja-JP`だけで、`en`または`en-*`がなければ、上の「英語OCR言語機能を導入する」の手順で文字認識（OCR）を追加し、アプリを再起動します。Windowsの表示言語を英語へ変更する必要はありません。日本語OCRを意図して使う場合は、警告が表示されたままでもSCANを続けられます。

## 失敗時の切り分け

| 表示/症状 | 段階 | 確認すること |
| --- | --- | --- |
| `CaptureTargetNotFound` | Capture | Windows版 `VRChat.exe` が起動し、通常ウィンドウがあるか |
| 自動復元エラー | Capture | VRChatが応答しているか確認し、一度だけ手動復元して再試行 |
| 別窓やタイトルバーが写る | Capture | 最新版をビルド・再起動し、`--capture-vrchat-check`のsourceが`windows-graphics-capture-client-area`か確認 |
| 黒い/一部だけ写る | Capture | `--capture-vrchat-check`を実行し、VRChatが応答しているか、HDRを一時的に切ると変わるか確認 |
| `OcrUnavailable` | OCR | Windowsの言語オプションにOCR機能があるか。診断コマンドが列挙する言語タグを確認 |
| `NoTextDetected` | OCR | 通常OCRと全画面3帯の強化OCRの両方で文字を取れなかった状態。文字へ少し近づく、ミラー解像度を上げる、画像診断で同じ場面を試す |
| `OCR結果（翻訳未設定）` | Completed | 正常です。無料・外部送信なしの既定状態では認識した英語を表示します |
| OpenAI専用キー未設定 | Translation | OpenAIを使う場合だけ、同じPowerShellに`VRCVA_TRANSLATION_PROVIDER=openai`と`VRCVA_OPENAI_API_KEY`があるか |
| 認証/レート制限 | Translation | APIキー権限、課金状態、利用上限。キー値自体はログに出ない |
| タイムアウト | Translation | ネットワークと `VRCVA_OPENAI_TIMEOUT_SECONDS` |
| ホットキー登録失敗 | Trigger | 他アプリとの競合。`VRCVA_HOTKEY` を変更して再起動 |
| XSOverlay通知が出ない | Rendering | XSOverlayが起動中か確認。Window Captureは不要。デスクトップ窓に結果が出るならSCAN自体は成功 |
| SteamVR結果パネルが出ない | Rendering | SteamVRを先に起動し、上の`--steamvr-overlay-check`を実行。失敗しても結果はデスクトップ窓に残る |
| 結果パネルを操作できない | Rendering | `--steamvr-overlay-check`でスクロールと「閉じる」を分離確認。SteamVR Dashboardのコントローラーポインターが他パネルで反応するかも確認 |
| 結果パネル表示中にVRChatを操作できない | Rendering | `Ctrl+Shift+I`またはOVRASの`KeyboardOne`をもう一度押し、結果パネル操作をOFFにする。パネル自体は閉じずに表示を続けます |
| OVRAS操作が反応しない | Trigger | 補助スクリプト適用後にSteamVRを再起動したか、Shortcut Twoをコントローラーへバインドしたか |

ログは次にあります。

```text
%LOCALAPPDATA%\VrcVa\logs\vrcva-YYYYMMDD.log
```

相関IDで1回のSCANを追跡できます。ログには画面・テキスト・キーを記録しないため、OCR精度の確認は明示的な画像診断を使います。

## 現在の制約

- 取得対象はVRChatのHMD eye textureではなく、Windowsデスクトップ上のVRChatクライアント描画領域です。Windowsのタイトルバーと枠は除外します。
- Windows Graphics Captureで対象ウィンドウを直接取得するため、他ウィンドウの遮蔽には依存しません。ただしVRChatを最小化した場合は描画再開のため短時間だけ自動復元します。
- 現在のVR結果表示はHMD相対のSteamVRパネルです。利用者が閉じるか次のSCANで置き換えるまで保持します。既定ではVRChat操作を妨げない非インタラクティブ表示で、`Ctrl+Shift+I`またはOVRASの`KeyboardOne`を押した間だけスクロール・閉じる操作が可能です。手首追従位置の調整はまだ実装していません。
- VRコントローラーはOVR Advanced SettingsからOSショートカットへ橋渡しする暫定方式です。一度バインドすればVR中の物理キーボード操作は不要です。内部のキー橋渡しもなくすVRCVAネイティブSteamVR入力/OSCQueryは後続です。
- ローカルOCRは通常認識が弱い場合、視線中央だけでなく画面全体を重なり付きの横帯3枚に分けて自動再認識します。それでも小さい文字、遠近、装飾フォント、発光、低コントラストでは精度が下がります。
- 翻訳バックエンドは未選定で、既定状態では日本語訳を生成しません。
- OpenAI APIの実通信は、リポジトリやCIに秘密を置かないため利用者が明示選択した場合だけ行います。自動テストは偽HTTP応答を使います。

## 設計とロードマップ

- [DESIGN.md](DESIGN.md): 課題、方式比較、アーキテクチャ、プライバシー、将来拡張
- [TASKS.md](TASKS.md): 実装順、成功条件、実機テスト、OSC/OpenVR以降のタスク

次の実機ゲートは、強化OCRを同じ場面で数回試し、認識内容と結果までの体感時間を記録することです。PCで別ウィンドウを前面表示した状態と、VRChatを最小化した状態も各1回確認します。内部のキー橋渡しをなくすSteamVR入力/OSCQueryはその後です。翻訳バックエンドは引き続き所有者の明示判断待ちです。

## ライセンス

まだ選択していません。公開リポジトリであっても、ライセンスファイルが追加されるまでは再利用許諾を意味しません。所有者が選択した後に追加します。
