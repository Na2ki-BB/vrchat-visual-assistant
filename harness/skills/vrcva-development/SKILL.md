---
name: vrcva-development
description: Safely design, plan, implement, and verify changes to the VRCVA Windows, OpenVR, capture, OCR, and translation application. Use when changing VRCVA code, tests, design documentation, or its AI development harness.
---

# VRCVA Development

Use this workflow for every VRCVA change.

## 1. Establish the change

1. Follow the active session instructions. If the repository contains an `AGENTS.md`, read it; its absence is not a blocker. Inspect `README.md`, `DESIGN.md`, `TASKS.md`, and the relevant code before proposing behavior.
2. State the outcome, non-goals, affected files, and validation plan before a multi-file or uncertain change.
3. Keep product behavior, magic numbers, thresholds, and compatibility workarounds in production code with focused tests; do not encode them in this skill.
4. Read [source-of-truth.md](references/source-of-truth.md) before changing OpenVR interop, placement, capture sequencing, or persisted settings.

## 2. Preserve boundaries

- Treat captured pixels, OCR text, translations, API keys, credential-store contents, and environment secrets as private. Never write them to source, fixtures, logs, issue text, or commits.
- Preserve the explicit in-memory capture contract. Do not add automatic image output or silently replace a failed capture route.
- Do not start SteamVR or VRChat as a side effect. OpenVR must retain shared, reference-counted runtime ownership.
- Treat OpenVR ABI slots, calling conventions, struct packing, interface versions, and coordinate transforms as source-derived facts. Verify them against the matching official header and the existing interop before implementation; do not infer slot numbers or coordinate directions.

## 3. Implement deliberately

1. Make the smallest cohesive production and test changes.
2. Maintain cancellation, disposal, fallback, and interaction-release paths whenever adding UI, capture, or OpenVR state.
3. Add or update focused tests for changed behavior, especially ABI layouts, capture ordering, persistence, and failure paths.
4. Keep the desktop fallback functional when SteamVR is absent.

## 4. Verify and hand off

1. From Windows PowerShell at the repository root, confirm the SDK and restore once before using any `--no-restore` command:

   ```powershell
   dotnet --info
   dotnet restore .\VRChatVisualAssistant.sln
   ```

2. Run tests focused on the changed behavior first. For the AI feature foundation, use:

   ```powershell
   dotnet test .\tests\VrcVa.Core.Tests\VrcVa.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FeatureCatalogTests|FullyQualifiedName~SummarizeAnalyzerTests"
   dotnet test .\tests\VrcVa.Infrastructure.Tests\VrcVa.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OpenAiResponsesTextModelClientTests|FullyQualifiedName~OpenAiTextTranslatorTests"
   dotnet test .\tests\VrcVa.Windows.Tests\VrcVa.Windows.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FeatureResultPresentationTests|FullyQualifiedName~TranslationRuntimeTests"
   ```

3. Run the repository-wide compile/type check, all tests, and formatting check. In this .NET repository, `dotnet build` is the type check and `dotnet format` is the lint/format check:

   ```powershell
   dotnet build .\VRChatVisualAssistant.sln -c Release --no-restore
   dotnet test .\VRChatVisualAssistant.sln -c Release --no-build --no-restore
   dotnet format .\VRChatVisualAssistant.sln --verify-no-changes --no-restore
   ```

   If the full formatting check reports pre-existing files outside the task, do not edit them silently. Report the full-check failure, then rerun `dotnet format` with `--include` followed by every changed/new C# path from `git status --short` to prove the current change is clean.

4. Inspect the complete worktree, including files that are not tracked yet:

   ```powershell
   git status --short
   git diff --check
   git diff --cached --check
   ```

   Read every path shown by `git status --short`. `git diff` does not show `??` files, so inspect each untracked file directly before staging it.
5. Run a reviewer after implementation. Address correctness, security, and requirement failures; leave style-only preferences as notes.
6. Report exact commands and outcome evidence. State any test not run and why.
7. Distinguish automated verification from headset/SteamVR validation. Mark device-dependent behavior as unverified until an actual Windows + SteamVR + headset session confirms it.
8. Before commit or push, inspect the diff for secrets and unintended files. Commit/push only with the user's authorization and report the commit ID plus push/PR evidence.
