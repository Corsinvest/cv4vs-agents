---
title: "Files Claude changed"
description: "A list of the files Claude changed in this chat, each against how it was before, with Keep and Undo."
---

When Claude edits files, the chat shows a card per edit and the files on disk are already
rewritten. The **changes chip** answers the question that comes after: *which files did it touch,
and what are they like now compared with before?* It lists them, one row each, and lets you
accept or put back each one.

## Using it

The chip sits in the row under the message box, beside the other chips, and appears with the first
edit. Its badge is the number of files. Clicking it opens the panel:

- **The file** (its name, and the folder it is in). Clicking it opens Visual Studio's own diff, the
  file as it was before Claude's first edit on the left and as it is now on the right, the same
  viewer the chat uses to [review a change](/cv4vs-agents/chat/diff/). Nothing is written.
- **M, A or D**: modified, added (Claude created it) or deleted.
- **+3 −1**: lines added and removed, counted between those two versions. A file that changed in
  every line shows an approximate figure, marked ≈; a binary file, or one too large to read, shows
  no figure at all.
- **Open, Keep, Undo**, the three buttons at the end of the row.
- **Keep all** and **Undo all** above the list. Undo all asks once before it touches anything.

The list refreshes when you open it: a file you edited or put back yourself since is measured
again, and one that is back to how it was leaves the list.

## What Keep and Undo do

**Keep** accepts the file as it is. Nothing is written, to the file or anywhere else: the row goes,
and the next edit of that file is compared with how it is now.

**Undo** puts the file back as it was before Claude's first edit of it, then drops the row.

- A file open in an editor is replaced as one edit and saved, so **Ctrl+Z** brings back what was
  there, and format on save runs as it does for your own saves.
- A file that is not open is written back byte for byte, so its encoding, byte-order mark and line
  endings come back as they were.
- A file Claude **created** is deleted, its tab closed first. A file Claude **deleted** is
  recreated, folders included.
- If a file cannot be restored (it is locked, or open in a designer that cannot take the text) the
  row stays, with the reason, so you can see it did not happen. Undo all carries on with the rest.

## What it compares with

The copy the CLI keeps before it first edits a file: the same ones [Rewind](/cv4vs-agents/chat/rewind/)
restores from, under `~/.claude/file-history`. So the list needs **Keep file checkpoints** to be on
(it is by default); with it off the chip says so instead of listing anything.

Two cases the CLI does not cover, and the extension covers for itself:

- **A sub-agent's edits.** The CLI keeps no copy for them, so a copy is taken just before the edit,
  once the editor's unsaved changes are saved, and dropped when the row goes.
- **The first edit after a Keep or an Undo**, for the same reason: there is no earlier copy of "now".

A file whose copy the CLI has since pruned is listed without one: you can open it, but not undo it.

## What it does not cover

- **What Claude changes by running a command** (`sed`, `git checkout`, a build script, a shell
  redirect) is not listed, and says so at the bottom of the panel. Only changes made through
  Claude's file tools are seen.
- **More than 200 files** are not listed beyond the first 200; the panel says so.
- **Closing Visual Studio forgets what you kept or undid.** Resuming a session rebuilds the list from
  the CLI's history, so a file you had already accepted comes back as a change.

## Options

**Track the files Claude changes** (Options > Chat > Files) switches the list off. It sits next to
**Keep file checkpoints (Rewind)**, which it depends on. See [Options](/cv4vs-agents/options/).
