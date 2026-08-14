# VRCVA source-of-truth map

Read only the sections relevant to the requested change.

| Concern | Primary source | Verification companion |
| --- | --- | --- |
| Project rules and authority | Active session and current task instructions | Repository `AGENTS.md` when present |
| Product decisions and measurement history | `DESIGN.md` | `README.md` user-facing behavior |
| App composition and lifecycle | `src/VrcVa.Windows/MainWindow.xaml.cs`, `App.xaml.cs` | Windows test project |
| OpenVR runtime, ABI, and overlay ownership | `src/VrcVa.Windows/OpenVr/OpenVrRuntime.cs`, `OpenVrInterop.cs` | Matching Valve OpenVR header and OpenVR-focused tests |
| Placement and coordinates | `src/VrcVa.Windows/OpenVr/ResultPanelPlacement.cs`, `OpenVrInterop.cs` | Placement tests and headset validation |
| Eye-mirror privacy ordering | `src/VrcVa.Windows/OpenVr/OpenVrOverlayCaptureSequence.cs`, `OpenVrEyeMirrorCapture.cs` | `OpenVrOverlayCaptureSequenceTests.cs` |
| Desktop fallback | `src/VrcVa.Windows/Capture/` | Capture tests and Windows fixture check |
| Settings persistence | `src/VrcVa.Windows/Settings/` and placement store | Corresponding settings-store tests |
| AI feature selection and results | `src/VrcVa.Core/Features.cs`, `Models.cs`, `ScanPipeline.cs` | Feature catalog, analyzer, and presentation tests |
| Text-model transport and usage policy | `src/VrcVa.Infrastructure/OpenAiResponsesTextModelClient.cs`, `TranslationRequestQuota.cs` | Infrastructure transport tests and official provider documentation |

For OpenVR, use the exact header matching the interface version requested at runtime. Existing code intentionally supports published older interface versions; do not replace a version or table slot without proving ABI compatibility.
