---
title: Two panes, one extension
description: The Chat pane and the CLI pane, both multi-instance and dockable, each on its own session.
---

The extension registers **two dockable tool-window types**, both **multi-instance**: open as many
Chat panes **and** as many CLI panes as you like, side by side, each on its own independent session,
docking as tabs. A busy dot on each pane's caption tracks which ones are working.

![The chat pane docked in Visual Studio](../../../images/chat.png)

![The CLI pane running the same session](../../../images/cli.png)

*The same conversation in both panes: the rich chat (inline diffs, tool rows, the editor file
attached to the prompt) and the real CLI. One session store: open it in either, or in VS Code.*

## The Chat pane

A rich WebView2 UI (TypeScript + Lit). It drives `claude.exe` in headless mode over the NDJSON
stream-json control protocol, and exposes the IDE to the CLI through an in-process MCP server.

What it adds on top of the CLI is in the **Chat** section of this site, starting from
[The composer](/cv4vs-agents/chat/composer/).

## The CLI pane

The interactive Claude Code CLI rendered with a real terminal (ConPTY), launching
`claude.exe --ide`. It reaches the same [IDE tools](/cv4vs-agents/mcp-tools/) over the WebSocket
MCP channel.

**Open Claude in Terminal**, in the chat's `/` menu, launches an interactive CLI session from a
chat.

## What changes without a restart

Model, permission mode and interrupt are **hot-swapped** on the live process: changing them never
kills the CLI. The process respawns only for what truly can't change at runtime (working directory,
resuming another session, fork).

## A different provider per pane

Each pane can run on a different provider (native Claude, GLM/z.ai, or any other
Anthropic-compatible host) and the IDE tools work the same either way. See
[Another provider](/cv4vs-agents/guides/another-provider/).

## Lazy and fast

Nothing is built, read or started until you actually look at it. The chat holds **nothing in
memory**: the transcript is read from the session file on demand, newest page first, older pages
only as you scroll, and heavy blocks (images, sub-agent transcripts, full diffs) only when you open
them. Same for the rest: services, the MCP server and the panes themselves start on first use, not
on solution load. Long sessions and large solutions stay as light and quick as an empty one.

With several panes open, see [Several panes at once](/cv4vs-agents/guides/several-panes/).
