# Contributing

The project is in MVP validation. Prefer small vertical changes that preserve the flow `Trigger → Capture → Analyzer → Result → Renderer`.

## Workflow

This is a solo project. Commit directly to `main` and push after every commit. A commit that stays on one machine is invisible to every branch and agent that starts from `origin/main`, so later work silently diverges from it.

Use a branch and a pull request only when one of these applies:

- Two agents are working at the same time.
- The change is large enough that you want to review or abandon it before it lands.

Delete the branch once it merges.

## Before committing

1. Read `DESIGN.md` and choose the existing component boundary.
2. Run `scripts\build.ps1` on Windows.
3. Run `scripts\test-capture.ps1` for capture/OCR changes when VRChat itself is closed.
4. Inspect staged files for secrets, captures, logs, dumps, user/world identifiers, and generated output.
5. Record any changed privacy boundary in the commit message, or in the pull request when you use one.

Do not add a real API key or network-backed secret test. Use a fake `HttpMessageHandler`. Do not log input/output contents to make a test pass.

An open-source license has not yet been selected. External contributions should wait until the owner adds a license and contribution terms.
