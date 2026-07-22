# Changelog

All notable changes to cv4vs Agents will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [1.0.0] - 2026-07-21

First public release. Requires the Claude Code CLI, installed separately with
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
