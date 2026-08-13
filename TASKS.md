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
- [x] Confirm the XSOverlay test notification and latest VRCVA result notification are visible in the headset.
- [x] Apply the OVRAS shortcut helper and validate `KeyboardTwo` from a left-grip + right-grip chord on Quest controllers.
- [x] Keep the progress notification to one second and move it after capture so neither it nor the Action Menu enters the captured eye image.
- [ ] Complete qualitative scoring for OCR accuracy, readability, and self-capture failures. Ten translated scans have already completed (1.59–6.70 s total); content-free timing and success metrics are recorded above.

## Post-MVP Milestone B — OCR/capture hardening

- [ ] Collect representative test images with explicit consent and no unnecessary personal data.
- [>] Measure single-pass versus conditional full-view band OCR accuracy and latency on real VRChat text.
- [x] Confirm on Windows that `OcrResult.Text` flattens lines, then build output from `OcrResult.Lines` so adaptive union/dedup receives line-sized inputs.
- [x] Compare unscaled, Fant 2x, and Cubic 2x OCR on the same self-authored image; use the better Cubic result for full-frame and band paths.
- [ ] Add optional ROI selection or contrast preprocessing only if the full-view fallback remains insufficient in measured scenes.
- [x] Document OpenVR compositor mirror feasibility, required APIs, overlay-exclusion invariant, window-capture fallback, and implementation size without implementing it.
- [x] Spike OpenVR compositor mirror capture (`GetMirrorTextureD3D11`) in a separate PR: Quest 3S acquisition, format/timing, coarse GPU sample, shared lifetime, overlay exclusion, explicit-save FOV comparison, and left-eye choice are complete; normal SCAN remains unchanged pending Stage 2 approval.
- [x] Promote the left OpenVR eye mirror to normal SCAN with a configurable eye, mandatory stale-frame discard, automatic window fallback, adaptive OCR pixel budget, route display, tests, Quest 3S latency/GPU measurements, and a SteamVR-stopped DPI-aware fixture fallback check.
- [x] Implement and device-check Windows Graphics Capture as the primary source; one content-free check returned a 1922×1041 frame.
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
- [x] Device acceptance: read at leisure, close immediately, scroll a long result, and run another scan without returning to desktop.
- [x] Device-check automatic interaction, explicit close/input restoration, OSC Action Menu settle delay, and the resized WPF window.
- [x] Device-check the ordered owned-overlay sequence (immediate acknowledgement, capture, OCR, result), flicker-free page navigation, laser page selection, and explicit close/input restoration. Minor visual polish remains acceptable follow-up work.
- [x] Add left/right controller-relative transforms, HMD fallback, a large-target in-VR position/size calibration screen, and atomic non-secret settings persistence for a wrist-like position.
- [ ] Device-check left/right/HMD placement, in-VR adjustment/save/cancel, and saved placement after a normal VRCVA restart.
- [ ] Add optional SteamVR input actions only after overlay stability is proven.

## Post-MVP Milestone E — Analyzer expansion

- [ ] Add analyzer capability metadata, typed inputs/outputs, and explicit data-boundary labels.
- [x] Add the OCR-only analyzer used when no translation provider is selected.
- [ ] Add multilingual translation analyzers.
- [ ] Add opt-in vision/VQA and summarization providers.
- [ ] Add puzzle-hint/object-recognition analyzers with clear uncertainty display.
- [ ] Add web search only as a distinct opt-in capability with query preview and source links.
