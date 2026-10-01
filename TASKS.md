# VRChat Visual Assistant — Implementation Tasks

Status legend: `[x]` complete, `[>]` in progress, `[ ]` pending, `[-]` deliberately deferred.

Design entry point: [DESIGN.md](DESIGN.md). Shared UI/input, feature registration, transport, privacy, and development rules belong to [the foundation design](docs/DESIGN-PLATFORM.md); capture, OCR, Japanese translation, feature costs, and measurement history belong to [the translation design](docs/DESIGN-JAPANESE-TRANSLATION.md). Voice input and video search follow [the approved video-search design](docs/DESIGN-VIDEO-SEARCH.md) and the new [implementation sequence](#voice-input-and-video-search--implementation-sequence) below. This split does not change the completion or device-validation status below.

## Milestone 0 — Research and decisions

- [x] Inventory repository, OS, WSL, Windows interop, installed SDKs, GPU, VRChat, SteamVR, and GitHub CLI.
- [x] Confirm that the repository starts empty and Git is not initialized.
- [x] Review official VRChat OSC, avatar parameter, Expression Menu, and Terms documentation.
- [x] Review official Windows capture, OCR, and desktop platform documentation.
- [x] Review official Valve OpenVR/SteamVR overlay and input documentation.
- [x] Review local and cloud OCR alternatives and OpenAI Responses API/model guidance.
- [x] Compare C#, C++, Rust/Python/TypeScript approaches.
- [x] Record architecture, privacy boundary, MVP scope, exclusions, and phased delivery; [DESIGN.md](DESIGN.md) now routes to the shared foundation and Japanese translation designs, preserving the original decisions and evidence.

Exit: a reviewer can explain why the MVP uses an external C# Windows app, local OCR, text-only translation, desktop UI, and a global hotkey.

## Milestone 1 — Public-safe repository scaffold

- [x] Initialize Git with `main` as the default branch.
- [x] Add `.gitignore` for secrets, local settings, logs, captures, dumps, NuGet/build/publish output, and editor metadata.
- [x] Add `.gitattributes` and `.editorconfig`.
- [x] Add solution and projects:
  - `src/VrcVa.Core`
  - `src/VrcVa.Infrastructure`
  - `src/VrcVa.Windows`
  - `tests/VrcVa.Core.Tests`
  - `tests/VrcVa.Infrastructure.Tests`
- [x] Pin .NET SDK expectations with `global.json` compatible with installed .NET 8.
- [x] Add `Directory.Build.props` with nullable references, warnings, deterministic builds, and analyzers appropriate for CI.
- [x] Add Windows GitHub Actions CI with restore, build, and tests and no secret use.
- [x] Keep GitHub vulnerability alerts and Dependabot security updates enabled; disable routine version-update PRs.
- [x] Add `SECURITY.md`, `CONTRIBUTING.md`, issue templates, and PR template.
- [x] Add an explicit unofficial/non-affiliation notice.
- [-] Add an open-source license only after the repository owner selects one.

Exit: a clean clone can be restored/built on a Windows runner, and common secret/artifact files cannot be staged accidentally.

## Milestone 2 — Platform-neutral scan pipeline

- [x] Define `ScanRequest`, `CapturedFrame`, OCR/translation outputs, `AnalysisResult`, stage timings, and typed failure codes.
- [x] Define minimal interfaces: `ICaptureSource`, `IAnalyzer`, `IOcrEngine`, `ITextTranslator`, and `IResultRenderer`.
- [x] Implement `TranslateAnalyzer` as OCR then translation composition.
- [x] Implement `ScanPipeline` with correlation ID, ordered stages, cancellation, single-flight behavior, disposal, and renderer updates.
- [x] Ensure exceptions are classified and surfaced; do not catch-and-ignore.
- [x] Add content-free structured/file log abstraction.
- [x] Unit-test success, empty OCR, capture failure, translation failure, cancellation, and overlapping scans.

Exit: fake adapters can drive Trigger → Capture → Analyzer → Result → Renderer without Windows or network dependencies.

## Milestone 3 — Windows capture vertical slice

- [x] Implement VRChat process/main-window discovery using Win32.
- [x] Detect missing or hidden capture targets with distinct messages.
- [x] When VRChat is minimized, restore it only for capture, wait for rendering, and return it to the prior minimized state.
- [x] Initially resolve the visible VRChat frame with DWM bounds and capture it with GDI; retain this only as superseded implementation history.
- [x] Replace screen-coordinate GDI with HWND-targeted Windows Graphics Capture after real use showed occluding desktop windows were included.
- [x] Create a free-threaded Direct3D11 frame pool, copy one window surface to an in-memory PNG, and release frame/session/pool resources deterministically.
- [x] Crop the captured surface to the DPI-aware Win32 client rectangle before OCR so title-bar text is excluded without word filtering.
- [x] Do not silently fall back to desktop pixels when direct window capture fails.
- [x] Implement an explicit local-image input path for OCR diagnostics; never enable implicit image persistence.
- [x] Add an isolated `VRChat.exe` fixture and repeatable script that verifies discovery, capture, and OCR without launching VRChat.
- [x] Add a content-free `--capture-vrchat-check` diagnostic that reports dimensions, byte count, and capture source without saving or printing OCR text.

Manual check:

1. Open a normal test window or VRChat with visible English text.
2. Run the diagnostic capture command.
3. Confirm reported dimensions and a non-empty in-memory frame.
4. Confirm no image appears under the repository, `%TEMP%`, or the log directory.

Failure split:

- No process/window: verify `VRChat.exe` is running and has a desktop mirror window.
- Auto-restore failure: verify VRChat is responsive, restore it once manually, and retry.
- Black/incorrect frame: verify `source: windows-graphics-capture-client-area`, check VRChat responsiveness, and compare HDR on/off before adding a provider fallback.
- Wrong scaling/crop: record the Windows Graphics Capture frame dimensions and display scaling without saving content.

## Milestone 4 — Local OCR

- [x] Restore WinRT contracts through the Windows target framework without requiring Visual Studio.
- [x] Implement in-memory PNG decode to `SoftwareBitmap`.
- [x] Prefer an installed `en` `OcrEngine`; fall back to `TryCreateFromUserProfileLanguages` with a visible warning.
- [x] Report available recognizer language tags and clear remediation when no engine can be created.
- [x] Normalize line endings and whitespace without changing recognized words.
- [x] Return `NoTextDetected` instead of sending empty input to translation.
- [x] Add an OCR smoke command that accepts an explicitly provided image.
- [x] Test on generated high-contrast English image and captured `VRChat.exe` fixture; Japanese OCR fallback recognized all fixture words.
- [x] Add a conditional, full-view three-band OCR retry with scaling, duplicate-line removal, candidate scoring, buffer disposal, and unit tests.
- [ ] Test OCR on at least three real VRChat screenshots supplied or captured during the owner's headset session.

Manual check:

1. Run the OCR smoke command on a high-contrast English image.
2. Confirm the source text is plausible and no network request occurs.
3. Repeat with an in-world sign at native mirror resolution.

Failure split:

- Engine unavailable: inspect installed OCR language tags; install English OCR through Windows language optional features if desired.
- Empty text: verify the captured frame, increase mirror resolution, crop closer, or test preprocessing.
- Garbled text: compare profile fallback with English engine before changing providers.

## Milestone 5 — Translation provider

- [x] Compare recurring and time-limited free tiers from Azure, DeepL, Google Cloud, and AWS against an offline Argos/OPUS-MT option.
- [x] Benchmark a GPU-based local translator and remove that path after observing about 3.7 GB additional VRAM use on the 8 GB PCVR GPU.
- [x] Keep the no-key default at `none` so a fresh installation never sends externally or incurs charges; return the local OCR text as a successful result.
- [x] Select the OpenAI Responses API as the current opt-in translation backend after owner approval; keep local OCR as the no-key default.
- [x] Implement and test the selected OpenAI backend, including owner-supervised Quest 3S PCVR translation.
- [x] Implement `OpenAiTextTranslator` using `HttpClient` and the Responses API.
- [x] Enable OpenAI only through an explicitly saved VRCVA credential or `VRCVA_TRANSLATION_PROVIDER=openai`; read only the VRCVA credential or `VRCVA_OPENAI_API_KEY`, never print it, and never reuse generic `OPENAI_API_KEY`.
- [x] Allow one-time UI registration in Windows Credential Manager so the VRCVA-specific key persists without plaintext files or repeated entry; retain the application-specific environment variable as a temporary override.
- [x] Make model, endpoint, and timeout configurable through documented environment variables.
- [x] Add an XSOverlay-accessible runtime selector for `gpt-5.4-nano` and `gpt-5.6-luna`; apply changes to the next scan and lock it during active scans.
- [x] Send only normalized OCR text, set `store: false`, bound output size, and request Japanese-only translation.
- [x] Bound OpenAI input to 4,000 UTF-8 bytes and API attempts to 10 per process, rejecting excess before network I/O without automatic retries.
- [x] Parse output items defensively and surface HTTP status/request ID without response bodies that may echo user text.
- [x] Distinguish missing key, authentication, rate limit, timeout, cancellation, invalid response, and provider failure.
- [x] Unit-test request shape and response parsing with a fake HTTP handler.
- [x] Do not place a real API key in tests, CI, tracked files, shell history examples, or screenshots.

Provider-selection acceptance:

1. [x] Start without provider variables and confirm the app returns OCR text while clearly reporting that translation is not configured.
2. [ ] Confirm the no-key state performs no translation HTTP request in an end-to-end run.
3. [x] Record the owner's decision to use OpenAI as the current opt-in provider before adding another adapter.

Optional OpenAI check:

1. Register the dedicated key through the WPF credential control, restart, and confirm the stored credential automatically selects OpenAI. Use the process-scoped environment override only for the non-persistent alternative.
2. Translate a fixed sentence and confirm logs contain lengths/timings only.
3. Confirm deleting the stored credential and restarting returns the app to OCR-only mode.

## Milestone 6 — WPF app and trigger

- [x] Build a compact WPF window with SCAN button, state, source text, Japanese result, timings, and copy button.
- [x] Show the active privacy boundary: `Local OCR / OCR text sent to <provider> / images not saved`.
- [x] Register a conflict-resistant global hotkey with Win32 and show registration failure clearly.
- [x] Make the hotkey configurable without a tracked secret/local config file.
- [x] Marshal renderer updates to the WPF dispatcher.
- [x] Disable SCAN while a request is running and provide cancellation semantics.
- [x] Keep the app usable without VRChat by exposing local-image diagnostic mode.
- [x] Add an accessible font size, selectable text, dark UI, and a window mode suitable for desktop debugging.
- [x] Fan results out to the WPF diagnostic view and XSOverlay localhost notifications.
- [x] Add an OpenAI-only model-toggle hotkey and report the selected nano/Luna model through XSOverlay.

End-to-end acceptance:

1. Start VRChat and leave its mirror visible.
2. Focus VRChat, look at English text, and press the global SCAN hotkey.
3. Within the configured timeout, see recognized English and Japanese in the assistant window.
4. Trigger again and confirm a fresh result with no persistent screenshot.
5. In the default state, confirm OCR text is shown and the UI explains that translation is unselected rather than sending or crashing.

## Milestone 7 — Verification and handoff

- [x] Run restore, Release build, and unit tests from Windows .NET 8.
- [x] Run framework-dependent publish from Windows .NET 8.
- [x] Run a source secret scan and inspect all intended Git files.
- [x] Verify the built application starts and responds on the development PC without Visual Studio.
- [x] Record the target headset environment: Meta Quest 3S PCVR with SteamVR and XSOverlay installed.
- [x] Record actual capture, OCR, translation, and total latency for ten scans: 10/10 completed, total 1.59–6.70 s, median 3.04 s, mean 3.40 s; attempt 11 was rejected before API I/O by the per-process usage guard.
- [x] Confirm by unit test that logs omit exception messages; implementation never sends image/text/key content to the logger.
- [x] Write `README.md` with prerequisites, exact commands, privacy behavior, normal use, local-image diagnostics, and stage-by-stage troubleshooting.
- [x] Record the vertical slice's then-known limitations: temporary mirror restoration, occluded window, OCR pack, cloud text, and notification-only VR rendering. Later milestones supersede the capture and VR-rendering limitations below.
- [x] Update completed boxes and decision log based on observed results.

Exit: the owner can reproduce the vertical slice and identify which stage failed without opening the source code.

## Milestone 8 — Git/GitHub publication

- [x] Read the publication workflow.
- [x] Create granular initial commits with a GitHub-provided noreply author address.
- [x] Re-authenticate GitHub CLI outside committed files.
- [x] Confirm owner/account and public repository name before the external write.
- [x] Create the public `vrchat-visual-assistant` repository without conflicting auto-generated files.
- [x] Push `main` and configure it to track `origin/main`.
- [x] Confirm the initial GitHub Actions build-and-test run passes.
- [ ] Enable branch protection/repository rules if account permissions support it.
- [ ] Create labels/milestones for `mvp`, `osc-trigger`, `openvr-overlay`, `privacy`, and `provider` work.
- [ ] Keep all API-backed tests secret-free; add repository secrets only if a later opt-in integration test is designed.

Current state: publication, authentication, and the initial CI run are complete. GitHub vulnerability alerts and Dependabot security updates are active, while routine version-update PRs are disabled; repository rules and labels remain pending.

## Post-MVP Milestone A — Quest 3S + XSOverlay in-VR validation

- [x] Start Quest 3S PCVR, SteamVR, XSOverlay, VRChat, and VRChat Visual Assistant together.
- [x] Evaluate `VRChat Visual Assistant` as an XSOverlay Window Capture.
- [x] Record device feedback: too many setup actions, oversized window, controller click failure, window hide/show, and unclear completion.
- [x] Reject the persistent Window Capture route for normal use.
- [x] Add the initial localhost XSOverlay notification renderer for SCAN start, OCR/translation result, and failure stage; the owned OpenVR panel later superseded its normal progress/result role.
- [x] Confirm from privacy-safe logs that two scans captured 1922×1041 frames and reached translation after OCR; the old no-provider behavior caused the missing result.
- [x] Add a safe OVR Advanced Settings helper for SCAN and model-toggle keyboard actions; do not modify user settings unless `-Apply` is explicit.
- [x] Confirm the XSOverlay test notification and latest VRCVA result notification are visible in the headset.
- [x] Apply the OVRAS shortcut helper and validate `KeyboardTwo` from a left-grip + right-grip chord on Quest controllers.
- [x] Keep the then-current XSOverlay progress notification to one second and move it after capture so neither it nor the Action Menu enters the captured eye image; later replace normal progress with the owned status panel.
- [ ] Complete qualitative scoring for OCR accuracy, readability, and self-capture failures. Ten translated scans have already completed (1.59–6.70 s total); content-free timing and success metrics are recorded above.

## Post-MVP Milestone B — OCR/capture hardening

- [ ] Collect representative test images with explicit consent and no unnecessary personal data.
- [>] Measure single-pass versus conditional full-view band OCR accuracy and latency on real VRChat text.
- [x] Confirm on Windows that `OcrResult.Text` flattens lines, then build output from `OcrResult.Lines` so adaptive union/dedup receives line-sized inputs.
- [x] Compare unscaled, Fant 2x, and Cubic 2x OCR on the same self-authored image; use the better Cubic result for full-frame and band paths.
- [ ] Add optional ROI selection or contrast preprocessing only if the full-view fallback remains insufficient in measured scenes.
- [x] Document OpenVR compositor mirror feasibility, required APIs, overlay-exclusion invariant, window-capture fallback, and implementation size without implementing it.
- [x] Spike OpenVR compositor mirror capture (`GetMirrorTextureD3D11`) in a separate PR: Quest 3S acquisition, format/timing, coarse GPU sample, shared lifetime, overlay exclusion, explicit-save FOV comparison, and left-eye choice were completed before the Stage 2 promotion recorded next.
- [x] Promote the left OpenVR eye mirror to normal SCAN with a configurable eye, mandatory stale-frame discard, automatic window fallback, adaptive OCR pixel budget, route display, tests, Quest 3S latency/GPU measurements, and a SteamVR-stopped DPI-aware fixture fallback check.
- [x] Implement and device-check Windows Graphics Capture as the automatic fallback when SteamVR or eye-mirror capture is unavailable; one content-free check returned a 1922×1041 frame.
- [>] Verify on the owner's VRChat window that client-area capture removes the Windows `VRChat` title while preserving in-world text.
- [ ] Verify with the latest GUI build that a browser/editor visibly covering the VRChat desktop window is not included in OCR.
- [ ] Evaluate Tesseract as a local `IOcrEngine` fallback, including native packaging and notices.
- [ ] Add provider selection UI with plain-language privacy impact; OpenAI nano/Luna model selection is already available.

## Post-MVP Milestone C — VRChat OSC trigger

- [x] Advertise dynamic OSC and OSCQuery ports through Windows DNS-SD and confirm VRChat auto-discovery plus a first-press trigger on PCVR while XSOverlay continues using 9001. Do not claim fixed port 9001.
- [x] Implement dynamic OSC/OSCQuery listeners that reject non-local senders. Windows DNS-SD cannot register a strict loopback bind, so `HOST_INFO` advertises `127.0.0.1` and callbacks allow only loopback or this PC's own addresses.
- [x] Parse only the configured address and expected Boolean/int value; reject malformed, oversized, bundled, multi-value, or differently typed packets.
- [x] Trigger on a rising edge using a monotonic clock and debounce duplicate/menu-reset packets.
- [x] Handle `/avatar/change` without logging its value; suppress an active state observed during the settling window until a fresh inactive value arrives, while allowing the first press after a quiet startup.
- [x] Document an unsaved/unsynced Expression Parameter and Button setup plus a no-capture OSC diagnostic.
- [x] Keep OSC disabled by default until explicitly enabled in both apps.

Current state: the Windows listener, minimal receive-only OSCQuery namespace, DNS-SD registration, parser, edge gate, diagnostics, and unit tests are implemented. PCVR confirmed that VRChat discovers `VRChat Visual Assistant` and sends the configured Bool parameter to its dynamic port while XSOverlay continues using 9001. The remaining gate is confirming from a second LAN device that OSCQuery receives no usable response.

## Post-MVP Milestone D — Interactive SteamVR result panel

**Implemented**

- [x] Initialize OpenVR as `VRApplication_Overlay` only when SteamVR is already running; never start or modify VRChat.
- [x] Render a static test texture and verify it appears over VRChat.
- [x] Feed a text texture from the existing `IResultRenderer` contract.
- [x] Keep the latest result visible until explicit close or the next scan.
- [x] Handle and device-check OpenVR pointer click and scroll events for close and long-text navigation.
- [x] Automatically enable close/scroll input while the result panel is visible, then release input on close, next scan, disconnect, or disposal; no interaction hotkey/OVRAS binding is required.
- [x] Clear overlay interaction on close, next-scan hide, disconnect, and disposal; never add time-based result dismissal.
- [x] Implement HMD-relative placement before tracked-device-relative placement.
- [x] Keep the WPF renderer and XSOverlay error fallback when SteamVR is unavailable.
- [x] Stop sending successful long-form results as fixed-duration XSOverlay notifications.
- [x] Verify that overlay rendering never logs or persists OCR/translation content.
- [x] Add left/right controller-relative transforms, HMD fallback, a large-target in-VR position/size calibration screen, and atomic non-secret settings persistence for a wrist-like position.

**Completed device evidence**

- [x] Device acceptance: read at leisure, close immediately, scroll a long result, and run another scan without returning to desktop.
- [x] Device-check automatic interaction, explicit close/input restoration, OSC Action Menu settle delay, and the resized WPF window.
- [x] Device-check the ordered owned-overlay sequence (immediate acknowledgement, capture, OCR, result), flicker-free page navigation, laser page selection, and explicit close/input restoration. Minor visual polish remains acceptable follow-up work.
- [x] Device-check in-VR adjustment/save/cancel, calibrated pointer alignment, and the next SCAN after leaving calibration.

**Additional completed device evidence**

- [x] Device-check left/right/HMD placement and saved placement after a normal VRCVA restart.

## Post-MVP Milestone E — Analyzer expansion

- [x] Add analyzer capability metadata, typed inputs/outputs, and explicit data-boundary labels through the compile-time feature descriptors and feature-neutral result sections.
- [x] Add the OCR-only analyzer used when no translation provider is selected.
- [ ] Add multilingual translation analyzers.
- [ ] Add opt-in vision/VQA and summarization providers.
- [ ] Add puzzle-hint/object-recognition analyzers with clear uncertainty display.
- [ ] Add web search only as a distinct opt-in capability with query preview and source links.

## Post-MVP Milestone F — SteamVR pass-through input and wrist launcher

Implementation status and headset acceptance are intentionally separate. An automated test or a successful historical click is not device acceptance for the current build.

**Implementation**

- [x] Add an OpenVR Input 2.0 action manifest and Oculus Touch default binding without joystick actions.
- [x] Add ABI-checked `IVRInput_010`, right-hand pose, and `IVROverlay_027` intersection adapters without a new package.
- [x] Add `--steamvr-input-pass-through-check`; keep product interaction unchanged until its device gate passes.
- [x] Define shared logical-surface primitives for texture drawing, pointer conversion, and hit testing, with direct edge, corner, +/-1 pixel, and coordinate-transform tests.
- [x] Replace result, page, scrollbar, close, and calibration mouse events with the priority-zero VRCVA pointer.
- [x] Keep `MakeOverlaysInteractiveIfVisible` false; input failure disables only VRCVA controls rather than taking scene input.
- [x] Replace joystick page navigation with visible previous/next controls and the existing scrollbar.
- [x] Keep Previous/Next/Close in a fixed visible body control rail whose rendered and hit rectangles are the same shared values; the title-only header is not an action surface on the supported runtime.
- [x] Add the left-wrist `DimChip -> ArmedChip -> Menu -> Scanning -> Result` state machine with pose-loss and cancellation cleanup.
- [x] Persist a launcher-specific left-controller transform and common chip/menu scale in versioned non-secret settings, with v1/v2 defaults and lossless version-3 Euler-to-quaternion migration.
- [x] Align the default left-hand result position and orientation to the saved VRCVA/SCAN launcher, migrate version-1 through version-4 left-hand result poses once in settings version 5, and follow later launcher saves only while the result pose still matches the old launcher; preserve result width and deliberately independent result placement.
- [x] Add a VR-only launcher calibration surface for XYZ, three-axis local rotation, scale, reset, cancel, and save; quaternion orientation and version-3 Euler migration prevent axis collapse near singularities.
- [x] Hide launcher/result/cursor surfaces under capture suppression before the established eye-mirror discard sequence.
- [x] Configure one full 1280x720 OpenVR intersection mask, distinguish native misses from selected-view mapping rejects in diagnostics, and render each interactive result page through a matching full 1280x720 texture view. Integrated tests cover `raw intersection -> full view -> logical point -> hit test` for result pages 1/2/3; generic atlas-view tests remain for atlas-backed surfaces. The header remains display-only.
- [x] Keep the visible cursor center at the native OpenVR intersection point and use overlay sort order for stacking; a panel-normal offset is forbidden because it creates view-dependent parallax from the logical hit target.

**Device evidence**

- [x] Pass-through gate: Quest 3S received exactly 20/20 VRCVA right-trigger edges with valid right-hand poses while walking continuously in VRChat, without movement loss, Action Menu, OSC, OVRAS, or a manual SteamVR binding edit.
- [x] Device gate: on the current Windows Release build, activate Previous/Next/Close across full-texture result pages 1/2/3, sweep the full lower rail without cursor loss, and confirm the scrollbar remains usable. The header is intentionally display-only.
- [ ] Device follow-up: while moving the HMD, confirm the visible cursor center remains on the hit target; verify disabled Previous/Next controls remain inert, the next SCAN restores the status atlas, and record any page-change flash.
- [x] Device gate: complete ten captures with zero launcher/result/cursor marker in the adopted eye image after capture suppression.
- [x] Device gate: calibrate the launcher in a natural reading pose, save, restart VRCVA, and verify the placement remains correct.
- [ ] Device follow-up: after that restart, explicitly re-check facing feedback, menu hit targets, and walking input together.
- [x] Device gate: from the saved launcher pose, select SCAN and verify the result replaces the menu at the same position and orientation without moving the left hand; confirm the saved placement survives a normal VRCVA restart.
- [ ] Device follow-up: verify Result Reset, another SCAN, and a normal VRCVA restart preserve an intentionally independent result size/position adjustment.

**Deferred or superseded**

- [-] Add a second optional SteamVR input-action interaction route. This is superseded by the shipped priority-zero action set and VRCVA-owned pointer; duplicating that route would recreate two coordinate/input contracts.
- [x] Keep desktop SCAN as recovery and document OSC/OVRAS as advanced fallback paths after the core wrist-launcher device gates passed.

## Post-MVP Milestone G — small-group onboarding and stable startup

- [x] Add a versioned application settings store and migrate version-1 result-panel placement without losing it.
- [x] Store onboarding and auto-launch preferences only; prove serialized settings never contain an API key or OCR/result text.
- [x] Add a single-instance guard for manual plus SteamVR launches.
- [>] Generate/register a fixed-key SteamVR application manifest and enable auto-launch only after an explicit wizard choice. Generation, ABI checks, and typed registration flow are complete; real SteamVR registration remains a device check.
- [x] Treat SteamVR-not-running registration as pending, never start SteamVR as a side effect, and preserve desktop operation.
- [x] Add a three-step first-run wizard for SteamVR startup, English OCR readiness, and optional OpenAI BYOK.
- [x] Keep the normal desktop window minimized for `--steamvr-autostart`, with an obvious way to reopen settings.
- [x] Make API-key save/delete affect the next SCAN without restart while preserving one process-wide ten-attempt limit.
- [x] Keep Credential Manager as the only persistent API-key store and the official OpenAI endpoint as the only saved-key destination.
- [x] Add a dependency-free self-contained beta publish path and document one stable extraction folder; do not build a commercial installer.

## Post-MVP Milestone H — feature foundation and AI-development harness

- [x] Add compile-time feature descriptors/catalog and keep unknown feature IDs as typed failures.
- [x] Separate backend selection/credential availability, process-lifetime usage policy, provider transport, and feature-specific prompt/result mapping.
- [x] Preserve local-only/no-key behavior and prove it performs zero HTTP requests.
- [x] Keep dynamic plug-ins, autonomous loops, arbitrary tools, vision uploads, and managed accounts out of this slice.
- [x] Prove the boundary with an unregistered summarization feature and fake text-model client before enabling another paid provider. It is not exposed in the UI and cannot create a new paid request path.
- [x] Prepare a repository-local `vrcva-development` Skill using the official skill scaffold.
- [x] Include project workflow, privacy boundaries, OpenVR ABI/coordinate rules, verification commands, review gates, and evidence format.
- [x] Add setup instructions only; do not copy the Skill into a personal skill directory or change Codex/Claude settings.
- [x] Validate the Skill package and forward-test it with an independent agent after implementation stabilizes. The final runbook path covers restore, focused/full verification, untracked files, baseline format failures, secret inspection, and device-evidence boundaries without installing the Skill.

Phase 3 completion requires Windows Release build/tests/format, `git diff --check`, a tracked-file secret scan, independent correctness review, and the Quest 3S acceptance gates above. Missing hardware evidence must remain explicitly unchecked rather than being inferred from unit tests.

## Voice input and video search — implementation sequence

Added: 2026-10-01. **計画のみ・全項目未着手**。正本は[共通音声入力](docs/DESIGN-PLATFORM.md#shared-voice-input--approved-design-not-implemented)と[動画検索設計](docs/DESIGN-VIDEO-SEARCH.md)。既存Milestoneの完了状態や実機証拠は変更しない。

翻訳時の「Milestone → チェックリスト → Exit」を継続し、新規タスクには依存先と確認条件を添える。I1〜L3の各項目を小さなPRの目安とし、対応するテストまで同じPRに含める。実装とWindows/Quest実機・有料APIの受入は別に完了を記録する。共通制御とVR画面を一度に置き換えず、未接続のadapterはfakeで検証してから公開入口へつなぐ。

推奨順: **I1 → I2 → I3 → I4 → J1 → J2 → J3 → K1 → K2 → K3 → K4 → K5 → L1 → L2 → L3**。依存を満たせばadapter作業は並行可能だが、`MainWindow` / `SteamVrResultPanel`の接続変更は直列にする。I1の判断は該当設計へ短く追記し、別の手続書は増やさない。

## Post-MVP Milestone I — text input and shared execution foundation

- [ ] **I1 — 未決定のadapterと設定境界を具体化する**（依存: なし）
  - 範囲: マイク選択/録音方式、音声形式・容量・無音判定・失敗音声の保持期限、GPT Transcribeの具体設定、設定場所/許容範囲/秒数丸め、解釈prompt/出力形式/入出力上限を決める。録音30秒、音声300秒・30送信、翻訳10回・解釈10回の独立初期値を維持し、個別変更と再読込時の消費量維持を定義する。
  - 範囲: yt-dlpの固定版・信頼できる配置・配布/更新方法・利用条件、process timeout/出力上限、許可URL/thumbnail配信先と画像制限を決める。公式仕様は実装時に再確認し、依存追加が必要ならそのPRで明示する。インストールや実API呼び出し自体はこのタスクの条件にしない。
  - 確認: 設計の「Remaining implementation decisions」の各項目が後続タスクへ対応する。既決定の操作を選び直さず、画面寸法の最終調整と性能/精度はL2/L3へ残す。

- [ ] **I2 — 画像とテキストの入力経路を分ける**（依存: I1）
  - 範囲: `Features.cs` / `Contracts.cs` / `Models.cs` / `ScanPipeline`に最小のtext handler境界を追加。認識文は共通sessionに保持し、用途の検索語とは分離する。音声/検索テキストの送信先を説明できるdata boundaryを加える。候補型/選択actionはK1で追加する。
  - 確認: fakeテキスト機能はcapture/OCRを0回、画像翻訳は従来経路を1回通る。入力種別不一致・未知IDを実処理前に拒否。翻訳primary section/互換表示、OCR-onlyの外部通信0回、未登録要約を維持する。

- [ ] **I3 — 全入口のsingle-flightと世代管理を共通化する**（依存: I2）
  - 範囲: `ScanPipeline._isRunning`と`MainWindow._uiScanRunning`の役割を整理し、アプリ寿命の共通gateへ既存SCAN全入口を接続。録音から文字起こし完了までを1処理、検索/コピーを個別処理とし、認識文/候補の待機中は解放する。session/operation、取消、後処理完了後の解放を共通化する。
  - 確認: fake録音・検索・コピーとdesktop/hotkey/OSC/腕SCANの競合、連打、runtime再構築で二重実行やqueue追加がない。Busy表示で現画面を壊さず、中止中は次を開始せず、閉じる/録り直し後の遅延応答をUI反映直前にも拒否する。既存capture抑制順序と共有OpenVR lifetimeを維持する。

- [ ] **I4 — 翻訳と検索解釈のquotaを分離する**（依存: I1、I3）
  - 範囲: `TranslationRequestQuota`、共通text client、`TranslationRuntimeFactory`とcomposition rootを見直し、翻訳/検索解釈を各10回の独立したプロセス寿命枠にする。設定値は用途別に検証し、同じ用途のclientを作り直しても同じ消費量を使う。音声の秒数/回数枠はJ2で別実装する。
  - 確認: 各上限の直前/一致/超過、片方を使い切っても他方は利用可能、送信開始後の失敗/取消も該当枠のみ消費、再読込/モデル切替/runtime再構築でリセットしないことをfake HTTPで検証。全用途のsingle-flightは維持し、翻訳の既定モデル/切替候補を変えない。

Exit: 既存翻訳の回帰を通し、fakeのテキスト機能を画像取得なしで安全に実行できる。新しい公開マイク/検索入口や有料送信をまだ有効にしない。

## Post-MVP Milestone J — shared microphone and transcription adapters

- [ ] **J1 — Windows録音adapterと音声sessionを作る**（依存: I1、I3）
  - 範囲: Windows側で明示開始、再押し停止、設定からの残り秒/初期30秒自動停止を実装。音声は容量制限付きメモリだけに保持する。音声opt-in/キー利用可否を確認してからマイクを開く。録り直しは全文置換し、旧文/検索語/候補を失効させる。
  - 確認: fakeマイク/時計で手動停止と上限到達が競合しても完了は1回。中止は文字起こしを開始しない。空/無音では検索へ進まず、無音による早期自動停止は加えない。デバイス喪失、閉じる、終了、SteamVR喪失時に停止・解放し、失敗音声も期限/サイズ上限を守る。正常認識後の解放はJ3で確認する。

- [ ] **J2 — GPT Transcribe通信と音声quotaを作る**（依存: I1、I4）
  - 範囲: Infrastructureに音声専用adapterを追加し、公式endpoint、専用Credential Manager運用、timeout/サイズ上限と応答検証を適用する。音声300秒・30送信の双方を送信前に一括予約し、翻訳/解釈とは別のプロセス寿命枠で保持する。
  - 確認: fake HTTPでrequest形式、キー無し/未同意の0送信、空/不正応答、認証/上限/timeout/取消を検証。300秒と30回それぞれの直前/一致/超過、秒数丸め、予約後の送信未開始時だけ返却、開始後の失敗/取消/本人再送の計数、再構築で消費量維持を確認。自動再送なし。音声にResponsesの`store: false`が適用されるとは表示しない。

- [ ] **J3 — 共通音声フローをWPFでつなぐ**（依存: J1、J2）
  - 範囲: `MainWindow`、設定/資格情報境界へ音声opt-inと送信先/費用/周囲の声への注意を追加。録音→文字起こし→全文表示、録り直し/閉じる、処理中の中止、段階別失敗を同じsessionへ接続する。用途actionの接続口を用意し、検索の公開ボタンはK5で実処理と同時に接続する。
  - 確認: fakeの全経路で成功後に音声を解放し、失敗の明示再試行だけ期限内音声を利用する。録り直し/閉じる/期限切れで破棄し、音声・本文・キーを設定やファイルに保存しない。キー保存済みでも音声未同意なら録音/送信0回。VRがなくてもWPFで中止・回復できる。

Exit: fakeマイク/HTTPで共有音声フローが完走し、独立した音声枠とバッファ寿命を証明できる。Windows実マイク・実API精度は未確認のままL2/L3に残す。

## Post-MVP Milestone K — bounded video search and desktop flow

- [ ] **K1 — 型付き候補と直接検索の契約を作る**（依存: I2、I3）
  - 範囲: text handler、検索provider境界、session/operation/候補ID/動画ID/title/任意thumbnail/正規watch URLの対応と許可された選択actionを定義する。「そのまま検索」は認識文を変更せず渡し、空白のみ/長さ超過は送信前に拒否する。
  - 確認: fake検索でAI/capture/OCRが0回、認識文不変、未知action/古いsession拒否。0/1/5/6/10件、5件ずつ最大2ページ、ページ送りで追加検索0回を検証。新検索/録り直しでは旧候補を選べない。

- [ ] **K2 — yt-dlpのmetadata検索adapterを作る**（依存: I1、K1）
  - 範囲: 固定実行パスと引数配列で`ytsearch10:`を1引数として渡す。設定/plugin/cookie取込みとshell連結を禁止し、simulate/skip-download/no-cache等の合意済み隔離を実装。stdout/stderrの並行・上限付き読取り、timeout、取消時の子プロセス終了/回収を行う。
  - 確認: fake processと自作JSONで引用符/改行/オプション風入力の安全な引数化、未導入/異常終了/不正JSON/出力超過/timeoutを検証。正常0件と全件不正を区別し、無効/重複entryは除外して部分取得を表示。YouTube ID/許可URLを照合してwatch URLを正規化し、任意host/schemeを拒否する。動画/音声保存、自動更新や依存インストールを検索の副作用にしない。

- [ ] **K3 — GPT-6 Lunaの検索語解釈を追加する**（依存: I4、K1）
  - 範囲: `ITextModelClient`と共通通信へ`gpt-6-luna`/`reasoning.effort=none`を明示対応。用途固有promptと上限付き出力検証、検索専用model設定/allowlistを追加し、認識文と確定検索語を別保持する。
  - 確認: fake HTTPで解釈1回・検索1回、共通文不変、検索解釈枠だけ消費。空/不正/超過出力は失敗とし、直接検索や別モデルへ暗黙fallbackしない。モデル生成URL/候補や任意toolを採用しない。検索だけ失敗した後の明示再試行は確定語を再利用し、追加解釈/文字起こし0回。翻訳モデル選択の回帰を確認する。

- [ ] **K4 — サムネイルとclipboardの副作用境界を作る**（依存: I1、K1）
  - 範囲: thumbnailは許可HTTPS先・redirect・byte数・timeout・デコード寸法を制限してメモリ取得する。clipboardはWPF dispatcher/STAへ分離し、書込み直前に現在のsession/候補IDを再確認する。コピーも共通gateを通す。
  - 確認: fake HTTP/clipboardで内部/ローカルURL、未許可redirect、巨大/不正画像を拒否し、資格情報を送らない。欠落/失敗でもplaceholderとtitleで選択可能。世代遅延と連打で誤コピーせず、成功後だけ完了表示。占有失敗時も候補/ページを保持して本人が再試行可能。自動無限retry、再検索、OS履歴/同期変更、終了時のclipboard消去をしない。

- [ ] **K5 — 2つの検索ボタンと候補表示をWPFに接続する**（依存: J3、K2、K3、K4）
  - 範囲: runtime catalogとcomposition rootへ検索を登録し、認識文直下へ最初から「そのまま検索」「解釈して検索」を配置。追加確認画面なしで選択経路を実行し、検索語/取得件数、5カード、前/次、入力へ戻る、閉じる、コピー状態を表示する。
  - 確認: fake end-to-endで両経路、長い認識文/title、0件/目的外候補、中止中/失敗/再試行を確認。最終ページは「取得した候補はここまで」とし、YouTube全体の終端を断定しない。コピー後も候補を保持し、入力へ戻っても原文不変。音声/解釈/YouTube/thumbnailの送信範囲とclipboard上書きが操作時に分かる。

Exit: WPFのfake end-to-endで録音から正しい候補URLコピーまで完了し、ページ移動や検索の再試行で成功済み有料段階を反復しない。VRChatへの貼り付け/再生操作は本人に残す。

## Post-MVP Milestone L — VR integration and separate acceptance gates

- [ ] **L1 — 共通の操作可能な進捗・失敗画面をVRへ追加する**（依存: I3、J3）
  - 範囲: `SteamVrResultPanel`とrendererへ録音残り秒/停止/中止、文字起こし等の進捗、取消回収中、段階別失敗/やり直しを追加。現行の非操作status atlasと失敗時の腕戻りだけに依存せず、翻訳も同じ共通制御へ接続する。
  - 確認: full 1280x720、body rail操作、表示専用header、描画/hit-test共通矩形、priority-zero入力とnative intersection上のcursorを維持。各状態の全controlを中心/端/角/±1px・有効/無効・押しっぱなしhoverで統合hit-testする。新overlayも翻訳captureのhide/boundary/discard/adopt対象とし、非表示中のWPF中止、切断/終了時の解放を回帰検証する。

- [ ] **L2 — 腕マイク・認識文・候補カードをVRへ接続する**（依存: K5、L1）
  - 範囲: `WristLauncherStateMachine` / `WristLauncherTexture`へマイク入口を追加し、認識文直下の2ボタン、5カード×2ページとbody railをWPFと同じsession/actionへ接続。長文/title/検索語のレイアウトを確認する。既存校正/配置保存、SCAN回復を維持する。
  - [ ] 実装/自動確認: 両ページ全候補/前次/戻る/閉じると認識文の全操作をraw intersection → 同一view逆変換 → logical hit-test → actionで検証。ページ境界、連打、閉じた後/録り直し後の遅延結果・画像・選択を確認する。
  - [ ] **実機ゲート（未実施）**: 現行Windows Release + SteamVR + Quest 3Sで実マイク開始/再押し/上限/中止/切断、VRChat内ミュート時の取得とOS/物理ミュートの違い、残り秒/長文の可読性を確認。文字起こし/候補はfakeでもよいことを記録し、両ページ全card/rail、頭を動かしたcursor整合、歩行維持、clipboard実書込み/占有回復、閉じる/再開/次SCANのoverlay除外と配置を確認する。実装/自動確認とは別に記録し、実機未確認ならL2全体は完了にしない。

- [ ] **L3 — 全体検証と任意の実サービス評価を記録する**（依存: L2の実装/自動確認。全体の自動検証は実機待ちでも進める）
  - [ ] 自動/静的確認: Windows restore → Release build → 全tests → format、`git diff --check`、公開差分のsecret確認と独立レビューを行う。偽の音声/本文/URL/例外/stdout/stderrを用い、ログ・設定・一時ファイルへ内容が残らないことを確認。OCR-only 0通信、翻訳・独立quota・capture順序・runtime解放の回帰を含める。CI通過とformat/実機の結果を混同しない。
  - [ ] **実サービスゲート（未実施・別途許可後）**: 最新の価格/保持条件を確認し、対象API・試行回数・予算・送信する自作サンプルを明示して承認を得てからGPT Transcribe/GPT-6 Lunaを評価する。固定版yt-dlpの実検索、metadata/thumbnail互換とファイル非生成も確認する。失敗を含む回数・音声秒数、段階別遅延/負荷、認識と補足指示の精度を内容を残さず記録し、未実施なら評価済みとしない。
  - [ ] 引渡し: 実装済み範囲に合わせREADME/設計/このチェックリストを更新し、固定yt-dlpの導入/更新手順、音声opt-in、3枠の上限、失敗時の回復と手動貼り付けを説明する。許可待ちの実API評価や残る実機不具合は未完了のまま明示する。

Exit: 新しい画面/マイクの実機証拠と有料APIの評価を、それぞれ現在のビルド・実施条件付きで記録する。過去の翻訳の成功やfakeテストだけで新機能の実用性を確認済みにしない。新しいアカウント/課金設定、常時録音、動画ダウンロード、VRキーボード、動的plugin、任意tool、自動貼り付け/再生は追加しない。
