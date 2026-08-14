---
name: vrcva-development
description: Safely design, plan, implement, and verify changes to the VRCVA Windows, OpenVR, capture, OCR, and translation application. Use when changing VRCVA code, tests, design documentation, or its AI development harness.
---

# VRCVA Development

Use this workflow for every VRCVA change.

## 1. Establish the change

1. Read `AGENTS.md` and inspect the relevant code before proposing behavior.
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

1. Run the focused tests, then the repository's Windows build/test/lint/type-check commands from its documented verification path.
2. Run a reviewer after implementation. Address correctness, security, and requirement failures; leave style-only preferences as notes.
3. Report exact commands and outcome evidence. State any test not run and why.
4. Distinguish automated verification from headset/SteamVR validation. Mark device-dependent behavior as unverified until an actual Windows + SteamVR + headset session confirms it.
5. Before commit or push, inspect the diff for secrets and unintended files. Commit/push only with the user's authorization and report the commit ID plus push/PR evidence.
