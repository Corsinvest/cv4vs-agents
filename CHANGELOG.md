# Changelog

All notable changes to cv4vs Agents will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [1.0.0] - 2026-07-27

First stable release. Everything from the preview, plus the work below.

### Added

- **Statistics**: a full window showing where your tokens went — by profile, folder, project, day
  or single session. Daily chart, activity heatmap, per-model breakdown, and a tile summary.
  Reads the session files already on your disk; nothing is sent anywhere.

- **Usage**: your plan's limits and how much of them you have used, per profile.

- **Context usage**: what is actually filling the model's context in a session — system prompt,
  tools, memory files, messages — as a map you can read at a glance, so it is clear what to trim
  before compacting.

- **Turn settings in the composer**: thinking, effort, model and permission mode now sit on their
  own row under the message box. What the model is and what it may do without asking used to be
  three levels down a menu.

- **Read a reply aloud**: any assistant message can be spoken, with pause and resume.

- **Clickable file references**: when the assistant mentions a file — with or without a line number
  — it becomes a link that opens in the editor at that spot. Recognises around 270 extensions.

- **Session notices**: rate limits, CLI advisories and warnings stack in one place instead of
  replacing each other, and can be dismissed individually.

### Changed

- **The context gauge is always there**, empty until the first reply instead of appearing
  afterwards and shifting the toolbar around it.

- **The model picker no longer lists the same model twice.** "Default" is hidden when it is just
  another name for a model already in the list — which also means a reopened session shows the
  model you actually picked.

- **Renaming a session** now goes through the CLI rather than editing its file directly, so the
  running session and what you see agree.

- The attach button is a plus rather than a paperclip: its menu adds content as well as files.

### Fixed

- **Stopping a turn no longer looks like a crash.** It used to print a red error with an internal
  diagnostic line underneath the notice that already said you had interrupted it.

- The Copy icon in the Info dialog was nearly invisible on a dark theme.

- The image viewer opened with the wrong window chrome, and its copy button did nothing.

- Slash-command output (`/config`, `/context`, …) is shown instead of being silently dropped.

## [1.0.0-rc1] - 2026-07-22

First public preview. Requires the Claude Code CLI, installed separately with
`npm i -g @anthropic-ai/claude-code` — the extension drives it and never bundles it.

### Added

- **Chat pane**: a WebView2 chat wired into the IDE. Streaming replies, thinking blocks, tool calls
  with collapsible output, image attachments, and a prompt composer with slash commands, `@` file
  picker and prompt history.

- **CLI pane**: the real `claude.exe` in an embedded terminal (ConPTY), connected to the IDE over
  the same WebSocket channel the official VS Code extension uses. Both pane types can be open at
  once, on different working directories.

- **Diffs in the chat**: file edits render inline, switching between line-by-line and side-by-side
  as the pane is resized. Opening one gives a full viewer with four modes — auto, split, unified and
  raw patch — and an **Open in VS** button hands the same comparison to the editor's own diff viewer.

- **MCP server**: around 50 tools exposing the IDE to the agent — editor and selection, solution and
  project structure, symbol navigation, diagnostics, build, debugger, and test runner. Runs
  in-process, so the agent sees the live state rather than the files on disk.

- **IDE context**: the active document and selection are offered to the agent as context, shown as a
  chip in the composer and toggleable from the toolbar.

- **Sub-agents**: nested runs are shown inline with their own transcript, and can be opened in a
  pane of their own.

- **Sessions and history**: past conversations are read from the CLI's own `.jsonl` files — the same
  sessions the terminal `claude` sees. Resume, fork, or open in a new pane.

- **Profiles**: named configurations for working directory, model, permission mode and environment,
  so a pane can run against a different setup (including Anthropic-compatible endpoints) without
  touching the global settings.

- **Context and usage**: a live context gauge in the composer, and a stats dialog aggregating token
  usage and cost from the local session files.

- **Permission prompts**: tool approvals appear inline in the chat, with the affected file and a
  preview of the change.

- **Options**: four pages — General, Chat, Profiles and Debug — under Tools → Options → cv4vs
  Agents.

### Notes

- Visual Studio 2022 (17.0) and later, x64.
- The extension is GPL-3.0. `claude.exe` is Anthropic's and is not distributed here.
