---
title: The composer
description: Writing and sending, the slash palette, file mentions and attachments, the model and permission pickers, the keyboard and voice input.
---

The composer is the input area at the bottom of a Chat pane: the prompt, and a toolbar with
everything that shapes the next turn.

## Writing and sending

- **Multi-line prompt** with Send/Stop, Enter-to-send (or Ctrl+Enter, configurable),
  Shift+Enter for a newline.
- **Write while it is still answering**: the message is held until the turn ends, then sent. Its
  bubble appears straight away, greyed out so it does not read as already sent, and drops into place
  below the reply it was waiting on, so each answer stays under the question that prompted it. A chip
  in the composer's toolbar lists what is still waiting and takes one back out, without stopping the
  turn to do it; see [Messages waiting to be sent](/cv4vs-agents/chat/queued-messages/).
- **Prompt history**: recall previous prompts with ↑/↓ (shell-style).
- **Notice bar**: info/success/warning/error messages above the composer (e.g. rate-limit notices).

## The slash palette

A lightning button opens a unified palette; typing `/` filters. It lists the CLI's slash/skill
commands plus built-in actions (Attach, Mention, Clear, Switch model, Settings, Manage plugins,
Open Claude in Terminal, Help, Report a problem).

## Mentions and attachments

- **`@` mentions**: inline file picker over the **whole** project tree: no depth limit, and the
  filter matches the *path*, not just the file name, so `ui/comp` narrows before you type a name.
  Honours `.gitignore`, your own ignore patterns, and git's `core.excludesFile`.
- **Attachments**: upload files/images from the computer, or paste image data straight into the
  composer; removable attachment chips. Images are sent as images, PDFs as documents, the rest as
  text.

The file open in the editor is attached by itself, as a chip: see
[Editor context](/cv4vs-agents/ide-integration/).

## Model, effort and thinking

Pick the model, with its **Effort** slider under the list and the level beside the model name in
the toolbar; toggle extended **Thinking**, **Fast mode** and auto-switch-on-flag.

Model, permission mode and interrupt change on the live process, never a restart.

## Permission mode

Shift+Tab cycles it, and while the composer has focus its border takes the mode's colour, so the
change is visible where you are already looking. The modes and what each one asks are in
[Permissions](/cv4vs-agents/chat/permissions/).

## Keyboard

The `@` menu, the `/` palette and the model and permission pickers move with ↑/↓ and a page at a
time with PageUp/PageDown. **Esc** closes whatever is open (a list, a permission prompt, the edit
of a queued message) and stops the turn only when nothing is.

| Key | Does |
|---|---|
| Enter | send (or newline, with **Use Ctrl+Enter to send** on) |
| Shift+Enter | newline |
| Alt+Enter | queue a message into the one before it, once something is queued |
| ↑ / ↓ | previous and next prompt; move in an open list |
| Shift+Tab | cycle the permission mode |
| Esc | close what is open; stop the turn when nothing is |

## Voice input

Dictate the prompt instead of typing it: a mic button in the composer transcribes as you speak (Web
Speech API). The mic pulses while recording, and is hidden when the platform doesn't support it.

## Related options

**Use Ctrl+Enter to send**, **Spell check in the composer**, **Send the selected text with the
message** and the `@` picker's ignore rules are under
[Options → Chat](/cv4vs-agents/options/#chat).
