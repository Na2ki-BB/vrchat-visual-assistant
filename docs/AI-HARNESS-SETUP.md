# AI development harness

This repository keeps its VRCVA development skill at `harness/skills/vrcva-development`. It is not installed automatically and does not change any personal Codex settings.

To make it discoverable in the current personal Codex skill directory, choose one manual method after reviewing the files:

```bash
mkdir -p "${HOME}/.agents/skills"
cp -R harness/skills/vrcva-development "${HOME}/.agents/skills/"
```

Or create a symbolic link instead:

```bash
mkdir -p "${HOME}/.agents/skills"
ln -s "$(pwd)/harness/skills/vrcva-development" "${HOME}/.agents/skills/vrcva-development"
```

The copy is a snapshot; refresh it manually after repository updates. The link reflects repository updates immediately. Codex normally detects skill changes automatically; restart it if the skill does not appear.

Invoke it explicitly with `$vrcva-development` for VRCVA design, implementation, verification, or harness work. It does not authorize dependency changes, external operations, commits, pushes, or headset claims without evidence.
