---
title: What differs from the official VS Code extension
description: Where this extension does things differently from Anthropic's VS Code extension, or does things that do not exist there.
sidebar:
  label: Differences from VS Code
---

This extension aims for parity with Anthropic's VS Code extension where it makes sense, but Visual
Studio is a different host, so several things are done differently, or don't exist there.

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
