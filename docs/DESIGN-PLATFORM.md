# VRChat Visual Assistant — 共通AI基盤設計

Status: implemented baseline / unexposed Windows recording adapter / approved voice flow (not connected) / historical decisions

Last reorganized: 2026-10-01 (Etc/UTC). Implementation and device evidence: through 2026-08-15 (Asia/Tokyo).

[設計の入口](../DESIGN.md) · [日本語翻訳機能設計](DESIGN-JAPANESE-TRANSLATION.md) · [動画検索機能設計（設計合意・未実装）](DESIGN-VIDEO-SEARCH.md)

## Purpose and ownership

この文書は、既存の日本語翻訳を載せている共通基盤を定義する。腕メニュー、入力、実行制御、結果表示、機能登録、テキストモデル通信、資格情報、プライバシーと開発上の制約を扱う。

画面の具体的な取得方法、OCR精度、英語から日本語への変換、翻訳費用と実測は[日本語翻訳機能設計](DESIGN-JAPANESE-TRANSLATION.md)で管理する。`ICaptureSource` とフレームの所有権は基盤の契約だが、現行のOpenVR/Windows取得方式の詳細は同文書が正本となる。

文書再整理そのものでは実装を変更しなかった。その後のI2で `CapturedFrame` と `TextInputSession` の入力境界を追加したが、実行時に選べる機能は `Translation` のみ。要約は未登録のテスト用実装であり、音声入力・音声翻訳・YouTube検索・任意ツールの実行基盤は実装済みと扱わない。

2026-10-01の設計合意では、録音・クラウド文字起こし・認識文の表示を**共通音声入力**としてこの基盤に置く。その文章から呼び出す最初の用途を[動画検索](DESIGN-VIDEO-SEARCH.md)とし、将来の別AI機能でも入力を再利用できるようにする。以下の専用節ではI1/I2の純粋な契約と未実装の接続を区別し、共通音声フロー全体が対応済みとはみなさない。J1で未公開Windows録音adapterと音声sessionを追加した。通信/UIの接続と実機受入は残す。音声送信上限は下記で合意済みとする。

## Reading current behavior and history

以下の環境、方式比較、Phase、決定ログは、元の設計書にある2026-08-11〜15の記録を責務別に移したもの。今回、Windows実機・API・価格・外部サービス規約は再検証していない。過去のMVP除外事項や初期方式を、後続Phaseで実装済みの機能の禁止事項として読まないこと。現在の操作案内は [README](../README.md)、未完了ゲートは [TASKS](../TASKS.md)を参照する。

## Environment confirmed on 2026-08-11

| Item | Confirmed state | Consequence |
| --- | --- | --- |
| Repository | Public GitHub repository on `main` with granular initial commits | Continue using small commits and inspect every public push |
| Linux side | WSL2, Ubuntu 24.04.4 LTS | Documentation, Git, text editing, and platform-neutral tests can be managed from WSL |
| Windows side | 64-bit Windows build 26200 | The shipping process must run natively on Windows |
| .NET | Windows .NET SDK 8.0.422 and Windows Desktop runtime installed; no Linux .NET SDK | Build and run through `dotnet.exe`/PowerShell; CI uses Windows runners |
| Windows SDK / Visual Studio | Not installed as standalone components | Prefer SDK-style projects and the Windows-targeted .NET TFM's WinRT references; avoid requiring Visual Studio for MVP |
| VR software | VRChat, Steam, SteamVR, and VRChat Creator Companion installed | Native capture and later OpenVR/OSC tests are possible on this PC |
| Headset / overlay | Meta Quest 3S PCVR; XSOverlay and OVR Advanced Settings installed | Use the VRCVA-owned OpenVR launcher/result panel for normal operation; retain XSOverlay and OVRAS only for error/diagnostic or recovery paths |
| GPU | NVIDIA RTX 4060 Laptop GPU plus Intel UHD | No NPU was detected; do not depend on Windows AI OCR APIs that require an NPU |
| GitHub CLI | Authenticated as `Na2ki-BB`; Git uses a GitHub-provided noreply author address | Public pushes can proceed without exposing the owner's regular email |

### WSL / Windows boundary

WSL is appropriate for source management, review, Git, and Markdown. The following must be run on Windows because they use HWND, Direct3D/Windows Graphics Capture, WPF, WinRT OCR, global hotkeys, or SteamVR/OpenVR:

- VRChat window discovery and capture
- Windows OCR smoke tests
- WPF result rendering
- global hotkey registration
- SteamVR Overlay and controller integration
- end-to-end tests against a running VRChat instance

The source stays in the current WSL workspace. Windows commands access it through WSL interop. If a tool rejects UNC paths, the documented fallback is a Windows-side clone; generated build output is never committed.

上記のWSL配置は当時の開発環境の記録であり、今回の文書編集環境を指定するものではない。

## Original MVP scope — shared foundation (historical)

### Included

- Windows desktop application targeting .NET 8
- Visible SCAN button and a configurable global keyboard hotkey
- WPF result view showing state, source text, Japanese text, and actionable errors
- Cancellation/single-flight behavior so repeated triggers cannot create request storms
- Privacy-conscious file logging without captured images, OCR text, translations, or secrets
- Best-effort localhost XSOverlay notification as an error fallback
- Persistent controller- or HMD-relative OpenVR result panel with a VRCVA-owned pointer, scrolling, and close
- Unit tests for the platform-neutral pipeline and HTTP translation response handling
- Windows CI build/test and public-repository hygiene

### Explicitly not in the original MVP

- DLL injection, memory reading, hooks inside VRChat, client modification, or anti-cheat interaction
- Continuous capture, recording, passive monitoring, automatic image upload, or telemetry
- Wrist-relative HUD placement
- Native SteamVR controller action bindings
- Avatar package/Unity asset generation
- Image/VQA analysis, free-form questions, web search, or puzzle-solving
- macOS, Linux, standalone Quest, or non-SteamVR runtime support

Wrist placement and native controller bindings were later delivered by Phase 3; those original exclusions are retained as history, not current limitations.

## Trigger

- **Current primary: VRCVA-owned SteamVR wrist launcher.** A priority-zero OpenVR Input action observes the right trigger without suppressing VRChat movement, and the left-wrist overlay exposes SCAN without an avatar parameter or manual binding edit.
- **Desktop recovery: global hotkey and SCAN button.** Win32 `RegisterHotKey` works outside the focused WPF window and does not touch the VRChat process.
- **Advanced recovery: OVR Advanced Settings.** Its SteamVR actions can send configured keyboard shortcuts from a controller. `Keyboard Shortcut Two` maps to SCAN and `Keyboard Shortcut Three` maps to the nano/Luna toggle.
- **Opt-in recovery: VRChat OSC avatar parameter advertised through OSCQuery.** A custom unsaved/unsynced Boolean `VRCVA_Scan` can be exposed as an Expression Menu button. VRCVA uses Windows-assigned dynamic ports instead of 9001 because installed XSOverlay may already occupy it. Windows DNS-SD rejects a strict loopback service registration, so the sockets are registered on Windows network interfaces while every OSC/HTTP callback rejects senders that are neither loopback nor one of this PC's own addresses; `HOST_INFO.OSC_IP` remains `127.0.0.1`.
- Do not emulate VRChat controls or modify its input pipeline.

## Renderer

- **Phase 1:** ordinary WPF window. This keeps full source text, translation, timing, and errors visible during development.
- **Phase 1.5 (superseded for normal progress/results): XSOverlay notifications.** UDP submission has no display acknowledgement and device use showed that a notification may appear late. XSOverlay therefore remains only a best-effort error fallback. The WPF view remains a parallel diagnostic renderer.
- **Rejected for normal use: XSOverlay Window Capture of the WPF app.** Real-device evaluation found too many setup interactions, an oversized panel, and a controller-click failure. It is no longer part of the normal instructions.
- **Phase 1.7 (selected after notification feedback): VRCVA-owned OpenVR result overlay.** Initialize only while SteamVR is already running and present the latest result over the scene until the user closes it or starts another scan. Its initial interaction path took SteamVR's global laser input and paused VRChat movement while reading; Phase 3 replaced that path with priority-zero input observation and a VRCVA-owned pointer, so the current panel does not stop walking input. Closing, hiding for the next scan, disconnecting, or disposing clears VRCVA interaction state. Placement defaults to the left controller and can switch to the right controller or HMD on the desktop. Position and width calibration occurs inside VR through large laser targets, with explicit save, reset, and cancel actions. A missing selected controller falls back to the established HMD-relative transform for ordinary results and prevents an ineffective calibration session.

For the capture route used by the current translation feature, see [Capture](DESIGN-JAPANESE-TRANSLATION.md#capture).

## Application architecture options

| Option | Strengths | Weaknesses | Decision |
| --- | --- | --- | --- |
| A. C#/.NET 8 + WPF + Win32/WinRT + provider adapters | Best balance for HWND capture, WPF UI, async HTTP, global hotkey, tests, and maintainability; Windows Desktop runtime already installed | OpenVR C# bindings may need a maintained interop layer later; WinRT contracts must be restored | **Selected for MVP** |
| B. C++20 + Win32/C++/WinRT + native OpenVR | Direct access to Direct3D and Valve's native API; strongest long-term overlay control | Highest implementation and memory-safety cost; slower UI/API iteration; standalone Windows SDK/toolchain missing | Reconsider for a small overlay host only if C# interop blocks Phase 2 |
| C. Rust core + Tauri/web UI or Python/TypeScript process | Good ecosystem for HTTP/AI (Python/TS) or memory safety (Rust); rapid prototypes | More runtime/packaging pieces; weaker first-party WinRT/WPF/OpenVR path; IPC and distribution complexity before user value | Not selected |

## Project boundaries

```text
src/
  VrcVa.Core/             platform-neutral contracts, result types, ScanPipeline
  VrcVa.Infrastructure/   translator adapters and protocol parsing
  VrcVa.Windows/          WPF shell, Win32 capture/hotkey, WinRT OCR, composition root
tests/
  VrcVa.Core.Tests/
  VrcVa.Infrastructure.Tests/
  VrcVa.Windows.Tests/
```

The project count is deliberately small. OpenVR remains a Windows adapter behind `ICaptureSource` and `IResultRenderer`, not a rewrite of the core pipeline.

### Core contracts

- The SteamVR wrist launcher, desktop button/hotkey, OVRAS recovery, and opt-in OSC adapters converge on the WPF composition root, which creates a `ScanRequest`; there is no separate trigger contract in Core.
- `ICaptureSource`: returns one `CapturedFrame`; implementations own platform APIs.
- `IAnalyzer`: turns one frame and request context into an `AnalysisResult`.
- `ITextFeatureHandler`: turns one completed `TextInputSession` and request context into a `FeatureResult`, without capture/OCR.
- `IResultRenderer`: renders progress, success, or failure.
- `ScanPipeline`: enforces stage order, cancellation, correlation ID, timings, and error classification.

- `FeatureCatalog` resolves a `FeatureId` to a typed `FeatureDescriptor` and matching image analyzer/text handler before processing. Registration rejects a descriptor/handler kind mismatch. Unknown IDs and request-kind mismatches fail at the trigger stage before capture/OCR/handler work.
- `FeatureResult` contains ordered, uniquely identified sections and exactly one primary section. `AnalysisResult` remains the compatibility adapter for existing translation consumers.
- `ITextModelClient` is the shared text-model boundary. Feature code owns instructions and result mapping; the infrastructure adapter owns HTTP, authentication, parsing, and bounded usage policy.

The I2 foundation distinguishes `FeatureInputKind.CapturedFrame` and `Text`. `ScanRequest.CreateText` carries an immutable, in-memory `TextInputSession` with a nonempty session ID and the original transcript (up to 4,000 UTF-8 bytes, without trimming/truncation). The text route uses `ScanStage.TextHandling` and sets capture source to `none`; the image route and translation compatibility remain unchanged. No text feature is registered by the Windows composition root yet. I3 adds an application-owned `ExecutionCoordinator` shared by all Windows SCAN pipelines and result copy. Audio/search adapters and their public UI remain later tasks.

Source: [Features.cs](../src/VrcVa.Core/Features.cs), [Models.cs](../src/VrcVa.Core/Models.cs), [ScanPipeline.cs](../src/VrcVa.Core/ScanPipeline.cs), and [OpenAiResponsesTextModelClient.cs](../src/VrcVa.Infrastructure/OpenAiResponsesTextModelClient.cs).

## Shared execution lifecycle — current implementation

1. The composition root turns a supported explicit trigger into a `ScanRequest` containing the selected feature ID, correlation ID, and timestamp.
2. `ExecutionCoordinator` admits one operation without queuing. The Windows caller holds it before overlay changes and pre-capture waits; `ScanPipeline` borrows that same operation and atomically claims execution once. Standalone callers use the pipeline-owned admission path. Registered feature/input kind is resolved before processing.
3. For an image request, the capture adapter returns one owned in-memory frame. Owned overlays must remain suppressed during acquisition; the exact current eye-mirror sequence is defined in the translation design. A text request instead passes its original completed session directly to the typed text handler, with no capture/OCR.
4. The selected image analyzer or text handler receives its typed input, request, progress sink, and cancellation token. The returned feature ID must match the selected descriptor. `FeatureResult` from a text handler passes through the existing `AnalysisResult` compatibility adapter. Cancellation is checked again before accepting the result.
5. Shared renderers consume progress/outcome and the primary result. Closing, hiding, failure, controller/runtime loss, and disposal release VRCVA interaction without taking VRChat's scene input.
6. The image frame is disposed after analysis; the text session stays owned by the caller for reuse. Logs retain only bounded metadata and sanitized failures. The owner releases the shared operation only after adapter/frame cleanup, awaited rendering, and UI-control restoration. Cancel/close invalidates presentation immediately without releasing busy ownership. WPF/OpenVR recheck the operation inside the dispatcher callback; clipboard rechecks before its STA write.

The full current capture → OCR → optional Japanese translation lifecycle, including trigger-specific timing and stable failure concepts, is in [the feature design](DESIGN-JAPANESE-TRANSLATION.md#data-flow-and-lifecycle).

### Shared OpenVR runtime ownership

`OpenVrRuntime` is process-wide and reference counted. Capture and rendering hold leases on the same runtime; disposing a per-scan capture lease must not shut down the result panel's lease. Use OpenVR only while SteamVR is already running, without starting SteamVR as a side effect. The feature-specific hide/boundary/discard/adopt ordering remains in [the translation capture contract](DESIGN-JAPANESE-TRANSLATION.md#capture).

## Shared voice input — approved design, not implemented

### Responsibility and entry point

既存の左腕メニューへマイクアイコンを追加する。共通側が「録音 → 文字起こし → 文章表示 → 使い道の選択」までを持ち、検索語の解釈・YouTube取得・候補選択は動画検索側が持つ。録音機構を動画検索の内部へ閉じ込めず、認識文を別用途に渡すための最小のテキスト入力境界を作る。現時点で未登録の用途や汎用エージェントを追加するものではない。

- マイクを1回押すと録音開始、もう1回押すと停止してクラウド文字起こしへ進む。メニューを開いただけでは録音しない
- 録音中表示、停止操作、残り秒数、中止を用意する。最大録音時間は**初期30秒**。到達時は自動停止し、手動停止と同じ文字起こし経路へ進む
- 30秒は1か所の設定値で後から変更できる設計とし、UI・タイマー・容量検査で別々に固定しない。設定の置き場所と許容範囲は下記I1に従う
- 無音による自動終了は初期仕様に含めない。無音/空の入力・空の認識結果では検索へ進まず、短い理由と録り直しを表示する。近無音判定は下記I1の方式・閾値で実装し、誤認識を完全に検出できるとは扱わない
- 文字起こし中はその状態と中止を表示する。完了後は全文と用途ボタンを表示し、検索を自動実行しない
- 認識文の直下に、最初から「そのまま検索」「解釈して検索」の2ボタンを並べる。先に「検索」を押してから方法を選ぶ二段階にはしない。ボタンの処理内容は動画検索側で定義する
- 「録り直し」は新しい入力セッションを開始して認識文を全文置換する。追記やVRキーボードによる編集は行わず、旧文・旧候補の操作を無効にする
- 正常に文字起こしできた共通の認識文は、検索語整形で上書きしない。用途から戻ってもセッション内では利用でき、終了・録り直しで破棄する

### Speech provider and ownership

音声認識は**OpenAI GPT Transcribeを正式採用**する。これは採用方針の決定であり、実APIでの精度・速度・マイク動作は未評価。音声用provider adapterに閉じ込め、具体モデルID・版を設定とadapterの境界で差し替えられるようにする。検索語を解釈するテキストモデルの選定とは分ける。ローカルAIは使わない。

Windows側はマイク取得・停止・メモリバッファの所有、Infrastructure側は音声API通信・応答解析、共通入力側は状態と認識文を持つ。I1で下記の初期adapter境界を決め、実装はJ1/J2へ残す。本人はVRChatをミュートして話す運用を想定しているが、VRChat内ミュートとOS/物理マイクのミュートは別であり、同時利用の可否はWindows + SteamVR + Questで確認する。

音声は明示した1回分をメモリ内にだけ保持する。文字起こし成功後は音声を解放する。失敗時の明示的な「やり直し」に必要な音声だけを現在の画面セッション内に一時保持し、中止・閉じる・録り直し・アプリ終了で録音停止と解放を行う。期限・サイズ制限は下記I1の判断に従う。内容の履歴、音声ファイル、逐次ストリーミング送信は初期仕様に追加しない。

### I1 adapter and settings decisions — foundation contracts only

2026-10-01に具体化。Coreの `VoiceInputOptions` / `FeatureUsageLimits` / `VoiceAudioFormat` は初期値、値域、PCM容量とquota秒数切上げの純粋な契約として追加する。I4でversion 6の非秘密設定保存/移行と独立したtext quotaへ接続した。**J1の未公開録音adapterのみ追加済み。同意画面、音声/検索の通信は未実装**。翻訳モデルallowlistは変更しない。

- **録音（J1）**: Windows標準のWinMM `waveIn` を直接包み、追加ライブラリは導入しない。`WAVE_MAPPER` と `WAVE_MAPPED_DEFAULT_COMMUNICATION_DEVICE` でWindows既定の通信入力デバイスを使う。`WAVE_MAPPER` 単独は別の対応デバイスを選び得るので使わない。録音開始時のデバイス/設定を固定し、途中切替や別マイクへの暗黙fallbackはしない。形式照会、未接続/拒否/非対応/切断を段階別の失敗にする。デバイス選択はWindows側で行い、アプリ内一覧は初期範囲に加えない
- **形式/容量（J1/J2）**: little-endian PCM16、mono、16,000 Hz（32,000 byte/秒）、44 byte headerのWAVをメモリで作る。録音上限は30秒、変更範囲1〜120秒。現設定秒数×32,000 byteで入力を止め、全体hard capはPCM 3,840,000 byte + WAV header 44 byte。余分なWAV chunk、別形式、途中sample、空データは送信しない。Windows driver内の同一デバイス形式変換の可否はJ1/L2で検証する
- **無音/保持（J1/J3）**: 停止後に空データ、または全体RMS ≤ 0.001かつpeak ≤ 0.01（PCM16正規化値）の近無音を拒否する。これは発話検出ではなく、無音で早期停止もしない。失敗音声は最初の送信失敗時から単調時計で初期120秒（許容15〜300秒）だけ保持し、再試行で期限を延ばさない。期限内に開始した送信は取消/完了まで所有するが、それ以降の再送は不可。期限、成功、閉じる、中止、録り直し、終了で所有bufferをゼロ化・解放する
- **文字起こし（J2）**: `POST https://api.openai.com/v1/audio/transcriptions` へ `multipart/form-data`、`model=gpt-transcribe`、`file=recording.wav` / `audio/wav`、`response_format=json`、`stream=false`。初期は言語自動判定、prompt/keywords/話者情報なし。専用adapterのallowlistはこのモデルと公式endpointだけ。`AllowAutoRedirect=false` とし、3xxは失敗、307/308で音声やキーを再送しない。別モデルは検証付きの別変更で追加し、無断fallbackしない。timeout 60秒、応答body 64 KiB、空/不正応答を拒否し、認識文は4,000 UTF-8 byte以内で切り捨てず検証する。音声用キーは専用Credential Manager target `VrcVa/OpenAI/Voice` に置き、既存翻訳キーや一般環境変数を自動流用しない。キーの保存だけでは音声同意にならない
- **設定（I4/J3）**: 既存 `%LOCALAPPDATA%/VrcVa/settings.json` の次のversion 6へ非秘密の `VoiceInput` / `UsageLimits` を追加し、旧version 1〜5から音声無効・下記初期値へ移行する。キー、音声、認識文、検索語、候補、消費量は保存しない。`VoiceInput.IsEnabled=false` が初期値で、J3の専用説明/明示操作でのみ有効化する。不正値は保存/新処理前に全体拒否し、失敗した再読込は最後の有効snapshotを維持する。I4でversion 6の保存/migrationを実装。新規公開音声opt-in UIはJ3へ残す

| 独立した設定値 | 初期値 | 許容範囲（整数・両端含む） | 接続タスク |
| --- | --- | --- | --- |
| `VoiceInput.MaximumRecordingSeconds` | 30 | 1〜120秒 | J1/J3 |
| `VoiceInput.FailedAudioRetentionSeconds` | 120 | 15〜300秒 | J1/J3 |
| `UsageLimits.VoiceSeconds` | 300 | 1〜3,600秒／起動 | J2 |
| `UsageLimits.VoiceRequests` | 30 | 1〜300送信／起動 | J2 |
| `UsageLimits.TranslationRequests` | 10 | 1〜100送信／起動 | I4 |
| `UsageLimits.SearchInterpretationRequests` | 10 | 1〜100送信／起動 | I4/K3 |

音声quotaは実sample数から求め、送信するPCM byte数を32,000で割った秒数を**要求ごとに整数秒へ切上げ**る（例: 32,000 byteは1秒、32,002 byteは2秒）。header/送信準備/壁時計は数えず、失敗音声の再送も同じ全量を新たに予約する。3つの用途の消費カウンターはcomposition rootのプロセス寿命所有とし、client/runtime/設定snapshotの交換とは別に保持する。設定を増減しても既消費量は不変、下げて消費済み量を下回った枠は残り0、再び増やせば新上限から既消費量を引く。進行中の録音/操作は開始時snapshotで完走し、新上限は次の操作から適用する。予約・取消・消費の実処理はI4/J2で検証する。

公式根拠は [Microsoft waveInOpen](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveinopen)、[OpenAI audio transcription API](https://developers.openai.com/api/reference/resources/audio/subresources/transcriptions/methods/create)、[GPT Transcribe](https://developers.openai.com/api/docs/models/gpt-transcribe)。2026-10-01に文書契約のみ確認し、価格/保持条件の利用前表示、実マイク、実API性能/精度はJ3/L2/L3の確認を残す。

### J1 Windows recording adapter — unexposed implementation

`VoiceInputRecorder` は `VoiceInputOptions.IsEnabled` と専用音声キー利用可否の非秘密snapshotを確認し、共通 `ExecutionCoordinator` の新sessionを受け付けてからマイクを開く。拒否/Busyでは現sessionを変更しない。公開WPF、Credential Manager接続、文字起こしAPI、音声quotaは追加しない。J3は開始時snapshotと明示操作をこの境界へ渡す。

`WinMmMicrophoneFactory` は既定通信deviceの形式を照会し、`CALLBACK_EVENT` と専用workerで3個の100 ms native bufferを循環する。実際のWinMM endpoint IDをCoreAudioへ対応付け、入力endpointの状態と通信既定の変更通知/再確認で喪失を検出する。WinMMの既定streamはOSにより別deviceへrouteされ得るため、変更を検知した録音は失敗とし、新既定へ継続しない。driverから返ったbufferはqueue順で処理し、停止時の最終部分bufferも回収する。reset、unprepare、closeが完了するまで後続処理へ渡さない。

`VoiceRecordingSession` は手動停止と設定時間/PCM容量上限を同じ停止に集約し、取消と区別する。全native回収後にPCM16の空/途中sample/近無音を検証し、44 byte headerのWAVを1つのbounded配列で所有する。無音による早期停止はしない。後続処理は同じoperationを借り、終了/取消の回収までgateを保持する。音声の取消/closeはcoordinator内でowner/session IDを原子的に照合し、古い画面の操作で次SCANを止めない。成功、中止、閉じる、録り直し、VR喪失、終了はbufferをゼロ化する。失敗音声は最初の失敗から単調時計で期限を固定し、期限内に取得したleaseのみ完了まで保持する。再送でも期限は延長しない。

正常driverの停止/解放順序はfakeで確認する。driverが繰返しreset/unprepare/closeを拒否する異常経路では、まだdriverが所有するpointer/eventを解放してuse-after-freeを起こさず、最大1録音分のbounded native allocationをprocess内で隔離する。`MicrophoneCleanupFailed` を返し、native再openを拒否し、共通coordinatorの `Stop()` で全用途の新admissionも拒否する。shutdown待ちは完了できるが、driver所有資源を解放成功とは扱わず保持する。OS資源回収には本人のアプリ再起動が必要となる可能性があり、この経路を正常解放成功と扱わない。

根拠: [waveInOpen](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveinopen)、[waveInReset](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveinreset)、[waveInClose](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveinclose)、[GetState](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getstate)、[stream routing](https://learn.microsoft.com/en-us/windows/win32/coreaudio/stream-routing)。2026-10-01に公式仕様とfakeを確認。実マイク/OS・物理ミュート/SteamVR/QuestはL2、実APIと保持条件はJ3/L3で確認する。

### Minimal input and result extension

I2で `FeatureInputKind.Text`、`TextInputSession`、`FeatureEntry(ITextFeatureHandler)` と `ScanRequest.CreateText` を追加し、`ScanPipeline` は入力種別を実処理前に検証して画像/テキスト経路へ分岐する。音声や文章を偽の `CapturedFrame` に包まず、テキスト機能のためにcapture/OCRを呼ばない。既存翻訳は現行analyzerと互換結果を維持する。具体型・クラス名の確定や全pipelineの一般化は本設計の条件にしない。

I2で `FeatureDataBoundary` をflagsへ拡張し、既存のlocal/抽出text/imageの値を維持したまま `VoiceAudioToOpenAi`、`InputTextToOpenAi`、`SearchTextToYouTube` を区別する。解釈検索は後者2つを組み合わせられ、音声の送信境界とは独立する。metadataはprovider有効化や送信同意ではなく、実通信は未接続である。「抽出テキストのみ」の表示で音声を送らない。認識文は不変のsession IDと本文を持ち、用途の検索語は共通sessionへ保存/上書きしない。I3で用途処理のoperation IDと世代、古い応答/操作の拒否を共通gateへ接続した。具体的な検索候補/音声adapterの接続は後続タスクで行う。

テキストセクションだけの `FeatureResult` に、動画候補の型付きデータと選択actionを追加する。タイトルや任意URL文字列をコマンドとして扱わず、現在の候補IDを照合して許可済みのコピーだけを実行する。翻訳のprimary section・互換表示は壊さない。候補の詳細は動画検索設計を正本とする。

### Cross-feature single-flight and cancellation

I3で `ScanPipeline._isRunning` とWindowsの `_uiScanRunning` を `ExecutionCoordinator` / `ExecutionOperation` へ置き換えた。MainWindowは1つのgateをアプリ寿命で所有し、desktop/hotkey/OSC/腕SCAN、診断画像、既存結果のコピーに共有する。録音/文字起こし/検索はまだ公開していないが、fake操作で同じ所有境界を検証する。録音、文字起こし、検索語解釈、検索、コピー、および既存翻訳を含めて、**処理は一度に1つ**にする。

- `TryBeginSession` は新しい入力を、`TryBeginOperation` は現在のsessionの検索/コピー等を受け付ける。Busyでは世代/画面を変えずqueueにも積まない。OSCは受信時に判定してからdispatcherへ渡し、腕SCANは受付後にのみlauncherを隠す
- operation IDは1起動中に再利用せず、同じoperationでpipelineを2回実行できない。認識文/候補の待機中はoperationを解放する。modelの短いメモリ更新はadmissionと原子的に排他し、資格情報の保存/削除とruntime交換は設定用operationを通して一括所有する
- 中止はoperation世代を失効させ、closeはsessionも終了する。取消tokenを伝えてもownerの回収完了まではBusyを保つ。取消callbackの例外は観測可能な `CancellationFailure` として保持し、Windowsは本文なしで記録する
- WPF/OpenVRは成功・失敗・進捗をdispatcher実行直前に再検証する。VRの本人によるcloseはcapture用Hideと別のeventで失効し、画像選択dialogも単調な世代で古い操作を拒否する
- Windows終了はClosingを一度保留し、operationとOSC起動の回収を待つ。最後のCloseを必ずdispatcherへpostしてからpanel/HTTP/CTSを破棄する。captureのhide/boundary/discard/adoptと参照数付きOpenVR所有は変更しない

共通制御と利用quotaは別の所有物で、翻訳10回/解釈10回、音声300秒/30送信の初期値は変更しない。text quota runtime接続はI4で実装済み、音声quotaはJ2、音声/検索UIと操作できるVR進捗/失敗画面はJ3/K5/L1以降へ残す。

1. 明示操作でgateを取得し、現在のsession/operationとキャンセルトークンを発行する。録音開始から文字起こし完了までを1処理として占有する
2. 認識文や候補を表示しているだけの待機状態ではgateを解放する。次の検索・コピー・SCANも同じgateで競合を確認する。他の処理中は新要求をqueueに積まず、現在の画面を壊さない短い処理中表示にする
3. 手動停止と30秒到達が競合しても停止・送信は1回だけ。中止は停止と区別し、録音中の中止では文字起こしを送らない
4. 中止・閉じる・新しい入力で世代を無効化し、HTTP、音声取得、検索子プロセス、画像取得へキャンセルを伝える。完了通知だけでなくUI反映とclipboard実行の直前にも世代を確認する
5. 中止後は「中止中」を示し、ローカルの処理とリソースの回収を確認してからgateを解放する。遅れて返った結果で新画面や新候補を置き換えない。既にサービスが受け付けた要求の中止が、課金取消を保証するわけではない
6. 失敗は段階と短い理由を表示し、本人が「やり直し」を選んだ時だけ該当段階から再実行する。認識文・確定済み検索語など有効な入力は再利用し、成功済みの有料段階を自動でやり直さない。認証不足・上限到達では設定案内を出し、無意味な再送をしない

処理表示だけの現行status atlasや、エラー時に腕へ戻る現行経路では中止・やり直しを操作できない。共通の**操作可能な処理中/失敗画面**を新設する必要がある。full 1280x720 view、表示とhit-testが同じ矩形を使う契約、priority-zero入力、歩行の非干渉を継承する。ヘッダーは表示専用とし、中止・やり直し等の操作は既存body railに置く。用途ボタンは認識文の直下となるbody内に配置する。

既存翻訳のcapture時には、新しい音声画面を含むVRCVA overlayを従来どおり抑制する。取得のhide/boundary/discard/adopt順序を変えない。captureの短い非表示中はWPF側の中止経路を維持し、その後に共通進捗画面へ戻す。SteamVR喪失・デバイス切断・終了でも録音や操作状態が残らないようにする。

### Voice opt-in, cost policy, and privacy

- 既存のWindows Credential Managerを活用し、公式OpenAI endpoint以外へ保存キーを送らない。翻訳用キーがあるだけでマイク取得・音声送信を有効にせず、音声機能の明示有効化と送信先・費用の表示を設ける
- 音声用のAPIは既存Responsesテキスト通信と分ける。既存の `store: false`、入力4,000バイト、10回quota等が音声にもそのまま適用されるとは説明しない
- 1回の録音上限は初期30秒。これとは別に、音声送信は**1起動につき累積300秒（5分）か30送信のどちらかの上限**で止める。両方を満たす要求だけ送信可能とし、上限値は後から設定で変更できるようにする。許容範囲・設定場所、要求サイズ・timeoutは下記I1で固定し、接続はJ2/J3へ残す
- 音声quotaはHTTP送信直前に、その要求に含む音声の全秒数と1回を一括予約する。予約で上限を超える場合は送信しない。送信開始後の失敗・中止・timeoutは返却せず、本人による再送も新たに秒数と回数を消費する。録音中の中止など送信前に終了したものは消費しない（予約後でも送信未開始を確認できれば返却する）。自動再送はしない
- 音声quotaはアプリのプロセス寿命で共有し、画面を閉じる、録り直す、設定再読込、runtime再構築では消費量をリセットしない。アプリ再起動でリセットされるため、月額支出上限やアカウント全体の予算保証ではない
- 音声quota、翻訳quota、検索AI解釈quotaは**3つの独立した枠**にする。翻訳と解釈は互いの残回数を消費しない。テキストの初期値は既存の10回を各用途に置く設計とし、後から個別に変更可能にする。詳細は下記のI4で実装した独立text quotaと、未実装の音声quota設計を参照する。解釈モデルはGPT-6 Luna（`reasoning.effort=none`）を採用し、詳細・料金根拠は動画検索設計に置く
- 通常ログは段階、時間、回数、サイズ、エラー種別等だけ。音声、認識文、検索語、候補のタイトル・URL、API本文、キーを記録せず、例外や子プロセス出力もそのままログへ流さない
- 録音に周囲の声が入る可能性を案内する。常時録音・待ち受け・ワールド音声取得・会話履歴保存は行わない

音声APIの公式根拠、動画検索固有の送信先、確認済み事項と未検証事項は[動画検索の根拠一覧](DESIGN-VIDEO-SEARCH.md#sources-and-verification-status)にまとめる。音声フローは設計合意で、J1の未公開録音adapter/sessionとI4の非秘密設定保存/独立text quotaを実装済み。音声送信/音声quota/公開UIは未接続である。

## Feature extension rules

Future AI features use a compile-time `FeatureCatalog`, typed feature descriptors, and shared backend/usage policy. Dynamic plug-ins, an autonomous agent loop, arbitrary tools, and a general-purpose kernel remain deferred. The earlier OCR/text-only extension point was proved with the unregistered summarization analyzer. The next user-visible feature is now the approved voice-driven video-search design above, which needs a minimal text-input extension; it must not force microphone input through OCR. OCR/world text, transcripts, model outputs, and search metadata are untrusted content, not authority for arbitrary tools or settings changes.

The current implemented foundation resolves a typed feature before capture and returns an ordered, feature-neutral set of result sections with exactly one primary section. Unknown IDs fail at the trigger stage without capturing. OpenAI HTTP/authentication, bounded request policy, response parsing, and the purpose-bound process-lifetime quota live behind `ITextModelClient`; translation and summarization own only their prompts and result mapping. The summarization analyzer is deliberately left out of the runtime catalog and UI: fake-client tests prove the extension boundary without adding a user-visible feature or another way to spend API credit. Existing translation and OCR-only behavior are retained through a compatibility adapter while renderers consume the generic primary result.

I4で翻訳/検索AI解釈に別カウンターを追加した。各10回は初期値で、設定により1〜100へ個別変更できる。音声quotaと公開解釈adapterは後続タスクへ残す。

新機能を追加するときの境界:

1. 機能固有の目的・入力・出力・データ送信範囲を別の機能設計で定義し、実装済みの入力型で表現できるか確認する。
2. `FeatureDescriptor` / `FeatureEntry` / `FeatureCatalog` へコンパイル時登録する。未登録の要約をUIで有効化したものとして扱わない。
3. 画像機能は `IAnalyzer` に処理を置き、結果セクションを返す。新しいテキスト機能は上記の入力別handlerを使い、翻訳専用プロンプトや検索語解釈の指示を共通UIへ持ち込まない。
4. テキストAIが必要なら `ITextModelClient` を再利用し、プロセス寿命で保持する用途別の使用量制限を適用する。翻訳と検索AI解釈の残回数は共有しない。外部送信の明示選択、キャンセル、エラー処理、fake-clientによる無通信テストを維持する。
5. 入力型の追加、画像送信、外部ツール、副作用、自律実行は別の設計・承認が必要な範囲。今回合意した共通音声入力、yt-dlpによるmetadata検索、本人が選んだURLコピーだけを必要な拡張とし、任意ツールや自律実行へ広げない。既存の拡張点だけで対応済みとも主張しない。
6. 結果UI・配置・入力を変更する場合は、下記の実機ゲートと [development harness](../harness/skills/vrcva-development/SKILL.md) の統合検証を適用する。

## Shared text-model transport and credentials

Windows Credential Manager remains the selected personal-use secret store. It gives the API key an OS-managed, per-user boundary without placing it in `settings.json`, environment files, command history, or logs. Saving or deleting a key must affect the next SCAN without restarting VRCVA. In the current implementation, independent process-lifetime quota objects survive runtime reconstruction and settings reload, preserving consumption even when their ceilings change. Saved OpenAI credentials are valid only for the official endpoint preset; custom endpoints require a separate future profile and credential.

現在の共通通信はOpenAI Responses API用の `OpenAiResponsesTextModelClient`。`OpenAiTextTranslator` は翻訳指示と結果整形を担当する。クラス・設定名に残る `Translation` は既存実装の命名であり、今回リネームしない。

| Responsibility | Existing owner / rule |
| --- | --- |
| HTTP, authentication, response parsing | `OpenAiResponsesTextModelClient` behind `ITextModelClient` |
| Request policy | `store: false`, `reasoning.effort=none`, no tools, no automatic retry |
| Input/output guard | At most 4,000 UTF-8 input bytes and 1,200 output tokens |
| Timeout | `VRCVA_OPENAI_TIMEOUT_SECONDS` configures 1–25 seconds; hard maximum is 25 seconds |
| Allowed model IDs | `gpt-5.6-luna` and `gpt-5.4-nano`; no arbitrary model bypass |
| Process quota | Application-owned `FeatureUsageQuotas` holds separate `TextRequestQuota` counters for translation/search interpretation, each initially ten attempts and configurable 1–100. Failed started sends count only their purpose; runtime reconstruction/reload never resets consumption, process restart does |
| Persistent secret | Windows Credential Manager `VrcVa/OpenAIApiKey`; same-user boundary, not protection against every process running as that user |
| Stored-key destination | Official OpenAI endpoint only; no silent forwarding to a custom endpoint |
| Local-first behavior | No key/provider selection means local OCR; no generic `OPENAI_API_KEY` fallback |
| Tests and logs | Fake HTTP handlers and placeholder keys; no content, credential, or HTTP-body logging |

動画検索で採用した `gpt-6-luna` は現行allowlistに含まれない。実装時に共通通信へ明示的に対応を追加し、翻訳の既定モデル・切替候補は変えない。設定で任意モデルを素通ししたり、利用できない時に別モデルへ暗黙fallbackしたりしない。

### Independent usage quotas — implemented text counters

2026-10-01の追加指示で、翻訳と検索AI解釈を同じ10回枠にする案を変更した。共通にするのは通信・認証・検証の仕組みであり、消費カウンターではない。I4で共有 `TranslationRequestQuota` を用途付き `TextRequestQuota` と `FeatureUsageQuotas` に置き換えた。MainWindowが2つのカウンターをプロセス寿命で所有し、翻訳factoryと全再構築へ翻訳用の同じinstanceを渡す。解釈枠はfake共通HTTPで検証し、公開adapter/モデル接続はK3へ残す。

- 翻訳と検索AI解釈に別々のプロセス寿命のquotaを持たせ、どちらを使っても他方の残回数を減らさない。片方が上限に達しても他方は自分の残回数で利用できる
- 初期値は既存の数値を引き継ぎ、**翻訳10回・検索AI解釈10回／起動**をそれぞれ設定する。10回ずつという数値は新しく利用希望回数を指定されたものではなく、分離時の初期値であり、用途別に変更可能とする。設定場所・許容範囲は上記I1に従い、text quotaへの接続はI4で実装済み
- 各枠でHTTP送信直前に1回を予約し、`SendAsync`呼出しを試みる直前を開始境界として確定する。予約後に未開始の取消/timeoutを確認した場合だけ返却し、開始後の認証/通信失敗・中止・timeoutもその枠だけで計数する。providerの受付確認がない通信失敗も保守的に数える。自動再送はしない。設定再読込・モデル切替・runtime再構築で消費量は戻さず、アプリ再起動時にリセットする。同じ用途の入口やclientごとに別カウンターを作って迂回しない
- 音声の300秒・30送信枠は両テキスト枠から独立する。「そのまま検索」は解釈枠を消費せず、確定済み検索語によるyt-dlpの再試行も翻訳/解釈枠を消費しない
- **single-flightは引き続き全用途で共通**。quotaの分離は同時に処理してよいという意味ではない。上限表示や失敗理由には対象の用途を示す

I4の`UsageSettingsSnapshot`は全体を検証した後だけ設定を交換する。version 1〜5は音声無効/初期枠へ移行し、version 6の全fieldを必須とする。SCANの受付後・capture前、および資格情報runtime交換時に共通gateを保持して再読込する。無効な設定は最後の有効snapshotを維持したまま、そのSCANを通信/取得前に拒否し、修正後の次処理で復帰する。進行中は再読込せず、上限増減でも過去の消費量は不変。カウンター/キー/音声/本文は設定へ保存しない。音声枠の計数はJ2へ残す。

設定の優先順位・モデル切替と翻訳への適用は[翻訳機能のOpenAI仕様](DESIGN-JAPANESE-TRANSLATION.md#translation)を参照。料金と過去の費用見積もりも機能側に置き、これらの起動ごとの制限をアカウント全体の支出保証とみなさない。

## Security, privacy, and public-repository policy

- Never inject code, load a DLL into VRChat, patch files, read process memory, bypass EAC, or depend on non-public VRChat APIs.
- Opt-in OSC uses DNS-SD link-local advertisement, so the service name and dynamic ports are visible on the LAN. Windows will not register a DNS-SD service bound only to loopback; callbacks therefore apply a local-host address allowlist before parsing or responding, and OSCQuery tells VRChat to send OSC to `127.0.0.1`. Raw packets, sender addresses, and `/avatar/change` values are never logged.
- Capture only after an explicit wrist-launcher trigger, click/hotkey, OVRAS shortcut, or OSC edge. There is no timer-based capture loop.
- Keep frame bytes in memory and dispose them. Debug image export is disabled by default; implemented diagnostic exports require an explicit save option and write only to the user-specified local directory.
- Capture and OCR are local. No OCR text leaves the PC in the default unselected state. Any future cloud provider or image-upload analyzer must have an unmistakable UI disclosure and explicit opt-in configuration.
- XSOverlay notifications stay on the PC through `127.0.0.1`. Normal SCAN sends only a sanitized error fallback; model changes and explicit diagnostics send short status messages. OCR text and translated content are never included, while another local process with access to that UDP endpoint remains inside the local trust boundary.
- Do not log images, OCR text, translations, Authorization headers, request bodies, environment variables, user IDs, avatar IDs, or world names.
- Keep secrets in environment variables or OS secret storage. `.env`, local settings, captures, logs, dumps, publish output, and IDE metadata are ignored by Git.
- CI never receives a production API key. Network-backed tests use fake HTTP handlers.
- Before each public push: inspect `git diff --cached`, run a secret-pattern scan, confirm no generated captures/logs, then commit.
- Brand the project as unofficial and avoid implying VRChat or Valve endorsement.
- Re-check VRChat Terms and supported OSC docs before shipping a release because service rules can change.

## GitHub management baseline (historical)

- Default branch: `main`.
- Public repository: `Na2ki-BB/vrchat-visual-assistant`; local `main` tracks `origin/main`.
- CI: Windows runner, restore/build/test with no secrets.
- Dependency security: GitHub vulnerability alerts and Dependabot security updates are enabled. Routine Dependabot version-update PRs are disabled to avoid update noise.
- Include `SECURITY.md`, contribution guidance, issue templates, and a pull-request template.
- Do not choose an open-source license silently. Public visibility does not itself grant reuse rights; add a license only after the owner selects one.
- Use feature branches and draft PRs once the remote exists; protect `main` after the first successful CI run.

This records the original baseline. For current contribution workflow, follow the active task instructions and [CONTRIBUTING.md](../CONTRIBUTING.md); this document split does not authorize a commit or push.

## Phased delivery and validation history

原設計のPhase番号と時系列を維持する。Phase 1 / 1.6 の翻訳パイプライン・OCR検証は[機能側の履歴](DESIGN-JAPANESE-TRANSLATION.md#phased-delivery-and-validation-history)へ分離した。

### Phase 0 — research and scaffold

Environment inventory, official API review, design, task plan, Git/public hygiene.

### Phase 1.5 — Quest 3S + XSOverlay vertical slice

Send compact results through XSOverlay's local notification endpoint instead of capturing the WPF window. Trigger the existing hotkeys from OVR Advanced Settings controller actions, preserving desktop diagnostics and avoiding XSOverlay pointer interaction.

### Phase 2 — natural in-VR trigger

The receive-only OSCQuery subset now advertises Windows-assigned OSC/HTTP ports, serves `/avatar` plus `HOST_INFO`, and accepts only a configured Bool or Int avatar parameter. A monotonic rising-edge gate allows the first press after quiet startup, while suppressing an active state observed during avatar-change settling, menu-reset duplicates, and rapid repeated triggers. It is opt-in and keeps the keyboard/OVRAS trigger as the recovery path. PCVR has confirmed VRChat auto-discovery and first-press delivery while XSOverlay continues using 9001; verification from a second LAN device that no OSCQuery response is usable remains outstanding.

### Phase 1.7 — interactive VR result panel

The XSOverlay notification evaluation exposed material limitations: fixed lifetime, no explicit close, and no long-text scrolling. The implemented VRCVA-owned OpenVR scene overlay uses the existing renderer contract, keeps a tracked-device-relative result until close/replacement, and retains WPF diagnostics and graceful fallback. Its first interaction path temporarily used SteamVR's global laser mode and therefore paused VRChat movement while reading; Phase 3 supersedes that path with priority-zero input observation, explicit overlay intersection, and a VRCVA-owned cursor. The default anchor is the left controller; its initial position and orientation are derived from the saved VRCVA/SCAN wrist-launcher transform so the result replaces the menu without requiring another wrist movement. Result width remains independent and larger for readability. Settings versions 1 through 4 migrate a left-anchored result pose to that saved launcher transform once while preserving the result width. A version-5 result that still has the same pose as the previous launcher follows a later launcher-calibration save, again preserving width; moving the result pose independently breaks that relationship, while changing only result size does not. This transform-derived rule avoids hidden persistence state and protects deliberate result calibration. Right-controller and HMD placements are not affected and remain selectable on the desktop. Fine-grained desktop sliders were rejected after immediate usability feedback because switching between the monitor and headset for every adjustment is impractical. A dedicated 1280×720 VR calibration texture instead provides eight large movement/size targets plus save/reset/cancel; every adjustment changes the overlay transform immediately without re-uploading its texture. Calibration deliberately uses one full texture whose dimensions match the logical surface. Values remain bounded and are atomically persisted as non-secret local settings only on explicit save. The controller role is resolved for every display instead of retaining a stale device index, and an unavailable selected controller uses the established HMD-relative placement for ordinary results.

The same overlay owns OSC acknowledgement and OCR progress. A fixed 2x3 atlas retains the three non-interactive status views. Interactive results instead upload only the current page as one 1280x720 texture, wait for `ImageLoaded`, select full bounds, and then re-enable pointer input. Page buttons and the scrollbar repeat that guarded upload for the new page. This deliberately accepts a possible brief page-change flash to remove the backing-atlas aspect ambiguity from the native intersection surface. Text beyond the three bounded VR pages remains complete in the WPF view.

The result header is title-only. Previous, Next, and Close live in a fixed, visibly rendered control rail inside the result body because the supported SteamVR/headset path did not provide a reliable pointer surface over the visible header. Rendering and hit testing consume the same shared rectangles; a control must not rely on an X-only column, ignore Y, or use an invisible fallback target. Result interaction uses one shared full-texture view for both the bounds sent to OpenVR and the inverse transform used for intersection. Atlas-backed status and launcher views retain the same selected-view invariant rather than independently reconstructing either side from a cell number.

The 2026-08-15 recurrence exposed two independent layers that must not be
collapsed into one diagnosis. Changing from result cell 3 to cell 4 produced
the expected discontinuity in atlas-global raw UVs while mapping to a
continuous 1280x720 logical point, and historical traces contain successful
Next, Previous, and Close activations. That proves the post-intersection atlas
inverse and button dispatch for those samples. Current traces also show a
successful Close hit at logical Y≈656 followed by native `ComputeOverlayIntersection`
misses lower on the still-visible surface; no managed rectangle or dispatch
code runs for those misses. Treating either the header rectangle or the body
rail rectangle as the whole root cause was therefore an invalid completion
claim.

The result overlay establishes its input surface explicitly with OpenVR's
`SetOverlayIntersectionMask`: one 1280x720 rectangle matching the mouse scale.
Pointer diagnostics distinguish a native OpenVR miss from a native hit rejected
by the selected-view mapping and reset their bounded capture window when a new
result starts. Despite the explicit full-surface mask, the 2026-08-15 current-
build headset check found native misses near both the header and lower result
surface. Result pages therefore use the same full 1280x720 texture shape that
already produced stable calibration alignment, instead of selecting a 16:9
cell from a 32:27 backing atlas. The header remains display-only and all result
actions remain in the fixed body control rail. Do not add a guessed offset,
move controls to hide a native-surface failure, or create an invisible target
without new device evidence that first explains the runtime mismatch.

The subsequent 2026-08-15 full-texture acceptance passed on the current
Windows Release build: result pages 1 through 3 remained operable, the complete
lower rail could be swept without cursor loss, and the scrollbar remained
usable. Ten adopted eye captures contained zero launcher, result, or cursor
markers after capture suppression. Saved left-, right-, and HMD-relative
placements survived a normal VRCVA restart, and SCAN replaced the launcher with
the result at the same left-hand position and orientation without requiring a
hand movement. These observations close the original atlas-surface and initial
placement device gates while retaining the diagnostic contract for future
layout or runtime regressions.

### Phase 3 — wrist launcher, local-first setup, and feature foundation

The implemented Phase 3 path replaces normal OVRAS/OSC launching with a VRCVA-owned left-wrist launcher. SteamVR Input 2.0 is read with action-set priority `0`, so VRCVA observes the right trigger without suppressing VRChat's scene actions. The left joystick is absent from VRCVA's action manifest and remains dedicated to VRChat movement. The former `MakeOverlaysInteractiveIfVisible` path was removed from ordinary result/menu use because OpenVR defines it as system-wide laser-mouse mode while the overlay is visible; that flag was the identified cause of movement loss.

VRCVA computes the right-controller ray and overlay intersection itself. Pointer position, button rectangles, rendering, and hit testing share one logical surface specification. Interactive result pages and calibration use a full 1280x720 texture view. Atlas-backed status and launcher views additionally preserve one explicit coordinate chain: OpenVR's atlas-global normalized, lower-origin intersection UV; the exact selected texture bounds; then the top-origin logical surface used by both drawing and hit testing. The trigger's rising edge is accepted only while the pointer is over an enabled VRCVA control; a trigger already held when hover begins must be released before it can activate anything. Input failure disables only VRCVA interaction and never falls back to taking scene input. The same input route must serve the launcher, result pages, close button, scrollbar, and placement calibration. Joystick scrolling is retired so walking and reading do not share one physical control.

The cursor overlay is centered at OpenVR's native tracking-space intersection point. It must not be displaced along the panel normal: even a small physical offset creates HMD-view-dependent parallax between the visible cursor and the logical hit point. Cursor-over-panel ordering is controlled solely by the cursor overlay's higher sort order. Tests must preserve the cursor center at the native intersection for both surface-normal signs and oblique rays.

The initial device gate is `--steamvr-input-pass-through-check`: Quest 3S must report 20/20 right-trigger edges while the owner continuously walks in VRChat, with no Action Menu, OSC, OVRAS action, manual binding edit, or movement pause. This gate passed on 2026-08-14: all 20 edges were received with an active action set and valid right-hand poses, and the owner independently confirmed that walking never stopped. Default Oculus Touch bindings ship with the app; this removes user-authored bindings, although SteamVR still uses a normal application binding internally. Trigger input remains visible to VRChat by design. Selectively suppressing it would require SteamVR's experimental overlay-input override and is outside this slice.

After the gate, a small non-blocking chip follows the left controller. It becomes armed only after its surface faces the HMD for 150 ms, using hysteresis to avoid flicker. The right-hand pointer expands a compact feature menu. Starting a feature immediately hides every VRCVA overlay before the existing compositor-boundary/discard capture sequence. A completed result does not enable global laser mode; closing it returns to the wrist chip. SteamVR loss, controller pose loss, cancellation, and disposal all fail open for VRChat input.

SteamVR exposes the HMD and controller poses used here, not a measured forearm or elbow pose. The launcher therefore remains compositor-tracked relative to the left controller instead of introducing a jitter-prone virtual-elbow estimate. Its feature menu opens a dedicated calibration surface fixed in front of the HMD while the left-hand preview remains visible. Position changes use 1 cm controller-local steps, orientation uses 5-degree post-multiplied steps about the panel's local X/Y/Z axes, and one scale changes both chip and expanded menu. Launcher orientation is stored as a normalized canonical quaternion so the three controls remain distinct near Euler-angle singularities; version-3 Euler settings migrate by preserving the complete transform matrix. Save and cancel are explicit; versioned non-secret settings migrate older files to the tested Quest default. Facing feedback uses the absolute panel-plane alignment because the supported runtime displays both sides of the overlay, while invalid or missing poses remain fail-closed.

The small-group onboarding flow is local-first and has three short checks: SteamVR auto-launch registration, English OCR readiness, and optional OpenAI BYOK storage. It does not start SteamVR without consent and does not require an installer. A stable self-contained beta folder is the supported distribution shape; the first run registers its fixed executable path with SteamVR and may require one SteamVR restart before auto-launch is recognized. Later launches occur with SteamVR and stay minimized unless setup or diagnostics need the desktop window. A single-instance guard prevents a manual launch and SteamVR launch from creating two processes.

The shared credential/quota, feature-extension, and development-harness contracts from this phase are maintained in their dedicated sections above and below.

#### Phase 3 completed implementation gates

1. Characterized capture, atlas, close, scrollbar, calibration, key, and no-network behavior with tests.
2. Added the Input 2.0 ABI and passed the Quest pass-through gate before changing normal interaction.
3. Moved result/calibration interaction to the shared pointer contract and permanently kept global laser mode off.
4. Added the wrist chip/menu state machine and routed `Translation` through the existing single-flight pipeline.
5. Added app-manifest auto-launch, single-instance behavior, versioned settings migration, and the first-run wizard.
6. Extracted the feature/backend composition boundaries without changing translation output or privacy behavior.
7. Prepared and validated the uninstalled AI-development skill and its configuration instructions.
8. Completed Windows build/tests/format, secret inspection, diagnostic checks, independent review, and the core Quest acceptance gates. Remaining device follow-ups stay explicitly unchecked in `TASKS.md`.

### Phase 4 — analyzer expansion

Add typed analyzer selection and explicit data-boundary indicators for OCR-only, multilingual translation, VQA, summarization, puzzle hints, object recognition, and opt-in web search.

### Earlier later-use-case list

- Add OCR-only, multilingual translation, VQA, summarization, puzzle hints, object recognition, and opt-in web search.

## AI-development harness

The AI-development harness is stored in the repository but is not installed or enabled automatically. It consists of a concise skill, project-specific references, deterministic verification commands, and evidence rules. Product invariants remain in source code and tests; the skill is a runbook that invokes them. Its setup document may explain how to copy or link the skill into a supported agent environment, but this project must not write to personal Codex/Claude settings, install plug-ins, or register MCP services.

See [setup instructions](AI-HARNESS-SETUP.md) and the [source-of-truth map](../harness/skills/vrcva-development/references/source-of-truth.md).

## Decision log

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-01 | 共通音声入力と用途別テキスト処理を分離し、GPT Transcribeを採用（未実装） | 動画検索以外でも認識文を使い、検索語の整形で共通文を上書きしない |
| 2026-10-01 | 押して開始/再押し停止、初期30秒自動停止、録り直しは全文置換 | 明示操作と短い録音を保ち、VR文字編集を増やさない |
| 2026-10-01 | 翻訳も含む共通single-flightと操作可能な中止/失敗画面を設計 | 現行SCAN内gateと非操作statusだけでは音声・検索の競合や再試行を扱えない |
| 2026-08-11 | Start with an external Windows app, not a VRChat mod | Complies with the non-invasive requirement and avoids client/EAC risk |
| 2026-08-11 | Select C#/.NET 8 + WPF | Best total fit for installed environment, Win32/WinRT, GUI, HTTP, tests, and maintainability |
| 2026-08-11 | Use hotkey first, official VRChat OSC next | Proves value with no avatar work; OSC later gives native in-VR interaction through a supported interface |
| 2026-08-11 | Defer OpenVR overlay until the desktop vertical slice is measured | Overlay work should not hide capture/OCR/translation failures |
| 2026-08-11 | Initially use installed XSOverlay Window Capture for the first Quest 3S test (superseded below) | It provided the quickest first visual test before real interaction evidence existed |
| 2026-08-11 | Do not add a license yet | License choice belongs to the repository owner |
| 2026-08-11 | Replace XSOverlay Window Capture with localhost notifications | Device feedback showed high setup friction, excessive panel size, and broken controller clicking; notifications preserve VR visibility without a persistent window |
| 2026-08-11 | Use OVR Advanced Settings as the interim controller bridge | It is already installed and officially supports controller-bound keyboard actions, so SCAN and model toggle do not depend on XSOverlay clicks or avatar edits |
| 2026-08-11 | Require OSCQuery for a future OSC trigger | XSOverlay uses the usual 9001 receive port on this machine; discovery avoids fixed-port conflicts and supports multiple receivers |
| 2026-08-12 | Shorten the XSOverlay start notification from 12 seconds to 1 second | Logs showed capture and OCR usually completed in about one second, but XSOverlay queued the result behind the long progress notification |
| 2026-08-12 | Replace fixed-duration XSOverlay result notifications with a VRCVA-owned OpenVR result panel | Real-device use requires the result to remain readable, close on demand, and scroll through long text; the renderer boundary allows this without changing capture, OCR, or translation |
| 2026-08-12 | Keep OpenVR result placement HMD-relative before wrist placement | It proves compositor rendering and interaction with the fewest new moving parts; controller-relative calibration remains an independent follow-up |
| 2026-08-13 | Automatically enable result-overlay interaction while visible | Direct laser scroll/close needs no keyboard or controller binding; the owner accepts that VRChat movement pauses until the panel is closed |
| 2026-08-13 | Preload a VRCVA-owned status atlas and show acknowledgement directly instead of through XSOverlay | XSOverlay UDP has no display acknowledgement and real-device feedback showed that queued progress appeared only at the end; the owned overlay provides deterministic ordering and is hidden before capture |
| 2026-08-13 | Replace pixel scrolling with a fixed six-cell atlas and texture-bound page changes | Repeated `SetOverlayRaw` replaced the compositor image on every step and visibly flashed; bounds-only navigation performs no image upload and also supports direct scrollbar selection |
| 2026-08-14 | Default the result panel to a user-calibrated left-controller transform | It shortens normal reading access without adding an input binding; right-hand/HMD choices and a per-display HMD fallback keep placement recoverable |
| 2026-08-14 | Perform placement calibration inside VR instead of with desktop sliders | The user must see the panel while adjusting it; large laser targets provide immediate spatial feedback and avoid repeated headset/monitor switching |
| 2026-08-14 | Render calibration as one full logical-UI texture instead of an atlas cell | Five device measurements showed X mapping was correct but bounded-atlas Y clicks expanded about 1.5× from center; matching texture and mouse-scale dimensions removes the ambiguous bounds transform without a headset-specific correction |
| 2026-08-15 | Render each interactive result page as one full 1280x720 texture and restore the status atlas before the next SCAN | Native misses occurred before managed mapping near the lower visible surface while successful hits still dispatched the correct rail action; matching the stable calibration texture shape removes the result-atlas ambiguity, with a possible brief page-change flash accepted for reliable input |
| 2026-08-15 | Align the left-hand result pose to the saved VRCVA/SCAN launcher pose in settings version 5 and follow later launcher saves only while those poses still match | Opening the result at a different hand-relative transform forced an unexplained wrist movement after SCAN; transform-derived following keeps new/default results together without hidden state, while result width and deliberately independent position calibration remain intact and right/HMD placements are unchanged |
| 2026-08-13 | Implement a receive-only OSCQuery subset with no new dependency | VRChat only needs the advertised `/avatar` namespace and dynamic OSC target; a full OSCQuery/WebSocket client would add unrelated surface area |
| 2026-08-13 | Register DNS-SD on Windows interfaces but reject non-local senders | Windows returned `0x8007232A` when registering a strict loopback DNS-SD socket; local-address filtering and `OSC_IP=127.0.0.1` preserve same-PC processing without falling back to fixed port 9001 |
| 2026-08-14 | Replace global overlay laser mode with priority-zero SteamVR Input 2.0 and VRCVA-owned hit testing | OpenVR permits overlay actions to be observed without suppressing the scene app unless experimental high priority is selected; this keeps VRChat walking active |
| 2026-08-14 | Use a left-wrist chip and right-hand laser instead of physical tapping | It matches the accepted XSOverlay-like interaction, avoids pose-tap tuning, and removes user-authored OVRAS/OSC bindings from normal use |
| 2026-08-14 | Keep Windows Credential Manager for small-group BYOK and apply changes without restart | It is the strongest built-in per-user store available without adding an account service, while immediate runtime refresh removes the current usability defect |
| 2026-08-14 | Use a compile-time feature catalog before considering plug-ins or an agent kernel | It gives the next OCR/text-AI feature a stable boundary without introducing third-party code loading, broad tool authority, or premature compatibility promises |
| 2026-08-14 | Prepare the AI-development harness in-repository without installing it | Repeatable agent instructions and evidence formats are useful, but personal agent configuration remains an explicit user action |
| 2026-08-15 | Keep the result header display-only and place actions in a fixed body rail | The selected-view mapping and explicit full-surface intersection mask passed automated checks, but the current headset still had no usable pointer over the visible header; moving the visible controls into the established body region is safer than another hidden offset or hit-target workaround |

Capture, OCR, provider, model, and cost decisions remain in the [translation decision log](DESIGN-JAPANESE-TRANSLATION.md#decision-log), with their original dates and reasons.

## Official sources reviewed

All sources below were checked on 2026-08-11 through 2026-08-15.

These are retained research references, not a fresh verification of service rules or pricing.

- VRChat OSC overview and ports: <https://docs.vrchat.com/docs/osc-overview>
- VRChat OSCQuery and multiple-receiver discovery: <https://docs.vrchat.com/docs/oscquery>
- VRChat OSC avatar parameters and generated config behavior: <https://docs.vrchat.com/docs/osc-avatar-parameters>
- VRChat Expression Menu controls: <https://creators.vrchat.com/avatars/expression-menu-and-controls/>
- VRChat Terms of Service (effective 2026-02-09), including client modification restrictions: <https://hello.vrchat.com/legal>
- Valve OpenVR API overview: <https://github.com/ValveSoftware/openvr/wiki/API-Documentation>
- Valve OpenVR source/bindings, including compositor mirror texture access: <https://github.com/ValveSoftware/openvr>
- Valve OpenVR v1.26.7 `openvr.h`, matching the runtime interfaces used here and documenting overlay texture bounds/intersection coordinates: <https://raw.githubusercontent.com/ValveSoftware/openvr/v1.26.7/headers/openvr.h>
- Valve `IVROverlay` overview: <https://github.com/ValveSoftware/openvr/wiki/IVROverlay_Overview>
- Steamworks SteamVR overlay apps: <https://partner.steamgames.com/doc/features/steamvr/info>
- OpenAI Responses API quickstart: <https://platform.openai.com/docs/quickstart/make-your-first-api-request>
- OpenAI current model catalog: <https://developers.openai.com/api/docs/models>
- OpenAI API key handling: <https://developers.openai.com/api/reference/overview#authentication>
- XSOverlay Steam page and Window Capture capability: <https://store.steampowered.com/app/1173510/XSOverlay/>
- OVR Advanced Settings controller actions and keyboard-input guide: <https://github.com/OpenVR-Advanced-Settings/OpenVR-AdvancedSettings>
- Microsoft guidance for calling WinRT APIs from .NET desktop apps: <https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps>
