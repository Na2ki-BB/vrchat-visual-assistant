# Contributing

The project is in MVP validation. Prefer small vertical changes that preserve the flow `Trigger → Capture → Analyzer → Result → Renderer`.

Before opening a pull request:

1. Read `DESIGN.md` and choose the existing component boundary.
2. Run `scripts\build.ps1` on Windows.
3. Run `scripts\test-capture.ps1` for capture/OCR changes when VRChat itself is closed.
4. Inspect staged files for secrets, captures, logs, dumps, user/world identifiers, and generated output.
5. Explain any changed privacy boundary in the pull request.

Do not add a real API key or network-backed secret test. Use a fake `HttpMessageHandler`. Do not log input/output contents to make a test pass.

An open-source license has not yet been selected. External contributions should wait until the owner adds a license and contribution terms.
