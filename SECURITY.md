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

Runtime logs intentionally exclude images, audio, transcripts, search queries, candidate titles/URLs, recognized/translated text, API keys, HTTP bodies, raw child-process stdout/stderr, exception messages, and VRChat identifiers.

XSOverlay result notifications are sent only to `127.0.0.1` over its local External Message API. Their payload necessarily contains the OCR or translated text being displayed; it is not written to the project log or sent to an internet host by this renderer.

With no stored text key and no explicit environment configuration, translation remains local OCR-only. The GUI can explicitly save a text key in Windows Credential Manager at `VrcVa/OpenAIApiKey`, enabling optional translation. Temporary translation mode requires both `VRCVA_TRANSLATION_PROVIDER=openai` and the process-scoped `VRCVA_OPENAI_API_KEY`. Generic keys such as `OPENAI_API_KEY` are never a fallback. Stored keys are restricted to the official OpenAI endpoint; only the explicit environment-based translation developer configuration supports a custom endpoint.

Voice is disabled by default and requires separate opt-in plus a dedicated Credential Manager entry, `VrcVa/OpenAI/Voice`. Stopping a recording or reaching its duration limit sends the audio to the official OpenAI transcription API; cancelling before transmission does not. Search interpretation uses only the saved text key, never the voice or environment key. Direct search sends the transcript to YouTube via the fixed, verified yt-dlp binary. Interpreted search sends it to OpenAI first and then sends the resulting query to YouTube. Thumbnails use only validated YouTube image origins. No arbitrary model-generated tools or candidate URLs are accepted. The child process does not import user configuration, cookies, or plugins; it does not automatically download/update tools or save video/audio files.

Audio is held in memory and zeroed when released; a failed recording has a bounded, non-renewing retry lifetime. Transcripts, queries, candidates, and images are released on close or replacement, and are not persisted by VRCVA. Ordinary settings contain only non-secret preferences and limits. This is not a guarantee against OS paging, crash dumps, another process with the same user privileges, or provider-side retention. Official one-file yt-dlp may temporarily extract executable components. A copied URL remains in the Windows clipboard and may enter OS history/sync; VRCVA does not clear it on exit.

See [current usage/privacy instructions](README.md#音声動画検索を始める前に) and [separate validation gates](TASKS.md#post-mvp-milestone-l--vr-integration-and-separate-acceptance-gates). Automated fake tests do not establish real-device or real-service acceptance.

Provider keys must never be committed, logged, shown in screenshots, or pasted into issues or chat. A newly researched provider must not be implemented or enabled until the owner makes an explicit decision.
