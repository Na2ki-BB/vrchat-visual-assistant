# VRChat Visual Assistant — Implementation Tasks

Status legend: `[x]` complete, `[>]` in progress, `[ ]` pending, `[-]` deliberately deferred.

## Milestone 0 — Research and decisions

- [x] Inventory repository, OS, WSL, Windows interop, installed SDKs, GPU, VRChat, SteamVR, and GitHub CLI.
- [x] Confirm that the repository starts empty and Git is not initialized.
- [x] Review official VRChat OSC, avatar parameter, Expression Menu, and Terms documentation.
- [x] Review official Windows capture, OCR, and desktop platform documentation.
- [x] Review official Valve OpenVR/SteamVR overlay and input documentation.
- [x] Review local and cloud OCR alternatives and OpenAI Responses API/model guidance.
- [x] Compare C#, C++, Rust/Python/TypeScript approaches.
- [x] Record architecture, privacy boundary, MVP scope, exclusions, and phased delivery in `DESIGN.md`.

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
- [x] Resolve the visible VRChat frame with DWM physical-pixel bounds, avoiding DPI virtualization.
- [x] Capture one frame with GDI/`Graphics.CopyFromScreen` into an in-memory PNG.
- [x] Reject invalid/empty dimensions and release GDI/bitmap resources deterministically.
- [x] Implement an explicit local-image input path for OCR diagnostics; never enable implicit image persistence.
- [x] Add an isolated `VRChat.exe` fixture and repeatable script that verifies discovery, DWM bounds, capture, and OCR without launching VRChat.

Manual check:

1. Open a normal test window or VRChat with visible English text.
2. Run the diagnostic capture command.
3. Confirm reported dimensions and a non-empty in-memory frame.
4. Confirm no image appears under the repository, `%TEMP%`, or the log directory.

Failure split:

- No process/window: verify `VRChat.exe` is running and has a desktop mirror window.
- Auto-restore failure: verify VRChat is responsive, restore it once manually, and retry.
- Black/incorrect frame: disable HDR for the test, keep the mirror visible, and record whether GDI must be replaced by `Windows.Graphics.Capture`.
- Wrong scaling/crop: inspect DPI and client-to-screen coordinate logs (numbers only).

## Milestone 4 — Local OCR

- [x] Restore WinRT contracts through the Windows target framework without requiring Visual Studio.
- [x] Implement in-memory PNG decode to `SoftwareBitmap`.
- [x] Prefer an installed `en` `OcrEngine`; fall back to `TryCreateFromUserProfileLanguages` with a visible warning.
- [x] Report available recognizer language tags and clear remediation when no engine can be created.
- [x] Normalize line endings and whitespace without changing recognized words.
- [x] Return `NoTextDetected` instead of sending empty input to translation.
- [x] Add an OCR smoke command that accepts an explicitly provided image.
- [x] Test on generated high-contrast English image and captured `VRChat.exe` fixture; Japanese OCR fallback recognized all fixture words.
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
- [x] Return the default state to `none` so provider evaluation never causes external sending or charges; return the local OCR text as a successful result.
- [ ] Select a translation backend only after the owner explicitly approves one candidate.
- [ ] Implement and test the selected backend after that approval.
- [x] Implement `OpenAiTextTranslator` using `HttpClient` and the Responses API.
- [x] Enable OpenAI only through `VRCVA_TRANSLATION_PROVIDER=openai` and read only `VRCVA_OPENAI_API_KEY`; never print it or reuse generic `OPENAI_API_KEY`.
- [x] Make model, endpoint, and timeout configurable through documented environment variables.
- [x] Add an XSOverlay-accessible runtime selector for `gpt-5.4-nano` and `gpt-5.6-luna`; apply changes to the next scan and lock it during active scans.
- [x] Send only normalized OCR text, set `store: false`, bound output size, and request Japanese-only translation.
- [x] Parse output items defensively and surface HTTP status/request ID without response bodies that may echo user text.
- [x] Distinguish missing key, authentication, rate limit, timeout, cancellation, invalid response, and provider failure.
- [x] Unit-test request shape and response parsing with a fake HTTP handler.
- [x] Do not place a real API key in tests, CI, tracked files, shell history examples, or screenshots.

Provider-selection acceptance:

1. [x] Start without provider variables and confirm the app returns OCR text while clearly reporting that translation is not configured.
2. [ ] Confirm the pending state performs no translation HTTP request in an end-to-end run.
3. [ ] Record the owner's explicit provider decision before adding another adapter.

Optional OpenAI check:

1. Set `VRCVA_TRANSLATION_PROVIDER=openai` and the dedicated key in the current PowerShell process.
2. Translate a fixed sentence and confirm logs contain lengths/timings only.
3. Clear both process-scoped variables after the test.

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
- [ ] Record actual capture, OCR, translation, and total latency for at least five scans.
- [x] Confirm by unit test that logs omit exception messages; implementation never sends image/text/key content to the logger.
- [x] Write `README.md` with prerequisites, exact commands, privacy behavior, normal use, local-image diagnostics, and stage-by-stage troubleshooting.
- [x] Record known limitations: temporary mirror restoration, occluded window, OCR pack, cloud text, and notification-only VR rendering.
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
- [x] Add a localhost XSOverlay notification renderer for SCAN start, OCR/translation result, and failure stage.
- [x] Confirm from privacy-safe logs that two scans captured 1922×1041 frames and reached translation after OCR; the old no-provider behavior caused the missing result.
- [x] Add a safe OVR Advanced Settings helper for SCAN and model-toggle keyboard actions; do not modify user settings unless `-Apply` is explicit.
- [ ] Confirm the XSOverlay test notification and latest VRCVA result notification are visible in the headset.
- [ ] Apply the OVRAS shortcut helper after SteamVR is closed and bind two unused Quest controller gestures.
- [ ] Run five representative scans and record total latency, OCR accuracy, readability, and self-capture failures.

## Post-MVP Milestone B — OCR/capture hardening

- [ ] Collect representative test images with explicit consent and no unnecessary personal data.
- [ ] Measure full-window versus center ROI OCR accuracy and latency.
- [ ] Add optional ROI selection and 2x scaling/contrast preprocessing only if measurements improve results.
- [ ] Spike OpenVR compositor mirror capture (`GetMirrorTextureD3D11`) behind `ICaptureSource` so desktop visibility is unnecessary.
- [ ] Evaluate `Windows.Graphics.Capture` as a second window capture source, without assuming it can render a minimized VRChat window.
- [ ] Evaluate Tesseract as a local `IOcrEngine` fallback, including native packaging and notices.
- [ ] Add provider selection UI with plain-language privacy impact; OpenAI nano/Luna model selection is already available.

## Post-MVP Milestone C — VRChat OSC trigger

- [ ] Implement OSCQuery discovery and advertise a dynamic localhost receive port; do not claim fixed port 9001 because XSOverlay already uses it on this PC.
- [ ] Implement a localhost-bound OSC listener on the discovered/configured port.
- [ ] Parse only the configured address and expected Boolean/int value.
- [ ] Trigger on a rising edge and debounce duplicate/menu-reset packets.
- [ ] Handle avatar changes and explain generated OSC config behavior.
- [ ] Document an unsaved/unsynced Expression Parameter and Button setup.
- [ ] Keep OSC disabled by default until explicitly enabled in both apps.

## Post-MVP Milestone D — Custom SteamVR overlay fallback

- [ ] Spike OpenVR initialization as `VRApplication_Overlay` without starting or modifying VRChat.
- [ ] Render a static test texture and verify it appears over VRChat.
- [ ] Feed a text texture from the existing `IResultRenderer` contract.
- [ ] Implement dashboard/head-locked placement before tracked-device-relative placement.
- [ ] Add controller-relative transform and user calibration for a wrist-like position.
- [ ] Test compositor restarts, headset disconnects, overlay cleanup, and WPF fallback.
- [ ] Add optional SteamVR input actions only after overlay stability is proven.

## Post-MVP Milestone E — Analyzer expansion

- [ ] Add analyzer capability metadata, typed inputs/outputs, and explicit data-boundary labels.
- [x] Add the OCR-only analyzer used when no translation provider is selected.
- [ ] Add multilingual translation analyzers.
- [ ] Add opt-in vision/VQA and summarization providers.
- [ ] Add puzzle-hint/object-recognition analyzers with clear uncertainty display.
- [ ] Add web search only as a distinct opt-in capability with query preview and source links.
