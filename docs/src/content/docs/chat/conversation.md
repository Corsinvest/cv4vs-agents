---
title: The conversation
description: Tool rows, view modes, diffs, Markdown, copy, cost and time, read aloud, and how a long session stays light.
---

What the transcript shows, and how much of it.

## Tool rows

Collapsible tool call/result rows; click a file to open it in VS; inline tool errors (toggleable).
On an Edit the selection lands on the lines that changed, taken from the patch the CLI itself
computed, so it is right even after the edit has been applied and the file touched again (**Select
lines when opening file**).

## View mode

How much of the work the transcript shows, from the **View mode** slider in the `/` menu.

| Mode | Shows |
|---|---|
| **Full** | every row |
| **Focus** | every reply; each run of tool calls and thinking between two of them folds into one row (`5 tool calls · 1 failed`, `Running Bash…` while it works) that opens in place, with the rows exactly as you left them |
| **Hide tool calls** | the tool rows are dropped altogether |

Your answers to questions, plan decisions and the task list stay in every mode.

## Diffs

Edit/Write rows show a diff with the file's own line numbers and the context around each change,
syntax-highlighted, with the changed words marked inside an edited line; the row's title carries
the counts (`+3 −14`). Clicking it opens the change in VS's native side-by-side diff, where you
review (and edit) it with the full editor. See [Reviewing changes](/cv4vs-agents/chat/diff/).

## Markdown and copy

- **Full Markdown rendering**: tables, lists, blockquotes, links, and fenced code blocks rendered
  with **syntax highlighting** (highlight.js) across all common languages.
- **Everything is copyable**: a copy button on every message, tool row, code block and table, so
  any part of the conversation can be lifted out. A table copies as markdown, pipes and alignment
  included, so it parses back as the same table wherever you paste it.
- **Clickable file references**: `ClientEvents.cs:208` in an answer is a link that opens the file;
  see [Clickable file references](/cv4vs-agents/chat/file-links/).
- **Image lightbox** and a **welcome screen** for empty chats.

## Cost and elapsed time

The spinner counts the seconds while the turn runs (and while it is thinking); when it lands, the
hover-actions row carries what the turn cost. Agent rows do the same for their own run. Enable with
**Show cost and duration**.

## Read a response aloud

A speaker button on each answer reads it out with the system voice (Web Speech, no extra install);
click to pause, click to resume. The markdown is spoken as plain prose: no "asterisk asterisk", no
code blocks recited.

## Code review findings as a list

`/code-review` reports its findings as a readable list: severity first, each with its file and line
as a link that opens there, instead of the raw call data. The row grows to fit them, so nothing
hides behind an inner scrollbar; after a `--fix` run it also says what each finding became.

## Long sessions

- **Lazy history**: the transcript is read from the `.jsonl` on demand: the newest page (batch of
  50) first, older pages only as you scroll up, and heavy blocks (images, sub-agent transcripts,
  full diffs) fetched only when opened. Nothing is held in memory up front: long sessions open fast
  and stay light.
- **Back to the latest message**: scrolling up puts a button in the corner that returns you to the
  bottom of the transcript.
- **Sub-agents** are grouped under the Agent row that spawned them; see
  [Sub-agents](/cv4vs-agents/chat/sub-agents/).

## If the CLI stops

A bar is shown if the CLI process exits unexpectedly. See
[Troubleshooting](/cv4vs-agents/troubleshooting/).

## Related options

**Preview lines**, **Collapse tool results**, **View mode**, **Sticky user messages**, **Show tool
errors inline** and **Chat font size** are under [Options → Chat](/cv4vs-agents/options/#chat).
