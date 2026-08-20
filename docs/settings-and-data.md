<!--
SPDX-FileCopyrightText: Copyright Corsinvest Srl
SPDX-License-Identifier: GPL-3.0-only
-->

# Settings and data — where everything is stored

Nothing the extension writes lives inside your solution. Settings go to the Visual Studio
settings store, everything else to a folder under `%LOCALAPPDATA%`. Chat sessions are not
ours at all: they belong to the CLI, in `~/.claude`, shared with the Claude Code CLI and the
VS Code extension.

Uninstalling the extension leaves both trees behind — see [Removing everything](#removing-everything).

## What the extension writes

| What | Where |
|---|---|
| Environment profiles | `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\profiles.json` |
| Editor context-menu prompts | `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\editor-prompts.json` |
| `@` picker ignore rules | `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\picker-ignore.gitignore` |
| Open panes per solution | `…\cv4vs-agents\data\projects\<project-hash>\workspace.json` |
| Usage stats cache | `…\<project-hash>\<config-id>\stats-cache.json` |

Plus two caches in the same folder — `WebView2\` (chat UI storage) and `icons\` — both
rebuilt on demand if deleted.

## Options are not a file

The Options pages (**Tools → Options → cv4vs Agents**) are VS `DialogPage`s, so there is no path
to point at: Visual Studio persists them in its own settings store, per VS instance. They apply on
**OK/Apply**, never on a keystroke, and they don't travel with the solution or a copied folder.

**Profiles** and **Editor prompts** are the exceptions: they edit `profiles.json` and
`editor-prompts.json` (below) instead, so the launcher menu and the editor context menu can be
built without first materialising the Options page. **Ignored patterns** is a third: the row shows
where `picker-ignore.gitignore` is and its `…` button opens that file, since the content is a rule
list with comments — something to edit in a real editor and copy between machines.

## Our data folder

Root: `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\`

```
profiles.json                       environment profiles (name, enabled, env vars)
editor-prompts.json                 editor context-menu prompts (title, prompt, needs-selection)
picker-ignore.gitignore             extra `@` picker ignore rules, on top of the workspace's own
WebView2/                           WebView2 user-data (chat UI cache/storage)
icons/                              file-type icons rasterised from VS KnownMonikers
data/projects/<project-hash>/
    workspace.json                  panes open for this solution (+ each pane's profile)
    <config-id>/
        stats-cache.json            usage stats for this (solution, profile) pair
```

`<project-hash>` identifies the **solution folder** — the same hash the CLI uses for its own
project folders, so the two trees line up. `<config-id>` identifies the **profile's config
directory**, which is why stats are per (solution, profile) while `workspace.json` is
per solution only: a pane's profile is recorded inside that JSON.

`profiles.json` holds the environment variables you enter in the Profiles page —
`ANTHROPIC_AUTH_TOKEN` among them. It is a plain file with no encryption, readable by anything
running as your user.

## The CLI's own data (not ours)

Chat sessions, CLI settings, plugins and skills belong to `claude.exe` and live in
`~/.claude` (or `%CLAUDE_CONFIG_DIR%`, or a per-profile directory when the profile sets one):

```
~/.claude/
    settings.json                   CLI settings (permissions, hooks, env…)
    projects/<project-hash>/*.jsonl one file per session — the transcripts
    file-history/<session-id>/      copies taken before each edit, for Rewind
    ide/<port>.lock                 discovery file for `claude --ide`
```

`file-history/` holds **whole files**, not diffs — one copy per file per turn that edited it — and
the CLI never removes them. Deleting a session's folder only costs you the ability to
[rewind](chat/rewind.md) that session; turning **Keep file checkpoints** off
([Options → Chat](options.md#chat)) stops new ones being written at all.

We **read** the session `.jsonl` files directly (that's how history, resume, rename and the
usage stats work) and write only a `custom-title` entry when you rename a session. Because
the store is the CLI's, a conversation started in Visual Studio also appears in the CLI and in
the VS Code extension, and vice versa.

## Removing everything

1. Uninstall the extension (Extensions → Manage Extensions). This does **not** remove data.
2. Delete `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\` — profiles, workspace, caches.
3. Options remain in the VS settings store; they are inert without the extension and are
   overwritten if you reinstall.
4. `~/.claude` is the CLI's: deleting it removes **all** your Claude Code sessions, including
   those created outside Visual Studio.
