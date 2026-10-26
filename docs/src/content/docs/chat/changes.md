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
- **M, A or D**: modified, added (Claude created it) or deleted. Hover it to see whether a command
  made the change rather than one of Claude's file tools.
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
  there, and format on save runs as it does for your own saves. This is for plain UTF-8 or UTF-16
  text. A file in another code page, or one whose byte-order mark is not what the editor would save,
  is written back on disk instead, and Visual Studio reloads it.
- A file that is not open is written back byte for byte, so its encoding, byte-order mark and line
  endings come back as they were.
- A file Claude **created** is deleted, its tab closed first. A file Claude **deleted** is
  recreated, folders included. A created file the editor holds unsaved changes to is not deleted:
  save or close it first.
- **Undo only acts on a file the list has measured.** If the file changed since (you saved it, a tool
  wrote it), the list measures it again and refuses that one Undo, with the reason. Look at what is
  in it now, and undo it again if you still want to.
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

## Files a command changed

The CLI keeps no copy of what a shell command overwrites (`sed -i`, a script, `git checkout`, a
formatter), whether Claude ran it through the Bash tool or the PowerShell tool. So for these the
extension takes its own, as long as the folder is in a **git repository**:

1. Before a command runs, it takes a snapshot of the working tree: every file git would list,
   tracked or not, minus what `.gitignore` excludes. The first one is taken when you send the
   chat's first message, so it is ready by the time Claude needs it. If nothing has changed since
   the last snapshot it is reused, so a command that only reads costs nothing while the tree is
   quiet; a build or a dev server writing files all the time makes each command take a new one.
   When Claude asks your permission first, the snapshot is brought up to date when you answer, so
   what you changed in the editor meanwhile is yours and not the command's.
2. When the command ends, and a file watcher saw something change, it takes a second snapshot and
   git compares the two. Both come from the same view of the tree, so they agree on what is
   ignored and on the case of a name: a build writing into `obj/` does not fill the list.
3. The files that differ get a row like any other, and Open, Keep and Undo work on it the same way.
   A file you had edited by hand before the command is compared with your version, not with an
   older one. Undo writes the old content back as git would check the file out, so its line endings
   follow your git settings (`core.autocrlf`, `.gitattributes`); where those do not convert
   anything, the bytes are the same ones.

The snapshots are kept in a private git store under the extension's data folder (see
[Settings and data](/cv4vs-agents/settings-and-data/)). It reads your repository's objects but never
writes to them: nothing is added to your history, your index or your object store. The store is
deleted when the chat closes. Files git does not ignore are copied into it, so keep build output and
large downloads in `.gitignore`. A repository that uses Git LFS is the one exception to "never
writes": git's own LFS filter keeps its large files in a cache under `.git/lfs`, whatever asks it.
Turn this off with **Track the files commands change** in [Options](/cv4vs-agents/options/).

## What it does not cover

- **What a command changes where git does not look.** A folder that is not in a git repository is
  not followed, and the panel says so, as it does when git refuses the repository (one that belongs
  to another user: mark it with `git config --global --add safe.directory`) or does not answer.
  Neither are files outside the repository, inside a submodule
  or a repository nested in it, or ones `.gitignore` excludes: an ignore rule is the only thing that
  tells source from build output, and without one every command would fill the list with its
  scratch files. A repository that is a whole drive or your profile folder (some dotfiles setups)
  is not followed either, since a snapshot would copy everything in it that is not ignored. And
  only the chat's own repository is followed: a command that works in another repository or
  worktree is not.
- **A file the command made is told from one that was always there** by its creation time. One that
  was ignored until a command changed the ignore rules, or that was moved in from elsewhere with
  its old creation time, is listed but has no copy to restore, so it cannot be undone.
- **More than 200 files** are not listed beyond the first 200, whatever changed them; the panel
  says so. When one command alone changes more than that, its rows also carry a warning.
- **A file another program holds open**, so that git cannot read it, is left out of the comparison
  until it is released: it is neither listed nor taken for a new file.
- **Anything that happens after the command returns**, such as a job it left running in the
  background, is not followed. And what you change in the editor *while* a command runs is listed
  as the command's, the same as whatever else changed in that time.
- **A command that is slow to start**, because the repository is too big to photograph in time (30
  seconds), runs without being followed. The next one is, once the snapshot is there.
- **A file git can only give as a pointer** (Git LFS) is listed without a copy: you can open it, but
  not undo it. So is a file larger than 100 MB that a command changed: copying what it was would
  fill the disk with a dataset or an installer.
- **Closing Visual Studio forgets what you kept or undid.** Resuming a session rebuilds the list from
  the CLI's history, so a file you had already accepted comes back as a change.

## Options

**Track the files Claude changes** (Options > Chat > Files) switches the list off. It sits next to
**Keep file checkpoints (Rewind)**, which it depends on. **Track the files commands change** switches
off only the part that follows shell commands, and with it the snapshots. See
[Options](/cv4vs-agents/options/).
