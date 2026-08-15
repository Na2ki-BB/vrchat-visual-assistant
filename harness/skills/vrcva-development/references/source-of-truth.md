# VRCVA source-of-truth map

Read only the sections relevant to the requested change.

| Concern | Primary source | Verification companion |
| --- | --- | --- |
| Project rules and authority | Active session and current task instructions | Repository `AGENTS.md` when present |
| Product decisions and measurement history | `DESIGN.md` | `README.md` user-facing behavior |
| App composition and lifecycle | `src/VrcVa.Windows/MainWindow.xaml.cs`, `App.xaml.cs` | Windows test project |
| OpenVR runtime, ABI, and overlay ownership | `src/VrcVa.Windows/OpenVr/OpenVrRuntime.cs`, `OpenVrInterop.cs` | Matching Valve OpenVR header and OpenVR-focused tests |
| Placement and coordinates | `src/VrcVa.Windows/OpenVr/ResultPanelPlacement.cs`, `WristLauncherPlacement.cs`, `OpenVrInterop.cs` | Placement and settings-store tests plus headset validation |
| Result texture, pointer mapping, and controls | `src/VrcVa.Windows/OpenVr/OverlaySurfaceSpec.cs`, `OpenVrInterop.cs`, `ResultPanelTexture.cs`, `SteamVrResultPanel.cs` | `OpenVrOverlayIntersectionTests`, `ResultPanelTextureTests`, `PointerActivationGateTests`, and current-build headset evidence |
| Eye-mirror privacy ordering | `src/VrcVa.Windows/OpenVr/OpenVrOverlayCaptureSequence.cs`, `OpenVrEyeMirrorCapture.cs` | `OpenVrOverlayCaptureSequenceTests.cs` |
| Desktop fallback | `src/VrcVa.Windows/Capture/` | Capture tests and Windows fixture check |
| Settings persistence | `src/VrcVa.Windows/Settings/` and placement store | Corresponding settings-store tests |
| AI feature selection and results | `src/VrcVa.Core/Features.cs`, `Models.cs`, `ScanPipeline.cs` | Feature catalog, analyzer, and presentation tests |
| Text-model transport and usage policy | `src/VrcVa.Infrastructure/OpenAiResponsesTextModelClient.cs`, `TranslationRequestQuota.cs` | Infrastructure transport tests and official provider documentation |

For OpenVR, use the exact header matching the interface version requested at runtime. The current overlay contract is checked against Valve OpenVR v1.26.7 and requests `IVROverlay_027`. Existing code intentionally supports published older interface versions; do not replace a version or table slot without proving ABI compatibility.

## Left-hand result placement contract

The default left-hand result position and orientation are derived from the saved wrist-launcher transform; result width remains an independent readability setting. Settings versions 1 through 4 align only a left-anchored result pose once during the version-5 migration and preserve its width. Version 5 and later load without unconditional resynchronization. When a launcher calibration is saved, a left-hand result follows only if its 12-element transform still matches the previous launcher within the defined rounding tolerance; the new pose preserves result width and is applied to persistence and live OpenVR state together. A position-adjusted result therefore remains independent, while a size-only adjustment continues following. Right-hand and HMD result placements are never changed by migration or launcher calibration. Selecting the left-hand anchor or choosing Reset while it is active uses the current launcher pose plus the default result width.

Launcher orientation is stored as a quaternion while the result placement retains Euler fields for its existing persistence and calibration contract. Conversion code must therefore prove equivalence through the complete `Rz * Ry * Rx` transform matrix, including yaw `+/-90` singularities and their neighborhoods; comparing Euler components is not sufficient.

## Result-texture coordinate and hit-test contract

Interactive results use one full 1280x720 texture per current page. Keep these coordinate spaces explicit; do not collapse or rename them all to “UV”:

1. `VROverlayIntersectionResults_t.vUVs` is a normalized point with a lower-left origin on the supported SteamVR/Quest path. Atlas-backed views expose atlas-global coordinates; a full result view spans 0..1.
2. `VRTextureBounds_t` selects the view. Valve defines its minimum corner as the texture's upper left and its maximum corner as the lower right. Full result pages use exact full bounds; half-texel insets remain part of actual atlas-cell bounds elsewhere.
3. VRCVA result controls use the 1280x720 logical surface with a top-left origin.

One immutable selected-view value must supply both the exact bounds passed to `SetOverlayTextureBounds` and the inverse transform from the raw intersection point to logical coordinates. Do not recompute one side from a page index, ideal grid fractions, or a separate set of constants. Result page changes upload a new full texture rather than selecting result cells 3, 4, or 5. Generic atlas-backed views must still change the raw point with cell bounds while preserving the same logical point.

Rendering and hit testing must consume the same shared control rectangle. A larger hit target must also be visibly rendered at that size; an invisible fallback strip, X-only action column, ignored Y coordinate, or duplicated rectangle is not allowed. Disabled Previous/Next controls may remain non-activating, but their state must be explicit rather than caused by a coordinate miss. On the supported SteamVR/headset path, the result header is display-only: Previous, Next, and Close belong to the fixed body control rail and must not be moved back into the header without new device evidence.

The VRCVA cursor's visual center must use the native tracking-space intersection point without a panel-normal offset. A physical offset introduces perspective parallax between the cursor and the logical hit point when viewed from the HMD. Keep the cursor parallel to the target surface and use its higher OpenVR sort order for compositing; do not fix visibility by moving it toward the controller. Cursor transform tests must cover both normal signs and oblique rays.

The native intersection surface is a separate contract. Configure
OpenVR with one explicit rectangle matching the 1280x720 logical surface.
Expanding a managed `Rect` after `ComputeOverlayIntersection` is not a fix for a
missing native surface. The 2026-08-15 device check found native misses near
both the header and lower visible result surface after applying that mask.
Production result pages therefore use a matching full 1280x720 backing texture,
while controls remain in the body rail. The subsequent 2026-08-15 current-build
device check passed across result pages 1 through 3 and the full lower rail
without cursor loss; the scrollbar also remained usable.
Diagnostics must preserve these distinct outcomes:
overlay hidden, native OpenVR miss, native hit rejected by selected-view
mapping, and mapped hit. Reset the bounded diagnostic window when a result
starts so launcher-only activity cannot consume all evidence before the result
surface appears.

Any change to result-panel layout, texture bounds, intersection mapping, or pointer activation requires one integrated table-driven test covering full-texture result pages 1, 2, and 3. Drive each control's center, edges, corners, and one pixel inside/outside through this complete chain, including the explicit disabled state of Previous/Next:

```text
logical point -> full view's exact bounds -> raw intersection
             -> the same selected view's inverse -> logical hit test -> action
```

Direct rectangle tests and direct transform tests remain useful but cannot replace this integrated test. The focused test set is `OpenVrOverlayIntersectionTests`, `ResultPanelTextureTests`, and `PointerActivationGateTests`.

Do not add a headset-specific scale or offset from visual impressions. First capture privacy-safe diagnostics containing only native-hit state, mapping state, full-texture/view state, page, raw coordinates, logical coordinates, target ID, and activated target. Full-texture results must report the legacy atlas-cell field as unset. Existing successful Next/Previous/Close traces prove only that those individual native hits survived mapping; they do not prove that the visible result texture is a continuous hit surface. Never log OCR/result content while diagnosing coordinates.

Automated coverage proves the ABI and coordinate contracts, not headset usability. The 2026-08-15 Windows Release build passed the recorded result pages 1, 2, and 3 control check, full lower-rail sweep without cursor loss, and scrollbar check. Any later change to the result layout, texture upload/view, pointer mapping, or activation path must repeat the complete current-build device gate; historical acceptance or a passing unit test is not a substitute for that changed build.
