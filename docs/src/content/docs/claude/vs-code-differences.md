---
title: Compared with the official VS Code extension
description: What is the same as in Anthropic's VS Code extension, because the same CLI is behind both, and what Visual Studio does differently.
sidebar:
  label: Compared with VS Code
---

This extension aims for parity with Anthropic's VS Code extension where it makes sense, but Visual
Studio is a different host, so several things are done differently, or don't exist there.

## What is the same

The chat is not a reimplementation of Claude. It drives the real `claude.exe`, and starts it the
way the VS Code extension does: over the stream-json control protocol, with the CLI told it runs
inside an IDE (`CLAUDE_CODE_ENTRYPOINT=claude-vscode`). What you get from the CLI is therefore the
CLI's, not a copy of it:

- **The same sessions.** One store, the CLI's own: a conversation started in VS Code or in a
  terminal shows up here, and vice versa. See [Sessions](/cv4vs-agents/chat/sessions/).
- **The same commands.** The `/` palette lists the CLI's own slash and skill commands, next to the
  extension's built-in actions.
- **The same plugins.** Plugins live in the CLI's store, so what you install here is the same set
  the CLI, the VS Code extension and the terminal see. See [Plugins](/cv4vs-agents/claude/plugins/).
- **The same settings and login.** `~/.claude/settings.json` (permissions, hooks, env) belongs to
  the CLI, and so does sign-in: the extension holds no credentials. See
  [Authentication and security](/cv4vs-agents/claude/authentication/).
- **The same editor channel.** Visual Studio pushes the editor selection to the CLI over the IDE
  integration channel, the same way the VS Code extension does.

Because it is the CLI doing the work, a newer CLI is handled by feature-detection, not by pinning a
version: the extension never bundles it. See [Updating Claude Code](/cv4vs-agents/claude/updating/).

What follows is where the two part ways.

## Two distinct panes, multi-instance

Open several chats and terminals side by side, each on its own session, docking as tabs. The Chat
pane (WebView2 + SDK-MCP) and the CLI pane (ConPTY + `--ide` WebSocket) are deliberately separate
startup paths. See [Two panes, one extension](/cv4vs-agents/two-panes/).

## Our own MCP tool suite for Visual Studio

The tools wrap VS's navigation, build and **debugger** (start/step/breakpoints/inspect/hot-reload)
and Error List, capabilities specific to the VS host, built language-agnostic via Roslyn reflection
and DTE, not tied to C#/VB only. See [MCP tools](/cv4vs-agents/mcp-tools/).

## Sub-agents you can see and control

Their tool calls are grouped under the Agent row that spawned them (last 3 steps, expand for the
full run) instead of being interleaved into the transcript, and a chip in the composer counts the
ones still running; click it to stop any of them, or all. See
[Sub-agents](/cv4vs-agents/chat/sub-agents/).

## Pane attention notifications

With several panes open (or VS in the background), an InfoBar or a layout-proof OS toast tells you
which pane needs input or has finished; the VS docked tab can't carry that state the way VS Code's
editor title does. See [Several panes at once](/cv4vs-agents/guides/several-panes/).

## Native `.jsonl` sessions

Sessions are read directly from the CLI's `~/.claude/projects/<folder>/*.jsonl` (head+tail reads to
avoid loading huge files); rename appends a `custom-title` entry, fork writes a new JSONL; no
separate session store. See [Sessions](/cv4vs-agents/chat/sessions/).

## Lazy, memory-light history

The chat holds nothing in memory: the transcript is read from the `.jsonl` on demand: the newest
page loads first, older history pages in only as you scroll up, and heavy blocks (images, sub-agent
transcripts, full diffs) are fetched only when you actually open them. A very long session opens
fast and stays light instead of loading the whole conversation up front.

## Changes reviewed in Visual Studio's own diff

Opening a file lands you in the real editor (optionally on the referenced lines); Edit/Write
changes open in Visual Studio's **native, interactive side-by-side diff** (not a static rendered
diff) so you review and edit with the full editor, then **save (Ctrl+S) to accept** or **close the
tab to reject** (the CLI applies the edit only if you saved). See
[Reviewing changes](/cv4vs-agents/chat/diff/).

## File references in prose are links

A bare `ClaudeInstall.cs:103` written in a sentence is a link here; the VS Code extension only turns
*markdown links* into file links. See
[Clickable file references](/cv4vs-agents/chat/file-links/#not-in-the-other-tools).

## Context gauge, usage and statistics

Rendered natively in the chat, including historical statistics across the current chat, the whole
project, or **all projects together**. See
[Context, usage & statistics](/cv4vs-agents/chat/context-and-usage/).

## Rewind

Restore the files to the state they were in before any message in the session, leaving the
conversation itself untouched; a dry-run preview lists the files and the lines added or removed
before anything is written, and clicking a file opens the pre-message copy against the current one
in Visual Studio's diff. See [Rewinding files](/cv4vs-agents/chat/rewind/).
