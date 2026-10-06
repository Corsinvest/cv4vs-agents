---
title: Spending less context
description: What actually reduces the tokens a conversation costs, and what only changes what the chat draws.
---

Tokens are a budget, and the chat's context window is the part of it you can watch filling up: the
[gauge in the composer](/cv4vs-agents/chat/context-and-usage/) shows how much room is left before
the CLI compacts the conversation. This is what you can actually do about it.

Worth saying first, because most of the Chat options page looks like it belongs here and does not:
**a setting that changes what the chat draws changes nothing about what was sent.** By the time the
tool result is on screen, it has already been through the model. Preview lines, Collapse tool
results, View mode (Focus and Hide tools included), Show tool errors inline: every one of those
is a rendering choice.
The knobs below are the ones that reach the wire.

## The four that reach the wire

| | Where | Default | What it does |
|---|---|---|---|
| **The eye** on the file chip | composer, **per pane** | open | Shuts off the file/lines block on every message. The biggest single knob: reach for it when the conversation has nothing to do with what is on screen. |
| **Send the selected text with the message** | Options → Chat | **off** | On, your selected code rides along with *every* message while that selection stands (the chip's icon turns from 🔖 to 🧾). Off, Claude opens the file when it needs it. Turn it on for unsaved buffers, where the copy on disk is the stale one. |
| **Extended thinking / effort** | `/` menu and model picker, **per session** | thinking off | Buys the model room to reason before answering; worth it on a hard bug, wasted on a rename. |
| **Send post-edit diagnostics** | Options → Chat | **off** | Feeds the errors an edit introduced back into the context after every edit. |

Three of the four already default to the cheap setting, so the one to know about is the eye: it is
per-pane and per-session, made to be flicked rather than configured.

## What travels with every message

Each message carries a short block naming the file you have open and, if you have selected code,
which lines, so Claude knows what "this method" means without you pasting it.

That block is a couple of dozen tokens, and *Options → Chat → Send the selected text with the
message* decides whether the code goes with it:

| | What the block says | Cost |
|---|---|---|
| **Off** *(default)* | `lines 40 to 78 from Foo.cs` | ~30 tokens, every message |
| **On** | the same, plus the selected code | ~30 tokens **plus the selection**, every message |

Off, Claude opens the file when it needs the code: once, on its own initiative, and free to read
around the selection. On, the code is there immediately but goes out again with **every** message
sent while that selection stands, including "yes", "go on" and "no, the other one".

Turn it on when you routinely ask about code you have not saved: on disk the file is stale, and
Claude reading it would see the wrong thing. Otherwise leave it off.

### The chip tells you which one is going out

A setting in a dialog you opened once is easy to forget, so the context chip carries the answer. The
icon at its right end names the shape of the block:

| Icon | Going out | When |
|---|---|---|
| 🔖 bookmark | the position: file and line numbers | the setting is off, **or** there is no selection |
| 🧾 code block | the position **and** the selected code | the setting is on **and** you have a selection |

The second row needs both conditions: an open file with nothing selected has no code to attach, so
it stays a bookmark whatever the setting says. Selecting only spaces or tabs counts as nothing
selected; there is no code in it to send. The icon follows what actually goes out, not how the
option is configured, and it disappears entirely when the eye is shut, since then nothing does.

The tooltip spells the same thing out in words, so the icon never has to be guessed at.

> **This does not stop the CLI from seeing your selection.** Visual Studio also pushes editor
> selections (the code included) over the IDE integration channel, the same way the VS Code
> extension does, and that path does not consult this setting. What the setting controls is the
> block prepended to *your message*, which is what accumulates in the conversation turn after turn.
> That accumulation is the cost worth managing; the live selection is re-sent, not stacked.

## The eye: sending nothing at all

Clicking the chip stops the block entirely (no file, no lines) for **that pane only**, until you
click it again. It then dims and picks up a struck-through eye; sharing is the ordinary state, so
only the exception is marked.

Worth doing when the conversation has nothing to do with what is on screen: a design discussion, a
question about another repo, a long back-and-forth about something abstract. This is the bigger of
the two knobs: the setting above changes what the block *contains*, the eye decides whether there
is one at all.

Picking an [editor prompt](/cv4vs-agents/options/#prompts) re-opens it: a question about "this code"
with nothing saying which code would reach the CLI as a question about nothing.

## Thinking and effort

The effort level is the slider at the foot of the model picker, named beside the model on its
button. Extended thinking is a switch in the `/` menu, under **Model → Thinking**.
Thinking buys the model room to reason before answering: real output tokens, on every turn it uses
them. It earns its cost on a hard debugging session and wastes it on "rename this variable".

These are per-session and live in the composer, not in Options, because they are the ones worth
changing *during* a conversation rather than once and for all.

## Post-edit diagnostics

*Options → Chat → Send post-edit diagnostics* (off by default) feeds the new errors an edit
introduced back to Claude after every edit. That is extra content into the context on each one.
It is [experimental and unreliable in Visual Studio](/cv4vs-agents/ide-integration/#post-edit-diagnostics) for reasons
that have nothing to do with tokens, but if you did turn it on, this is part of what it costs.

## What does not help

- **Autosave before Claude reads/writes**: turning it *off* costs more, not less: Claude reads the
  stale file, then has to ask for the editor buffer separately.
- **Respect `.gitignore` / Ignored patterns**: they filter the `@` picker's list. They make it
  harder to attach a huge generated file by accident, which is a guard-rail, not a dial.
- **Keep file checkpoints (Rewind)**: copies files to disk. Costs disk space, never context.
