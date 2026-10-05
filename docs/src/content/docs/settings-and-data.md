---
title: "Settings and data: where everything is stored"
description: "Every file the extension writes, where it is, what belongs to the CLI instead, and how to remove it all."
---

Nothing the extension writes lives inside your solution. Settings go to the Visual Studio
settings store, everything else to a folder under `%LOCALAPPDATA%`, apart from a few lines in the
CLI's own files and scratch copies in `%TEMP%`, both listed below. Chat sessions are not
ours at all: they belong to the CLI, in `~/.claude`, shared with the Claude Code CLI and the
VS Code extension.

Uninstalling the extension leaves both trees behind: see [Removing everything](#removing-everything).

## What the extension writes

| What | Where |
|---|---|
| Environment profiles | `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\profiles.json` |
| Context-menu prompts | `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\prompts.json` |
| `@` picker ignore rules | `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\picker-ignore.gitignore` |
| Open panes per solution | `…\cv4vs-agents\data\projects\<name>-<hash>\workspace.json` |
| Usage stats cache | `…\<name>-<hash>\<config-id>\stats-cache.json` |

Plus two caches in the same folder: `WebView2\` (chat UI storage) and `icons\`, both
rebuilt on demand if deleted.

`picker-ignore.gitignore` exists only after you open it from Options: until then the built-in
defaults apply. `workspace.json` is written when the solution closes. `stats-cache.json` is thrown
away and rebuilt when a new version of the extension changes its format.

## Options are not a file

The Options pages (**Tools → Options → cv4vs Agents**) are VS `DialogPage`s, so there is no path
to point at: Visual Studio persists them in its own settings store, per VS instance. They apply on
**OK/Apply**, never on a keystroke, and they don't travel with the solution or a copied folder.

**Profiles** and **Prompts** are the exceptions: they edit `profiles.json` and
`prompts.json` (below) instead, so the launcher menu and the context menus can be
built without first materialising the Options page. **Ignored patterns** is a third: the row shows
where `picker-ignore.gitignore` is and its `…` button opens that file, since the content is a rule
list with comments, something to edit in a real editor and copy between machines.

## Our data folder

Root: `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\`

```
profiles.json                       environment profiles (name, enabled, env vars)
prompts.json                        context-menu prompts, one list per menu (editor, Error List, Output)
editor-prompts.json                 the editor's prompts before prompts.json: read once to carry them over, never written
picker-ignore.gitignore             extra `@` picker ignore rules, on top of the workspace's own
WebView2/                           WebView2 user-data (chat UI cache/storage)
icons/                              file-type icons rasterised from VS KnownMonikers
data/projects/<name>-<hash>/
    project.json                    the folder this data belongs to ({"path": "…"})
    workspace.json                  panes open for this solution (+ each pane's profile)
    <config-id>/
        stats-cache.json            usage stats for this (solution, profile) pair
```

`<name>-<hash>` is the **solution folder's** own name (up to 30 characters) followed by eight hex
digits of a hash of its full path, so `K:\source\repos\MyApp` becomes `MyApp-1a2b3c4d`. It is not
the CLI's project folder name: `project.json` records the path, because the hash cannot be read
back. `<config-id>` is the **profile's config directory** with every non-alphanumeric character
replaced by `-` (`C--Users-jane--claude`), which is why stats are per (solution, profile) while
`workspace.json` is per solution only: a pane's profile is recorded inside that JSON.

`profiles.json` holds the environment variables you enter in the Profiles page,
`ANTHROPIC_AUTH_TOKEN` among them. It is a plain file with no encryption, readable by anything
running as your user.

## The CLI's own data (not ours)

Chat sessions, CLI settings, plugins and skills belong to `claude.exe` and live in
`~/.claude` (or `%CLAUDE_CONFIG_DIR%`, or a per-profile directory when the profile sets one):

```
~/.claude/
    settings.json                   CLI settings (permissions, hooks, env…)
    projects/<project-hash>/*.jsonl one file per session, the transcripts
    file-history/<session-id>/      copies taken before each edit, for Rewind
    ide/<port>.lock                 discovery file for `claude --ide` (written by the extension)
```

`file-history/` holds **whole files**, not diffs: one copy per file per turn that edited it, and
the CLI never removes them. Deleting a session's folder only costs you the ability to
[rewind](/cv4vs-agents/chat/rewind/) that session; turning **Keep file checkpoints** off
([Options → Chat](/cv4vs-agents/options/#chat)) stops new ones being written at all. The
**[File history](/cv4vs-agents/documents/file-history/)** tab measures what they occupy, per project and per session, and
deletes them from there, backups whose transcript is already gone included.

We **read** the session `.jsonl` files directly (history, resume and the usage stats work that
way). What we write into the CLI's tree is short and deliberate:

| Where | What | When |
|---|---|---|
| `projects/<…>/<id>.jsonl` | a `custom-title` line | you rename a session and no live CLI can do it for us |
| `projects/<…>/<id>.jsonl` | an `ai-title` line | after a session's first exchange, if it has no title yet |
| `projects/<…>/<new-id>.jsonl` | a new transcript | **Fork** |
| `projects/<…>/<id>.jsonl` | deleted | **Delete** in the session list |
| `settings.json` | `"diffTool": "auto"`, only if the key is missing | opening a pane, so edits are reviewed in the IDE diff |
| `settings.json` | `remoteControlAtStartup` | the Remote Control start-up switch |
| `ide/<port>.lock` | the discovery file | while the IDE's MCP server is running |
| `file-history/<id>/` | deleted | **File history**, delete |

Outside both trees: opening a tool's input or output, an attachment, or a diff writes a copy into
`%TEMP%` (`<name>_in_<id>.<ext>`, `cv4vs-agents-<name>.<ext>`). The tool copies are marked read-only
on purpose; nothing removes them, Windows' own temp clean-up does.

 Because
the store is the CLI's, a conversation started in Visual Studio also appears in the CLI and in
the VS Code extension, and vice versa.

## Removing everything

1. Uninstall the extension (Extensions → Manage Extensions). This does **not** remove data.
2. Delete `%LOCALAPPDATA%\Corsinvest\cv4vs-agents\`: profiles, workspace, caches.
3. Options remain in the VS settings store; they are inert without the extension and are
   overwritten if you reinstall.
4. `~/.claude` is the CLI's: deleting it removes **all** your Claude Code sessions, including
   those created outside Visual Studio.
