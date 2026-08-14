# VRCVA source-of-truth map

Read only the sections relevant to the requested change.

| Concern | Primary source | Verification companion |
| --- | --- | --- |
| Project rules and authority | `AGENTS.md` | Current task instructions |
| Product decisions and measurement history | `DESIGN.md` | `README.md` user-facing behavior |
| App composition and lifecycle | `src/VrcVa.Windows/MainWindow.xaml.cs`, `App.xaml.cs` | Windows test project |
| OpenVR runtime, ABI, and overlay ownership | `src/VrcVa.Windows/OpenVr/OpenVrRuntime.cs`, `OpenVrInterop.cs` | Matching Valve OpenVR header and OpenVR-focused tests |
| Placement and coordinates | `src/VrcVa.Windows/OpenVr/ResultPanelPlacement.cs`, `OpenVrInterop.cs` | Placement tests and headset validation |
| Eye-mirror privacy ordering | `src/VrcVa.Windows/OpenVr/OpenVrOverlayCaptureSequence.cs`, `OpenVrEyeMirrorCapture.cs` | `OpenVrOverlayCaptureSequenceTests.cs` |
| Desktop fallback | `src/VrcVa.Windows/Capture/` | Capture tests and Windows fixture check |
| Settings persistence | `src/VrcVa.Windows/Settings/` and placement store | Corresponding settings-store tests |

For OpenVR, use the exact header matching the interface version requested at runtime. Existing code intentionally supports published older interface versions; do not replace a version or table slot without proving ABI compatibility.
