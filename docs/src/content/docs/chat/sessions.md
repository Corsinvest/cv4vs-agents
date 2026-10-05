---
title: Sessions
description: One session store shared with the CLI and VS Code; resume, rename, delete, fork, and see what a pane is really running.
---

## One store, shared

No separate database: the extension reads and writes the CLI's own session store, so a conversation
started in the VS Code extension or in a terminal shows up here, and vice versa. Resume, fork and
rename all work on those shared files, so you can switch editor mid-project without losing context.

Where those files are is in [Settings and data](/cv4vs-agents/settings-and-data/).

## The session list

List, select-to-resume, inline **rename**, **delete** (with confirmation).

The list is always read fresh from the `.jsonl` files on disk each time you open it, with no cached
index that can go stale, so a session started or renamed elsewhere (VS Code, the CLI) shows up
immediately. It stays fast by reading files in parallel with head+tail 64 KB windows, never loading
whole files.

Sessions get **AI-generated titles**. A **New** split button starts a new one, Chat or CLI: which
is the default is **Default new session** under
[Options → General](/cv4vs-agents/options/#general).

## Fork

Fork a conversation into a new session from any user message. It opens a new pane from that point
and leaves the original alone.

To put the *files* back without leaving the conversation, see
[Rewinding files](/cv4vs-agents/chat/rewind/).

## Session info

**Info**, in the pane's More (…) menu: the session's title, id and `.jsonl` path, the working directory and
profile, and which `claude.exe` is running it (path, version and PID). Chat panes add what the page
currently weighs.

The first thing to open when something is running against the wrong session, the wrong folder, or
the wrong CLI.

## Restoring panes with the solution

**Restore panes on solution open** (opt-in) reopens the panes you had open for a solution, each on
its own session and profile. See [Several panes at once](/cv4vs-agents/guides/several-panes/).
