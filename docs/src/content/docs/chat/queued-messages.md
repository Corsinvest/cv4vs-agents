---
title: "Messages sent while Claude works"
description: "Write the next message while the turn is running: Claude reads it when the step in progress ends, in the same turn."
---

You do not have to wait for an answer to write the next message. Type it while the turn is running
and it goes to Claude Code **at once**. Claude reads it when the tool in progress returns, and
carries on in the same turn with what you said.

That is what makes a correction worth typing: "not that file", "use the other API" arrive while the
work can still be redirected, not after it is done.

## When it is read

| What Claude is doing when you send | When your message is read |
|---|---|
| Running a tool (a command, an edit, a search) | when that tool returns, in the same turn |
| Running a long command | when the command ends: a message cannot cut a tool short |
| Waiting on a sub-agent in the foreground | when the sub-agent returns |
| Only writing its answer | when the turn ends; a new turn then starts on your message |
| Nothing | at once, as always |

## Where it waits

Until it is read the message sits **at the bottom of the conversation**, paler than the others, so it
does not read as part of what Claude already has. When it is read it takes its place in the
conversation, above the work that follows it, and looks like any other message.

Several messages can wait together. They are read at the same moment:

- at the end of a tool they stay separate messages;
- at the end of a turn Claude Code joins them into **one** message, and the conversation shows
  that one, with the text and the attachments of each in the order you sent them.

## Taking a message back

Point at a waiting message and its actions appear under it, with a **cross** at the end. It takes the message
out of the conversation and puts its text and attachments back into the composer, to fix and send again or to drop.

- If you had already typed something, the returned text goes below it: nothing you typed is replaced.
- A message sent again waits at the end, after the others still waiting.
- Once Claude has read a message it is part of the conversation and cannot be taken back. If the
  cross is pressed at that very moment, a notice says so: *That message had already been read.*

## Stop

**Stop**, and **Esc**, stop the turn and also drop the messages still waiting: whoever presses Stop
wants Claude to stop, not to start on the next message. Their bubbles go with them.

## Attachments

- A message with an **image** is read like any other.
- A message with a **file** attached (text, PDF, anything that is not an image) waits for the end of
  the turn, and says so under the bubble: *Sent after this turn*. Read earlier, Claude Code delivers
  the message without the file, so it is held back on purpose. A message without a file sent in the
  meantime is not held up by it.

## Slash commands

A slash command sent while a turn runs always waits for the end of the turn. `/clear` and `/compact`
are the exception: they are the way out of a stuck turn, and act at once.

## Small print

- Switching session, or clearing the conversation, forgets the messages still waiting.
- If Claude Code stops unexpectedly, the messages still waiting go back into the composer: they
  were never read.
- A waiting message can be copied but has no Fork: it is not in the conversation yet.
- On an older Claude Code some of this is missing rather than broken: a message that cannot be
  taken back stays where it is, and messages waiting when you press Stop are sent after it.

## See also

- [The composer](/cv4vs-agents/chat/composer/): keys, attachments and the rest of the input area.
- [Options](/cv4vs-agents/options/): Enter vs Ctrl+Enter to send.
