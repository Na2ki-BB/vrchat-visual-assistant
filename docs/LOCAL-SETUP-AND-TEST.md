# Windows PCへの導入と実API・Questでの確認

クラウドで準備したソースを自分のWindows PCへ持ってきて、音声から動画検索・URLコピー・VRChatでの手動貼り付けまでを一続きで確認するガイドです。細かい手動fakeテストを先に繰り返す必要はありません。

**VRCVAはWindowsで動く外部SteamVRオーバーレイです。** アバターへSDKやPrefabを入れる、Unityからアップロードする、VRChatのファイルを書き換える手順はありません。Quest単体のAndroid版には導入できず、Quest 3SをWindows PCへ接続するPCVRで使います。通常の腕メニューにはOSCやOVR Advanced Settingsの追加設定も不要です。

- [1. 準備するもの](#1-準備するもの)
- [2. 既存環境を残してソースを取得](#2-既存環境を残してソースを取得)
- [3. Windowsでビルド・起動](#3-windowsでビルド起動)
- [4. 動画検索用yt-dlpを配置](#4-動画検索用yt-dlpを配置)
- [5. マイクとキーを設定](#5-マイクとキーを設定)
- [6. VRChatで一続きの実機確認](#6-vrchatで一続きの実機確認)
- [7. 中止・終了・元の版へ戻す](#7-中止終了元の版へ戻す)
- [確認済みと未確認](#確認済みと未確認)

## 1. 準備するもの

- Windows x64。Windows 10 build 19041以降、Windows 11推奨
- ソースを取得する [Git for Windows](https://git-scm.com/downloads/win) と、[.NET 8 SDKのWindows x64版](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。本書の基準は `global.json` の **8.0.422**。Visual Studioは不要
- 普段使っているQuestのPC接続、[SteamVR](https://store.steampowered.com/app/250820/SteamVR/)、[Windows版VRChat](https://store.steampowered.com/app/438100/VRChat/)。Quest接続をまだ準備していなければ [Meta公式のLink / Air Link案内](https://www.meta.com/help/quest/509273027107091/) を参照
- 自分の声だけで試せる場所と、本人が入力するOpenAI APIキー。既存のキーが保存されていれば再入力不要

XSOverlayはエラー通知の補助として任意です。WPF窓をXSOverlayのWindow Captureへ登録する必要はありません。

助手へ任せられるのは、接続後のソース取得、既存変更の確認、ビルド、配置・hash確認、起動準備と内容を含まない結果整理です。**キーの入力、音声送信の同意、Windowsの権限許可、ヘッドセット内の操作とワールドへの貼り付けは本人が行います。** 新しいアカウント・課金設定や、実APIへの送信を準備作業だけで承認した扱いにはしません。

## 2. 既存環境を残してソースを取得

1. 以前のVRCVAを終了します。元のソース、実行フォルダー、未コミットの変更は残します。既存フォルダーへ上書きせず、**別の新しいフォルダー**で準備します
2. 保存済みの配置・同意・利用上限を戻せるよう、更新前の設定をPC内だけへコピーします。以下はWindows PowerShellで実行します。キーはこのファイルには含まれず、資格情報マネージャーから書き出す必要もありません

   ```powershell
   $backup = Join-Path $env:LOCALAPPDATA ("VrcVa-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
   New-Item -ItemType Directory -Path $backup | Out-Null
   $settings = Join-Path $env:LOCALAPPDATA "VrcVa\settings.json"
   if (Test-Path -LiteralPath $settings) {
       Copy-Item -LiteralPath $settings -Destination (Join-Path $backup "settings.json")
   }
   ```

3. 作業用フォルダーをPowerShellで開き、次を実行します。`vrchat-visual-assistant-local` が既にあるなら別名にします。本書ではCIで確認した実装commitを固定し、途中で別の版へ変えません

   ```powershell
   git clone https://github.com/Na2ki-BB/vrchat-visual-assistant.git vrchat-visual-assistant-local
   if ($LASTEXITCODE -ne 0) { throw "ソース取得に失敗しました" }
   Set-Location .\vrchat-visual-assistant-local
   git checkout --detach f8c5da1babcef2b547dd778ab807d31d72147f23
   if ($LASTEXITCODE -ne 0) { throw "確認対象のcommitを取得できませんでした" }
   git rev-parse HEAD
   git status --short
   ```

   HEADが `f8c5da1babcef2b547dd778ab807d31d72147f23`、最後の表示が空であることを確認します。これは元のブランチを切り替えずに試すためのdetached HEADです。後で開発する場合は別ブランチを作ります

既存cloneを使う場合も、まず `git status --short --branch` を確認し、作業中なら新しいcloneを使います。`reset --hard`、`clean`、変更の破棄や、未確認のstashは使いません。クラウドの `bin/obj`、`.env`、キー、設定、ログをPCへ丸ごとコピーする必要はありません。

設定は `%LOCALAPPDATA%\VrcVa\settings.json`、キーは同じWindowsユーザーの資格情報マネージャーに残ります。ソースや実行フォルダーを替えても共通なので、既存の音声同意・翻訳設定を含めて起動後に確認してください。

## 3. Windowsでビルド・起動

リポジトリルートの**Windows PowerShell**から実行します。

```powershell
dotnet --version
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました。起動せず結果を確認してください" }
```

`build.ps1` はrestore → Release build → 自動の単体テストをまとめて実行します。実マイク・API・YouTubeへは送信しません。手動でfakeを一つずつ動かす手順ではありません。初回restoreにはパッケージ取得の通信が必要です。

普段のQuest PC接続とSteamVRを先に起動してから、次でVRCVAを開きます。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

`run.ps1` はビルドせず、既存の `src\VrcVa.Windows\bin\Release\net8.0-windows10.0.19041.0\VrcVa.exe` を起動します。出力がなければ失敗します。古いVRCVAが動いている場合は終了してから起動してください。

初回だけ3画面の案内が出ます。SteamVR連動は希望する場合だけ有効にします。`登録保留`ならVRCVAを終了し、SteamVRを起動または再起動して同じ実行ファイルを開きます。VRCVA自身はSteamVRを起動しません。

### 普段使う自己完結ZIPを作る場合だけ

上のビルド成功後、同じWindows PCで実行します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-beta.ps1 -Version local-f8c5da1
```

`artifacts\beta\VRCVA-beta-local-f8c5da1-win-x64.zip` が作られます。中の `VRCVA` フォルダー全体を、旧版とは別の、後で動かさない場所へ展開して `VrcVa.exe` を起動します。.NET Runtime同梱のWindows x64版で、`OpenVr\Assets` も含むためexeだけを移しません。スクリプトはpublishと配置検査を行い、単体テストは行いません。同名の既存ZIPは日時付きで保存します。

インストーラー、署名、自動更新、yt-dlp同梱はありません。CIの成功runからアプリZIPをダウンロードする経路もありません。警告の発行元や取得元が不明なら、そのまま実行せず確認してください。起動先を替えたら、その版の `初期設定を開く` からSteamVR連動を再設定します。

## 4. 動画検索用yt-dlpを配置

1. [公式release 2026.08.19](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19) から **`yt-dlp.exe`** と `SHA2-256SUMS` を取得します。別版、`yt-dlp_x86.exe`、PATHに入っている版は使いません
2. Windowsの通常のフォルダー `%LOCALAPPDATA%\VrcVa\tools\yt-dlp\2026.08.19\` を作り、`yt-dlp.exe` を配置します。リンク・ジャンクションは使いません。既に正しい版があればそのまま使います
3. PowerShellでhashを表示し、公式 `SHA2-256SUMS` の `yt-dlp.exe` 行と、下の固定値の両方に一致することを確認します

   ```powershell
   Get-FileHash -Algorithm SHA256 -LiteralPath "$env:LOCALAPPDATA\VrcVa\tools\yt-dlp\2026.08.19\yt-dlp.exe"
   ```

   ```text
   66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a
   ```

2026-10-01に [公式assetのdigest](https://api.github.com/repos/yt-dlp/yt-dlp/releases/assets/521488854) とコードの固定値を照合済みです。大文字・小文字の違いは構いません。不一致なら検索せず、取得元を確認します。アプリは自動導入・更新しません。検索時もpath/hash/版を検査します。

動画・音声本体をダウンロードする機能ではなく、候補metadataだけを検索します。PythonやFFmpeg、cookies、ログイン、JS runtimeを追加する手順はありません。YouTube側の変更で失敗する可能性は実検索で確認します。公式one-file exeは実行時の一時展開を行い得ます。

## 5. マイクとキーを設定

1. Windowsの `設定` → `システム` → `サウンド` → `サウンドの詳細設定` の `録音` で、使うQuest/ヘッドセットのマイクを **既定の通信デバイス** にします。見つからなければ `Win+R` → `mmsys.cpl` → `録音` で開けます。通常の既定入力と通信デバイスは別です。アプリ内で任意のマイクを選ぶ機能はありません
2. [Windowsのマイク許可](https://support.microsoft.com/en-us/windows/privacy/turn-on-app-permissions-for-your-microphone-in-windows) で、マイクアクセスとデスクトップアプリのアクセスを本人が許可します。OS/物理ミュートも確認します。VRChat内ミュートだけではVRCVAの録音を止められません
3. PCのVRCVAで `音声入力` タブを開きます。利用を決めたら説明を読み、`上記を確認し、録音操作による音声の外部送信を有効にする` をチェックします。音声専用キーを本人が入力し、`専用キーを保存` を押します。保存先は `VrcVa/OpenAI/Voice`。保存だけでは同意は有効にならず、翻訳キー・環境変数からの代用もありません
4. `解釈して検索` も使う場合は、`画面の翻訳` タブの `OpenAI APIキー` → `暗号化して保存` から **テキスト用キー** を本人が保存します。保存先は `VrcVa/OpenAIApiKey` で、翻訳と検索解釈に共用します。既存の保存済みキーがあれば入れ直しません。音声キーや一時環境キーだけでは検索解釈は使えません。保存後は翻訳も次回のSCANから有料送信できるので、検索だけの確認中にSCANは押しません

キーをチャット・GitHub・コマンド・設定ファイルへ貼りません。キーの値を助手に見せる必要はありません。英語看板の翻訳も試す場合だけ、初期設定で英語OCRを確認し、足りなければ [英語OCRの導入](../README.md#英語ocr言語機能を導入する) を行います。

**録音停止や初期30秒の上限到達は、実際の有料文字起こしを開始します。** 実送信を助手へ任せる場合は、内容・送信先・回数/秒数・予算を先に合意します。本書はその承認の代わりではありません。初期枠は1起動につき音声300秒か30送信まで、翻訳10送信、検索AI解釈10送信です。失敗・再送も計数し、再起動で消費量が戻るため月額予算の保証ではありません。送信後の中止は料金取消を保証しません。

## 6. VRChatで一続きの実機確認

準備ができたら、別のデモに分けずこの流れで使います。自分の声だけを録音し、貼り付け先は自分が操作権限を持つ動画プレイヤーにします。既存clipboardの必要な内容は先に退避してください。

1. 普段のQuest PC接続 → SteamVR → Windows版VRChatの順で起動します。VRCVAのSteamVR連動が有効なら自動起動を確認し、未設定なら `run.ps1` または配置先の `VrcVa.exe` を開きます
2. 左手首の小さい `VRCVA` ランチャーを、右手の水色の照準点とトリガーで開き、`マイク` を選びます。例として「落ち着いたピアノの作業用BGMを探して」のような自分の短い一文を話します
3. `マイク停止 → 認識` を押し、実OpenAI音声API（`gpt-transcribe`）の認識文を確認します。PCなら `録音開始 / 録り直し` → `停止して文字起こし` です。認識文は編集されず、VRとPCで同じ入力を扱います
4. `そのまま検索` を押します。認識文をそのままYouTubeへ送り、最大10件を5件ずつ表示します。タイトル・サムネイルを見て、2ページ目がある場合は `次へ` / `前へ` も使います。サムネイル取得先は `i.ytimg.com` / `img.youtube.com` です
5. 候補カードを選び、URLコピーの完了表示を確認します。Windows共有clipboardを上書きします。**本人が**ワールドの動画プレイヤーへ貼り付け、再生を操作します。VRCVAによる自動貼り付け・再生はありません。ワールド側のURL許可や操作権限で再生できない場合は、検索・コピーの成否と分けて記録します
6. VRCVAで `入力へ`（PCは `入力へ戻る`）を押し、同じ認識文から `解釈して検索` を選びます。録り直さず、実OpenAI（`gpt-6-luna` / `reasoning.effort=none`）へ認識文を送り、確定した `検索語` と候補を確認します。候補を選んでコピーし、必要なら本人が同じプレイヤーへ貼り付けます
7. パネルを出したままVRChat内で歩けるか、照準点・ボタン・候補が読めるかも、この操作中に確認します。右トリガーはVRChat側にも届くため、ワールド側の操作が同時に起きる可能性があります。読みにくければ `VRで位置調整` を使います
8. `閉じる`（PCは `閉じる・内容を破棄`）で内容を消し、VRCVAを終了します。終わったら下の「確認済みと未確認」へ実施commit、各段階の成否と遅延、送信回数/秒数、歩行・表示・手動再生の結果を記録します。キー、音声、認識文、検索語、URL、スクリーンショットを公開記録へ残す必要はありません

解釈が失敗しても直接検索や別モデルへ自動で切り替わりません。検索だけの失敗後に `やり直す`（PCは `確定済みの検索語でやり直す`）を選ぶと、成功済みの文字起こし・AI解釈は再送せず、同じ検索語を使います。無限再試行や上限を戻すための再起動は行いません。

つまずいた段階だけ確認します。

- 腕メニューが出ない: SteamVRが先に起動しているか、旧VRCVAが残っていないか確認。PCの同じ `音声入力` タブから進められますが、VR操作の合格扱いにはしません
- 録音できない: 音声同意、専用キー、既定の通信マイク、Windows許可、OS/物理ミュートを確認。録音中のデバイス切替・切断は失敗として止めます。ミュートで無音なら、停止後に無音として拒否される場合があります。物理ミュートの即時検知は保証しません
- 検索の設定失敗: 固定yt-dlpの配置/hash、解釈なら保存済みテキストキーを確認。認証・上限エラーを連打で回避しません
- 終了未確認・再起動要求: 新しい録音や検索は開始せず、VRCVAを終了してリソース解放を確認します

必要な診断は [失敗時の切り分け](../README.md#失敗時の切り分け) を参照します。通常ログは `%LOCALAPPDATA%\VrcVa\logs\vrcva-YYYYMMDD.log` ですが、本文やキーは記録しません。

## 7. 中止・終了・元の版へ戻す

- **録音中に送らず止めたい:** `マイク停止 → 認識` ではなく `中止` を押します。送信開始後の中止はサービス側の処理・課金を取り消せません
- **内容を消して終える:** `閉じる・内容を破棄` またはアプリ終了を使います。成功音声は破棄され、失敗音声は初期120秒だけメモリ保持します。中止・閉じる・録り直し・終了で破棄します。コピー済みURLはOS側に残るので、不要なら本人がclipboardと必要な履歴を消します
- **次回の自動起動を止める:** SteamVRが起動中の状態で `初期設定を開く` からSteamVR連動を無効にし、`無効` の表示を確認して終了します。SteamVR停止中は変更が保留になるため、起動してから確認します
- **前の版へ戻す:** 新版を終了し、更新前に残した実行フォルダーから起動します。設定versionは新版で保存時に6になるため、旧版が読めなければ両方を終了し、手順2で退避した更新前の `settings.json` を元へ戻します。新版の設定も別名で退避し、上書き前に確認します。旧版の起動先からSteamVR連動を再設定します。資格情報マネージャーのキーは削除・再入力せず維持します

不要になった新版フォルダーを片付ける場合も、元のソース・旧版・設定backupを巻き込みません。停止だけのためにキー、`%LOCALAPPDATA%\VrcVa` 全体、VRChatやSteamVRの設定を削除する必要はありません。

## 確認済みと未確認

**2026-10-01時点の実装基準:** [`f8c5da1babcef2b547dd778ab807d31d72147f23`](https://github.com/Na2ki-BB/vrchat-visual-assistant/commit/f8c5da1babcef2b547dd778ab807d31d72147f23)。[mainのWindows CI104](https://github.com/Na2ki-BB/vrchat-visual-assistant/actions/runs/36908642593) でRelease buildはwarnings/errors 0、単体テスト1,445件と独立cold shutdownチェック3件が成功しました。クラウドでのソース準備・自動検証であり、本人のPCへ導入済みという意味ではありません。詳しい証拠と過去の実機履歴は [TASKSの検証記録](../TASKS.md#l3-verification-evidence--2026-10-01) を残しています。

**現行版で未確認:** ローカルPCへの取得・Windowsビルド/publish、実マイク、実OpenAI API、固定yt-dlpでの実YouTube検索・サムネイル・実clipboard、Quest 3Sの新しい音声/候補パネル操作とワールドでの手動貼り付け・再生。過去の翻訳用ビルドの実機成功を、これらの成功として流用しません。

実機確認後は、内容を保存せず次だけ追記します。

- 実施日・実施commit・実行先（開発版または自己完結版）
- ローカルビルド/起動、音声認識、直接検索、解釈検索、サムネイル、コピー、手動貼り付け/再生それぞれの成否
- 段階別遅延、実送信回数/音声秒数と確認できた費用、歩行維持・可読性・中止/終了の結果
- 未確認または失敗した項目と、次に必要な対応
