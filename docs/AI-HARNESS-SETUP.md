# AI development harness

This repository keeps its VRCVA development skill at `harness/skills/vrcva-development`. It is not installed automatically and does not change any personal Codex settings.

To make it discoverable in a personal Codex skill directory, choose one manual method after reviewing the files:

```bash
cp -R harness/skills/vrcva-development "$CODEX_HOME/skills/"
```

Or create a symbolic link instead:

```bash
ln -s "$(pwd)/harness/skills/vrcva-development" "$CODEX_HOME/skills/vrcva-development"
```

When `CODEX_HOME` is unset, replace it with `~/.codex`. The copy is a snapshot; refresh it manually after repository updates. The link reflects repository updates immediately.

Invoke it explicitly with `$vrcva-development` for VRCVA design, implementation, verification, or harness work. It does not authorize dependency changes, external operations, commits, pushes, or headset claims without evidence.
