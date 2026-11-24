---
title: Glossary
description: The words these pages use for the parts of the chat and of a conversation with Claude, each with a short definition and a link to where it is explained.
---

The pages use a handful of words for the parts of the chat and of a conversation with Claude.
Some come from Claude Code itself, some from the interface. This is what each one means here.

## The conversation

### Turn

One exchange: the message you send and everything Claude does in reply (its thinking, the tools
it calls, its answer) until it stops and waits for you again. A turn can end with an answer, on
an error, on a limit, or because you stopped it.

### Transcript

The scrolling record of the conversation in a chat pane: your messages, Claude's answers, tool
rows and the other lines in between. See [The conversation](/cv4vs-agents/chat/conversation/).

### Session

A whole conversation as Claude Code saves it, one file per session, which you can reopen,
resume or fork later. See [Sessions](/cv4vs-agents/chat/sessions/).

### Tool call

One action Claude takes during a turn: reading a file, editing one, running a command,
searching, asking Visual Studio something.

### Tool row

How a tool call appears in the transcript: a collapsible row with what was asked (**IN**) and
what came back (**OUT**, or **ERR** on a failure). See
[Tool rows](/cv4vs-agents/chat/conversation/#tool-rows).

### Thinking

The reasoning a model writes before it answers, shown as a closed row above the answer when the
option is on. See [Thinking](/cv4vs-agents/chat/conversation/#thinking).

### Sub-agent

A separate Claude that the main one starts for a piece of work, with its own context, reporting
back when done. See [Sub-agents](/cv4vs-agents/chat/sub-agents/).

## The chat pane

### Pane

A tool window of the extension, docked in Visual Studio like any other. There are two kinds,
Chat and CLI: see [Two panes](/cv4vs-agents/two-panes/).

### Composer

The box at the bottom of a chat pane where you write a message, with its toolbar under it. See
[The composer](/cv4vs-agents/chat/composer/).

### Chip

A small rounded label for one item, often with an icon, which you can usually click. Chips on a
message show what was sent with it (the editor file and lines, images, attached files); in the
composer's toolbar they show what is going out or running, like the
[context chip](/cv4vs-agents/ide-integration/#the-context-chip) for the editor file.

### Notice

A short message at the top of the chat or above the prompt, separate from the transcript: a
turn that ended without an answer, a process that exited, a rate limit. See
[Notices](/cv4vs-agents/chat/conversation/#notices).

### View mode

How much of the work the transcript shows: every tool row, runs of them folded into one row,
or none. See [View mode](/cv4vs-agents/chat/conversation/#view-mode).

## Context and cost

### Context

Everything the model reads on each turn: the conversation so far, the tool results, the system
instructions and the files sent along. It has a limit, shown by the gauge in the composer. See
[Context, usage & statistics](/cv4vs-agents/chat/context-and-usage/).

### Token

The unit the model reads and writes in, a piece of a word on average. Context size, usage and
cost are all counted in tokens (`tok` in the interface).

### Compaction

What Claude Code does when the context fills up: it replaces the earlier part of the
conversation with a summary, so the session can go on. See
[Compaction](/cv4vs-agents/chat/conversation/#compaction).

### Prompt cache

The API's short-lived copy of the context already sent, which makes the next turn cheaper while
it lasts. See [The prompt cache](/cv4vs-agents/chat/context-and-usage/#the-prompt-cache).

## Control

### Permission mode

How much Claude may do without asking you first, from asking for every edit to acting on its
own. See [Permissions](/cv4vs-agents/chat/permissions/).

### Rewind

Putting the files back the way they were before one of your messages, from the copies Claude
keeps before it edits; the conversation is left as it is. See
[Rewinding files](/cv4vs-agents/chat/rewind/).
