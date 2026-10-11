# VRChat Visual Assistant

VRChat Visual Assistant（VRCVA）は、VRChat を中断せず、ユーザーがその場で見ているものや抱いた疑問を AI への入力として扱い、結果を VR 内へ返すための外部 Windows アプリ／共通 AI 基盤です。画面や音声からの入力、実行制御、結果表示、資格情報・通信を共通化し、VR 内で操作を完結できることを目的としています。

現在、この基盤上で利用できる実用機能は「画面の読み取りと翻訳」と「音声入力と動画検索」です。画面の読み取りでは、必要なときに PCVR 画面を 1 回だけ取得し、Windows の OCR で英語を認識します。OpenAI API を設定すると日本語翻訳も利用できます。動画検索では、音声から YouTube 動画を探し、選んだ動画 URL をクリップボードへコピーします。

どちらの機能もデスクトップ画面のほか、SteamVR 内の左手首ランチャーから操作できます。

> [!IMPORTANT]
> このプロジェクトは開発中の非公式ツールです。VRChat Inc.、Valve Corporation、OpenAI との提携・承認を示すものではありません。VRChat への DLL 注入、クライアント改変、メモリ読み取りは行いません。

## 主な機能

### 画面の読み取りと翻訳

- `SCAN` ボタンまたは既定の `Ctrl+Shift+T` で、その時点の VRChat 画面を 1 回取得
- SteamVR が利用できる場合は片眼のアイミラー、利用できない場合は `VRChat.exe` のウィンドウを取得
- Windows 内蔵 OCR で英語をローカル認識
- OpenAI API キーが未登録なら、認識した英語だけを表示
- OpenAI API キーを登録した場合は、OCR したテキストを日本語へ翻訳
- 認識結果と翻訳結果をデスクトップまたは SteamVR のパネルに表示
- 結果パネルを左手・右手・HMD 正面へ配置し、VR 内で位置と大きさを調整
- ローカル画像を指定した OCR 診断

### 音声入力と動画検索

- Windows の既定の通信マイクから録音し、OpenAI の音声文字起こしで認識文を作成
- 認識文をそのまま使う検索、または OpenAI で検索語を整える検索を選択
- 固定版 `yt-dlp` で YouTube の候補を最大 10 件取得し、5 件ずつ表示
- 選んだ候補の URL を Windows のクリップボードへコピー
- デスクトップと SteamVR の両方から操作

VRCVA は URL をワールドへ自動入力したり、動画を自動再生したりしません。コピー後の貼り付けと再生は利用者が行います。

## 配布状況

現時点で、このリポジトリの [GitHub Releases](https://github.com/Na2ki-BB/vrchat-visual-assistant/releases) に一般利用者向けの配布物はありません。インストーラー、署名済み実行ファイル、自動更新、CI からダウンロードできるアプリ ZIP も提供していません。

利用するには次のいずれかが必要です。

1. このリポジトリを取得し、Windows でソースからビルドする
2. リポジトリ所有者から個別に提供された自己完結 ZIP を使う

第三者が配布する出所不明の実行ファイルや、README に記載していないダウンロード先は使用しないでください。

## 動作条件

### 画面 OCR・翻訳

- Windows 10 version 2004 / build 19041 以降（Windows 11 推奨）
- Windows x64
- Windows 版 VRChat
- 英語の Windows OCR 言語機能
- VR 内のランチャーと結果パネルを使う場合は SteamVR
- 日本語翻訳を使う場合だけ OpenAI API キーと API の利用料金

SteamVR がなくても、デスクトップ画面から VRChat ウィンドウの OCR と結果表示を利用できます。

### 音声・動画検索

上記に加えて、次が必要です。

- Windows の既定の通信デバイスとして設定したマイク
- OpenAI API キーと API の利用料金
- 指定された固定版 `yt-dlp.exe`

YouTube Data API キー、Python、FFmpeg、YouTube ログイン、Cookie は不要です。

### ソースからビルドする場合

- [Git for Windows](https://git-scm.com/downloads/win)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（`global.json` の基準は 8.0.422）
- Windows PowerShell

Visual Studio は必須ではありません。

## インストール

### 1. ソースを取得する

Windows PowerShell で作業用フォルダーを開き、次を実行します。

```powershell
git clone https://github.com/Na2ki-BB/vrchat-visual-assistant.git
Set-Location .\vrchat-visual-assistant
```

既存の clone に未コミットの変更がある場合は、上書きや破棄をせず、別のフォルダーへ新しく clone してください。

### 2. ビルドと自動テストを実行する

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

このスクリプトは NuGet パッケージの復元、Release ビルド、単体テストを順に実行します。初回の復元にはインターネット接続が必要です。実マイク、OpenAI API、YouTube への送信は行いません。

成功すると、次の実行ファイルができます。

```text
src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.exe
```

### 3. 起動する

SteamVR の機能を使う場合は SteamVR を先に起動し、次を実行します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

`run.ps1` はビルド済みの実行ファイルを起動します。ビルドは行いません。

### 個別提供の自己完結 ZIP を使う場合

ZIP 内の `VRCVA` フォルダーを任意の場所へ展開し、フォルダー内の `VrcVa.exe` を起動します。.NET Runtime は同梱されています。

SteamVR の自動起動登録には実行ファイルの場所が保存されます。初回設定後にフォルダーを移動した場合は、移動先の `VrcVa.exe` から設定をやり直してください。

## 初回設定

初回起動時に 3 画面の案内が表示されます。

1. **SteamVR 連動**: SteamVR 起動時に VRCVA も起動するか選びます。VRCVA 自身が SteamVR を起動することはありません。
2. **英語 OCR**: Windows に英語 OCR が導入されているか確認します。
3. **日本語翻訳**: 翻訳を使う場合だけ OpenAI API キーを保存します。空欄のまま完了すると、画像と OCR は PC 内だけで処理されます。

SteamVR が停止中だと自動起動の登録が保留になることがあります。その場合は VRCVA を終了し、SteamVR を起動または再起動してから VRCVA をもう一度起動してください。

### 英語OCR言語機能を導入する

英語の看板を読むには、Windows に英語の「文字認識 (OCR)」が必要です。Windows の表示言語を英語へ変更する必要はありません。

1. Windows の `設定` → `時刻と言語` → `言語と地域` を開く
2. `English` を追加する
3. English の `言語のオプション` を開く
4. `オプション機能` から `文字認識 (OCR)` を追加する
5. VRCVA を再起動し、画面上部の「利用可能な OCR 認識器」に `en` または `en-US` などが表示されることを確認する

リポジトリから起動している場合は、次のコマンドでも利用可能な OCR 言語を確認できます。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-ocr-languages.ps1
```

## 画面を読み取る

### デスクトップから使う

1. Windows 版 VRChat を起動する
2. VRCVA を起動する
3. 読み取りたい文字を視界に入れる
4. VRCVA の `SCAN` を押すか、`Ctrl+Shift+T` を押す
5. 「OCR 結果（英語）」と、翻訳を有効にしている場合は「日本語訳」を確認する

1 回の操作につき 1 回だけ画面を取得します。常時録画や定期キャプチャは行いません。

### SteamVR から使う

1. SteamVR、Windows 版 VRChat、VRCVA の順に起動する
2. 左手首の小さい `VRCVA` ランチャーを表示する
3. 右手の水色の照準点を操作項目へ合わせ、トリガーで `翻訳 SCAN` を選ぶ
4. SteamVR 内の結果パネルで内容を確認する

結果パネルを表示したままでも、VRChat の歩行用入力は維持されます。右トリガーは VRChat にも届くため、ワールド側の操作が同時に発生する場合があります。

結果パネルの追従先や位置は、デスクトップ画面の「VR 結果パネルの配置」から変更できます。VR 内では結果パネル下部の操作欄と右端のスクロールバーを使います。

### 翻訳を有効にする

VRCVA の「画面の翻訳」タブで OpenAI API キーを入力し、「暗号化して保存」を押します。キーは Windows 資格情報マネージャーの `VrcVa/OpenAIApiKey` に保存され、次回の SCAN から反映されます。

保存キーを削除し、`VRCVA_*` の一時翻訳設定もない場合は、以後ローカル OCR のみになります。一般的な `OPENAI_API_KEY` 環境変数は自動利用しません。

## 音声から動画を探す

> [!WARNING]
> 録音の「停止して文字起こし」または初期 30 秒の自動停止は、音声を OpenAI へ送信し、API 利用料金が発生する操作です。送信せずにやめる場合は「中止」を選んでください。送信開始後の中止は、処理や料金の取り消しを保証しません。

### 1. yt-dlp を配置する

動画検索には、Windows x64 用の `yt-dlp` **2026.08.19** を使用します。VRCVA は `yt-dlp` を自動でダウンロード、インストール、更新しません。

1. [yt-dlp 2026.08.19 の公式 Release](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19) から `yt-dlp.exe` と `SHA2-256SUMS` を取得する
2. 次の場所へ `yt-dlp.exe` を置く

   ```text
   %LOCALAPPDATA%\VrcVa\tools\yt-dlp\2026.08.19\yt-dlp.exe
   ```

3. Windows PowerShell で SHA-256 を確認する

   ```powershell
   Get-FileHash -Algorithm SHA256 -LiteralPath "$env:LOCALAPPDATA\VrcVa\tools\yt-dlp\2026.08.19\yt-dlp.exe"
   ```

4. 次の値と一致することを確認する

   ```text
   66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a
   ```

別バージョン、PATH 上の実行ファイル、リンクやジャンクション経由のファイルは使用されません。VRCVA は起動時ではなく検索時に、配置場所、ハッシュ、バージョンを検証します。

### 2. 音声入力を有効にする

1. Windows の既定の通信マイクを設定する
2. Windows のマイクアクセスと、デスクトップアプリのマイクアクセスを許可する
3. VRCVA の「音声入力」タブを開く
4. 外部送信に関する説明を確認し、音声入力を有効にする
5. OpenAI API キーを入力して「専用キーを保存」を押す

音声用のキーは Windows 資格情報マネージャーの `VrcVa/OpenAI/Voice` に保存されます。翻訳用キーや環境変数は音声認識に代用されません。

### 3. 録音して検索する

1. 「録音開始 / 録り直し」を押して話す
2. 「停止して文字起こし」を押す
3. 認識文を確認する
4. 次のいずれかを選ぶ
   - **そのまま検索**: 認識文を検索語として YouTube へ送る
   - **解釈して検索**: 認識文を OpenAI へ送り、整えた検索語を YouTube へ送る
5. 候補を選んで URL をクリップボードへコピーする
6. VRChat の動画プレイヤーへ自分で貼り付ける

「解釈して検索」には、音声用キーとは別に「画面の翻訳」タブへ保存したテキスト用キーが必要です。翻訳と同じキーを使用できます。

## 外部通信・データ・料金

初期状態では、画面の取得と OCR は PC 内で完結します。外部通信が発生する機能は次のとおりです。

| 操作 | 送信先 | 送る内容 | 料金・注意 |
| --- | --- | --- | --- |
| OpenAI 翻訳 | OpenAI Responses API | OCR 済みテキスト。画像は送信しない | 任意・従量課金。要求は `store: false` |
| 音声文字起こし | OpenAI Audio Transcriptions API | 録音した音声 | 任意・従量課金。別の同意と専用キーが必要 |
| そのまま検索 | YouTube（固定版 `yt-dlp` 経由） | 認識文を基にした検索語 | OpenAI の追加送信はないが、直前の音声文字起こしは有料 |
| 解釈して検索 | OpenAI Responses API、続いて YouTube | 認識文、続いて生成された検索語 | 音声文字起こしに加え、検索語の解釈も従量課金 |
| 候補画像 | `i.ytimg.com` / `img.youtube.com` | サムネイル取得リクエスト | YouTube の外部通信 |
| XSOverlay 通知 | `127.0.0.1` | エラー、モデル切替、明示診断などの短い状態通知 | 同じ PC 内だけ。通常 SCAN の OCR・翻訳本文は送信しない |
| OSC トリガー | この PC の OSC / OSCQuery | トリガー値 | 既定で無効。DNS-SD のサービス名とポートは LAN に広告される |

OpenAI API の利用料金は ChatGPT のサブスクリプションとは別です。料金、モデル、データ保持条件は変更されるため、利用前に [OpenAI API Pricing](https://developers.openai.com/api/docs/pricing)、[API のデータ管理](https://developers.openai.com/api/docs/guides/your-data)、[Usage Dashboard](https://platform.openai.com/usage) を確認してください。VRCVA の上限は 1 回の起動中の誤操作を抑えるためのもので、月額予算を保証しません。

既定の上限は次のとおりです。

- 音声: 合計 300 秒または 30 送信
- 翻訳: 10 送信
- 検索語の AI 解釈: 10 送信

失敗、キャンセル、手動再送でも、通信を開始した処理は計数されることがあります。自動再試行は行いません。アプリを再起動するとカウンターは戻るため、API 側でも利用上限を設定してください。VRCVA 専用の OpenAI Project を作り、[spend limit](https://developers.openai.com/api/docs/guides/spend-limits) を設定することを推奨します。利用額の集計には遅延があり、設定額をわずかに超える場合があります。

### 保存されるもの

- 一般設定: `%LOCALAPPDATA%\VrcVa\settings.json`
- API キー: Windows 資格情報マネージャー
- 内容を含まない診断ログ: `%LOCALAPPDATA%\VrcVa\logs\vrcva-YYYYMMDD.log`

通常動作では、取得画像、音声、認識文、検索語、候補、OCR 本文、翻訳本文をファイルやログへ保存しません。成功した音声は文字起こし後に破棄します。失敗した音声は再送のため既定 120 秒だけメモリに保持し、中止・閉じる・録り直し・終了で破棄します。

コピーした動画 URL は Windows のクリップボードに残り、OS の履歴や同期対象になる場合があります。VRCVA は終了時にクリップボードを消去しません。

詳しい境界は [SECURITY.md](SECURITY.md) を参照してください。

## 失敗時の切り分け

| 症状・表示 | 確認すること |
| --- | --- |
| 英語の途中に漢字や全角記号が混ざる | 英語 OCR が未導入の可能性があります。「利用可能な OCR 認識器」を確認してください |
| `CaptureTargetNotFound` | Windows 版 `VRChat.exe` と通常ウィンドウが存在するか確認してください |
| `OcrUnavailable` | Windows の言語オプションに英語 OCR があるか確認してください |
| `NoTextDetected` | 文字へ近づく、ミラー解像度を上げる、装飾や発光の少ない文字で試してください |
| 「OCR 結果（翻訳未設定）」 | 正常です。OpenAI 未設定時は認識した英語だけを表示します |
| OpenAI キー未設定・認証・レート制限 | 対象機能のキー、API 権限、API 側の支払い設定と利用上限を確認してください |
| SteamVR の結果パネルが出ない | SteamVR を先に起動してください。処理結果はデスクトップ画面にも残ります |
| 左手首ランチャーが出ない | VRCVA を終了し、SteamVR → VRCVA の順で起動し直してください。旧 VRCVA プロセスが残っていないかも確認してください |
| 録音できない | 音声同意、音声専用キー、既定の通信マイク、Windows のマイク許可、物理ミュートを確認してください |
| 動画検索を設定できない | `yt-dlp.exe` の場所、バージョン、SHA-256 を確認してください |
| ホットキーを登録できない | 他アプリとの競合を避けるため、`VRCVA_HOTKEY` を変更して再起動してください |
| 結果パネル表示中に VRChat を操作できない | VRCVA を終了して入力を解放し、再現手順を記録してください |

ログには相関 ID と失敗段階が記録されますが、画面内容、音声、認識文、検索語、API キーは記録されません。機密情報や取得画像を公開 Issue に添付しないでください。脆弱性は [SECURITY.md](SECURITY.md) の方法で報告してください。

## 現在の制約

- Quest 単体で動く Android アプリではありません。Windows PC に接続した PCVR 用です。
- SteamVR の通常 SCAN は片眼だけを取得します。両眼合成は行いません。
- 小さい文字、遠近の強い文字、装飾フォント、発光、低コントラストでは OCR 精度が下がります。
- 翻訳、音声認識、検索語の解釈には OpenAI API が必要です。完全ローカル翻訳・音声認識は実装していません。
- 動画検索は YouTube 側や固定版 `yt-dlp` の互換性変更により失敗する可能性があります。
- インストーラー、コード署名、自動更新、一般公開バイナリはありません。
- 実機確認は限られた Windows + SteamVR + Quest 3S 環境で行われており、すべての PC、HMD、接続方式、ワールドを保証しません。

確認済みの環境、テスト結果、未確認項目は [TASKS.md](TASKS.md) に記録しています。

## 高度な設定

通常利用では変更不要です。環境変数は VRCVA を起動するプロセスにだけ設定してください。

| 環境変数 | 既定値 | 用途 |
| --- | --- | --- |
| `VRCVA_TRANSLATION_PROVIDER` | 未指定（保存キーあり: `openai` / なし: `none`） | 一時設定で `none` または `openai` を明示 |
| `VRCVA_OPENAI_API_KEY` | なし | 一時的な翻訳専用キー。一般的な `OPENAI_API_KEY` は使わない |
| `VRCVA_OPENAI_MODEL` | `gpt-5.6-luna` | 翻訳モデル。対応値は `gpt-5.6-luna` / `gpt-5.4-nano` |
| `VRCVA_OPENAI_ENDPOINT` | 公式 Responses API | 開発用の翻訳エンドポイント |
| `VRCVA_OPENAI_TIMEOUT_SECONDS` | `25` | 翻訳のタイムアウト。1〜25 秒 |
| `VRCVA_HOTKEY` | `Ctrl+Shift+T` | SCAN のグローバルホットキー |
| `VRCVA_MODEL_TOGGLE_HOTKEY` | `Ctrl+Shift+G` | 翻訳モデル切替のホットキー |
| `VRCVA_OPENVR_EYE` | `left` | `left` または `right` |
| `VRCVA_OSC_TRIGGER_ENABLED` | `false` | VRChat OSC / OSCQuery トリガーを有効化 |
| `VRCVA_OSC_TRIGGER_ADDRESS` | `/avatar/parameters/VRCVA_Scan` | Avatar Parameter の完全一致アドレス |
| `VRCVA_OSC_TRIGGER_DEBOUNCE_MS` | `750` | 連続入力を無視する時間。100〜10000 ms |
| `VRCVA_OSC_TRIGGER_TYPE` | `bool` | `bool` または `int` |
| `VRCVA_OSC_TRIGGER_INT_VALUE` | `1` | `int` 使用時に SCAN と判定する値 |

資格情報マネージャーへ保存したキーは、公式の `https://api.openai.com/v1/responses` にだけ送信されます。`VRCVA_OPENAI_ENDPOINT` で送信先を変更できるのは、`VRCVA_OPENAI_API_KEY` を同じプロセスに明示した開発用構成だけです。この場合、OCR テキストは指定した endpoint へ送信されるため、運営者とデータ取扱条件を自分で確認してください。

OSC と OVR Advanced Settings を使う回復経路、個別診断コマンド、実機確認手順は [ローカル導入・実機確認ガイド](docs/LOCAL-SETUP-AND-TEST.md) を参照してください。

## 開発者向け

### ビルド・テスト・フォーマット確認

Windows PowerShell でリポジトリルートから実行します。

```powershell
dotnet --info
dotnet restore .\VRChatVisualAssistant.sln
dotnet build .\VRChatVisualAssistant.sln -c Release --no-restore
dotnet test .\VRChatVisualAssistant.sln -c Release --no-build --no-restore
dotnet format .\VRChatVisualAssistant.sln --verify-no-changes --no-restore
```

まとめてビルドとテストを行う場合は `scripts\build.ps1`、ビルド済みアプリを起動する場合は `scripts\run.ps1` を使用できます。

### 自己完結 ZIP を作る

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1 -Version dev
```

成果物は `artifacts\beta\VRCVA-beta-dev-win-x64.zip` です。このスクリプトは Windows x64 用の .NET Runtime を同梱しますが、テスト、コード署名、公開は行いません。

### 関連資料

- [DESIGN.md](DESIGN.md): 設計資料の入口
- [共通 AI 基盤設計](docs/DESIGN-PLATFORM.md): UI、入力、通信、資格情報、プライバシー
- [日本語翻訳機能設計](docs/DESIGN-JAPANESE-TRANSLATION.md): キャプチャ、OCR、翻訳
- [動画検索機能設計](docs/DESIGN-VIDEO-SEARCH.md): 音声認識後の検索、候補、URL コピー
- [TASKS.md](TASKS.md): 実装・検証履歴と残件
- [CONTRIBUTING.md](CONTRIBUTING.md): 開発時の注意
- [SECURITY.md](SECURITY.md): セキュリティ境界と脆弱性報告

## ライセンス

ライセンスはまだ選択されていません。公開リポジトリであっても、ライセンスファイルが追加されるまでは、コードや文書の再利用許諾を意味しません。外部からのコントリビューションも、ライセンスと貢献条件が整うまで待ってください。
