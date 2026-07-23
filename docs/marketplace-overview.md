<!--
  The Overview shown on the Visual Studio Marketplace listing, kept here so edits go through git
  like everything else. It is not part of the VSIX: paste it into the Overview field on the
  publishing portal, which is also where it is edited. Only <Description> in
  source.extension.vsixmanifest ships inside the package -- that is the short blurb search results
  use.

  Images must be absolute raw.githubusercontent.com URLs; relative paths do not resolve there.
-->
<img src="https://raw.githubusercontent.com/Corsinvest/cv4vs-agents/master/docs/images/logo.png" alt="cv4vs Agents" width="128">

# cv4vs Agents

**Claude Code inside Visual Studio.** A rich chat pane and an interactive terminal, both wired into
the editor, solution, debugger and build system.

It is **not** a fork of the CLI. It drives the real `claude.exe`; the binary is never bundled, and
version differences are handled by feature detection rather than by pinning a version.

![The chat pane and the CLI pane side by side](https://raw.githubusercontent.com/Corsinvest/cv4vs-agents/master/docs/images/chat.png)

---

## Before you start

This extension needs the **Claude Code CLI** installed separately — it drives it, and cannot work
without it:

```powershell
winget install Anthropic.ClaudeCode
# or: npm install -g @anthropic-ai/claude-code
```

Other platforms and methods are in Anthropic's
[setup guide](https://docs.claude.com/en/docs/claude-code/setup). If it is missing, the pane says so
and links you there instead of failing silently.

Then open **View → cv4vs Agents → Claude**. The IDE tools are wired up automatically — nothing to
configure.

---

## Two panes, one session

**Chat** — streaming replies, thinking blocks, collapsible tool output, inline diffs, image
attachments, and a composer with slash commands, an `@` file picker and prompt history.

**CLI** — the real `claude.exe` in an embedded terminal, connected to the IDE over the same channel
the official VS Code extension uses.

Both read the same session store, so a conversation started in one opens in the other — or in VS
Code. Panes can run on different working directories at the same time.

---

## What it can see and do

Around **50 MCP tools** expose the IDE to the agent, in process, so it reads the live state rather
than the files on disk:

| | |
|---|---|
| **Editor** | active document, selection, open files |
| **Solution** | projects, structure, references |
| **Navigation** | go to definition, find references, symbol search |
| **Diagnostics** | errors and warnings, including the ones an edit just introduced |
| **Build** | build and rebuild, with the errors handed straight back |
| **Debugger** | breakpoints, stepping, locals, call stack |
| **Tests** | discovery and runs |

Tools are language-agnostic where Visual Studio allows it: they go through Roslyn's per-document
language services or through `EnvDTE`, not through a C#-only path.

![Diffs and tool rows in the chat](https://raw.githubusercontent.com/Corsinvest/cv4vs-agents/master/docs/images/chat/subagents-panel.png)

---

## Built for long sessions

Nothing is built, read or started until you look at it. The chat holds **nothing in memory** — the
transcript is read from the session file on demand, newest page first, older pages as you scroll,
and heavy blocks (images, sub-agent transcripts, full diffs) only when you open them. Services, the
MCP server and the panes themselves start on first use, not on solution load.

A long session opens as quickly as an empty one.

---

## Context and cost, visible

A live gauge in the composer shows how full the context window is, and a stats dialog aggregates
token usage and cost from your local session files — per day, per project, per model.

![Context and usage statistics](https://raw.githubusercontent.com/Corsinvest/cv4vs-agents/master/docs/images/chat/statistics-dialog.png)

There is also a full-window **Statistics** tab: a navigable tree (All → Profile → Folder → Project →
Days/Sessions) drives summary tiles, a GitHub-style activity heatmap and per-day/per-model charts —
all aggregated locally, no telemetry. A companion **Usage** tab shows each profile's live plan and
rate-limit windows.

![Statistics document-tab](https://raw.githubusercontent.com/Corsinvest/cv4vs-agents/master/docs/images/statistics-document.png)

---

## Profiles

Each pane can run against a different configuration: working directory, model, permission mode and
environment. That includes Anthropic-compatible endpoints, so one pane can use a different provider
without touching your global settings.

![Profiles in the options pages](https://raw.githubusercontent.com/Corsinvest/cv4vs-agents/master/docs/images/options-profiles.png)

---

## Requirements

| | |
|---|---|
| **Visual Studio** | 2022 or 2026 (17.0+) — Community, Professional or Enterprise |
| **OS** | Windows (the CLI pane uses ConPTY, Windows 10 1809+) |
| **.NET Framework** | 4.8 — already present on any machine running VS 2022 |
| **Claude Code CLI** | installed separately, see above |

---

## Documentation and support

Full documentation, including MCP tools, options, sub-agents and architecture, is on
[GitHub](https://github.com/Corsinvest/cv4vs-agents).

- [Report a bug](https://github.com/Corsinvest/cv4vs-agents/issues/new?template=bug_report.yml)
- [Request a feature](https://github.com/Corsinvest/cv4vs-agents/issues/new?template=feature_request.yml)
- [Release notes](https://github.com/Corsinvest/cv4vs-agents/releases)

Problems in `claude.exe` itself belong to
[the CLI's own tracker](https://github.com/anthropics/claude-code/issues) — this extension drives
the CLI, it does not ship it.

---

## Credits and legal

Artwork by [filocorsa](https://github.com/filocorsa).

GPL-3.0-only — Copyright Corsinvest Srl. Made in Italy 🇮🇹

**Claude** and **Claude Code** are trademarks of Anthropic, PBC. **Visual Studio** is a trademark of
Microsoft Corporation. This is an independent extension by Corsinvest Srl, not affiliated with or
endorsed by either company; the names are used only to describe what it works with.
