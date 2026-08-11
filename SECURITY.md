# Security Policy

## Supported versions

The project is pre-release. Only the latest commit on `main` will receive security fixes until versioned releases begin.

## Reporting a vulnerability

After the public GitHub repository exists, use a private GitHub Security Advisory for `Na2ki-BB/vrchat-visual-assistant`. Do not open a public issue containing:

- API keys or Authorization headers
- captured VRChat screens
- OCR or translation text with private information
- world, avatar, or user identifiers
- exploit details before a fix is available

Use a private GitHub Security Advisory for sensitive reports. Do not publish vulnerability details or private captured content in the public issue tracker.

## Security boundaries

This app must remain an external Windows utility. Reports proposing or depending on VRChat client injection, client modification, memory access, EAC bypass, credential capture, or undocumented privileged APIs are out of scope.

Runtime logs intentionally exclude images, recognized/translated text, API keys, HTTP bodies, and VRChat identifiers.

No translation backend is selected by default, so OCR text is not sent externally. Optional OpenAI mode must be explicitly selected and reads only the process-scoped `VRCVA_OPENAI_API_KEY`; the app deliberately does not fall back to generic key variables used by other tools.

Provider keys must never be committed, logged, shown in screenshots, or pasted into issues or chat. A newly researched provider must not be implemented or enabled until the owner makes an explicit decision.
