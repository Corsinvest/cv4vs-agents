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

## Going on to the next file

After **Keep** or **Undo** on one file, the file that follows it in the list opens the way **Open** opens it (in its own
editor with the review, or in the comparison window), so the changes can be gone through one file after another. The list
is by path, as the panel shows it, and after the last file it goes on with the first one still listed. It stops when
nothing else is listed.

- It happens for a click in the panel, for **Keep** and **Undo** in the bar at the bottom of the editor, and when the last
  change of a file has been kept or undone one at a time, or the file saved as it was.
- **Keep all** and **Undo all** open nothing, and neither does an **Undo** that could not be done: its reason stays in
  view.
- The file opens without waiting for the click to be answered, so a review that takes a moment to be drawn does not hold
  the buttons.

**Open the next changed file after Keep or Undo** (Options > Chat > Files) turns it off. It is read at each click.

## In the editor

A listed file that is open in an editor shows its changes in place, in the editor's own window, with
**GitHub Copilot's own review**: the lines Claude removed are drawn among the file's own, each change has its
**Keep** and **Undo**, and a bar at the bottom of the window has **Keep** and **Undo** for the whole file. The
review is Copilot's, not this extension's: it looks and behaves as it does when Copilot's agent has changed a
file, and Copilot counts the Keep and Undo you press as its own review actions. **Open** in the panel does the
same for a file that is not open yet, in the **preview tab**: the one the next file opened that way replaces, so
going through the list does not leave a tab behind for each file. Typing in it, or opening the file in any other
way, keeps it. A file that is open already stays as it is, and with Visual Studio's own setting for preview tabs
off (Tools > Options > Environment > Tabs and Windows) the file opens in a tab of its own. A file opened later, or
changed by Claude while it was open, is switched when it appears.

- **Keep** on a change accepts it as it is. It stops being a difference; nothing is written to the file.
- **Undo** on a change puts back what was there before Claude's change, in that change only, and Copilot
  saves the file.
- **Keep** and **Undo** in the bar at the bottom do the same for every change in the file. When Undo has put the file back
  as it was, byte for byte, it leaves the list; **Keep** accepts the file, and it leaves the list.
- When the last change has been kept or undone, the file leaves the list if it is now what it was before, byte
  for byte. A difference that is only in how the lines end leaves it listed, for Keep and Undo in the panel.
- **Keep** and **Undo** in the panel's row end the review first, and then do what they always do to the file.
- Copilot's keys, by default: with the caret on a line of a change, **Ctrl+Y** keeps that change and **Ctrl+N**
  undoes it; **Ctrl+Shift+Y** and **Ctrl+Shift+N** keep and undo the whole file. So during a review Ctrl+Y redoes
  only away from a change: **Ctrl+Shift+Z** redoes anywhere.

When more than one file is listed, the bar at the bottom also has two buttons of this extension's, the previous and the
next file of the list. They open that file the way Open does. The file list Copilot's bar carries is Copilot's own
working set, which has nothing of Claude's in it.

**Review the changes in the editor** (Options > Chat > Files) turns this off: the editor is left alone, and Open
shows the comparison window described above. Changing it applies at once to the files already shown.

This needs **GitHub Copilot in Visual Studio 18**, signed in to GitHub: the review is Copilot's, and where it is not
there nothing is drawn in the editor, Open shows the comparison window, and with Options > Debug > Log level at
*Info* the log says that Copilot's review is not available.

A file that cannot be reviewed this way is shown in the comparison window instead, and the log says why. The reasons:
Copilot is already reviewing the file for its own agent; another difference (a Git diff) is already shown in it; the
file, or its copy from before, is not plain UTF-8 or UTF-16 text; its byte-order mark is not what it was before
Claude's change; it mixes CRLF and LF line endings; it is read-only; its window shows two languages in one file
(Razor, for one); this Visual Studio's editor cannot show a difference in it; the list has no copy of the file
from before; or Claude **created** the file, which has no earlier text to show a difference against (the list's Keep and
Undo take it whole, and Undo deletes it). A file that was declined is tried again when it changes.

A few things work differently there:

- A file with changes of yours that are not saved is reviewed once you save it, and so is one that the editor has not
  reloaded since Claude wrote it: the difference would not be the one the list measured.
- After a **Keep** of a change, the baseline the list holds is the one the editor shows, so the counts, Open and
  an Undo of the whole file all agree with it. It is kept in the file's own encoding and line ending.
- A file that two chats list is shown for the one that listed or changed it last, or the one where you pressed
  Open, and each row says *also in Chat N*, naming the other chat.
- Closing the file's tab ends its review; opening it again, while it is still listed, starts it again.
- **Ctrl+Z** does not go back past the start of a review. Claude changes the file on disk and the editor takes the new text
  in as one edit, so Ctrl+Z would otherwise take back the whole of Claude's change at once, in the editor only. What you
  do during the review (a change undone with Undo, your own typing) can still be taken back. A change you kept cannot: Keep
  does not edit the file, and the list's Undo puts the file back to how it was before Claude's change with that change
  in it.

## Every change in one window

On Visual Studio 2026 the panel's header also has **Review all**. It opens the changes of every listed file in the one
window the editor has for that: a section per file, how it was before on the left and the file itself on the right, which
is opened for editing. It is for looking at everything together. Keep and Undo stay in the panel, and the window is the
list as it was when you pressed the button: a file kept afterwards stays in it until you close it, and pressing Review all
again shows the list as it is then.

Only files that can be shown as text are in it: not a deleted file, not a binary one, not one the list has no copy of from
before. A Visual Studio that does not have the window (2022 does not) has no button, and nothing else differs.

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

## A solution opened through a link

A folder can be reached by two names: `C:\Users\you\Dev` may be a junction to `D:\`, or a drive may be a `subst` of
another folder. Visual Studio keeps its documents under the name the solution was opened by, while git and, since
version 2.1.314, Claude Code itself name a file by its real path. The list turns a path under the chat's working
directory into the name the solution was opened by, so that a file is listed once, **Open** opens the document you
already have (and not a second copy as a miscellaneous file, outside every project), and the review in the editor finds
the tab. The same goes for the tools that open or save a file by the path Claude gives, such as `editor_open_file`, and
for autosave before an edit.

Only the working directory's own folder is mapped. A file outside it that happens to go through the same link keeps
the path Claude gave. A session's transcript is still looked for under the folder the CLI names after the working
directory: if the CLI files a chat under the real path and the extension looks under the other one, History cannot find
it. Making the two project folders under `~/.claude/projects` one folder (a junction from one to the other) fixes
that.

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
- **Closing Visual Studio forgets part of what you decided.** Resuming a session rebuilds the list from
  the CLI's history, which knows what Claude touched and nothing of what you did with it. A file you
  **kept** (a deletion you kept included) does not come back unless it has changed since: the extension
  remembers, for each chat, how each kept file looked (its length and a hash, nothing of its content)
  under `changes/` in the data folder, and forgets a chat's file after 30 days without the chat being
  open. A file over 256 MB is not remembered. A file you went through **change by change** in the editor is
  remembered once no change is left in it, each one kept or undone; while some are still there, it comes back
  with all of them, the ones you kept included, against the CLI's copy. And the files a command or a sub-agent
  changed are not rebuilt at all, since the CLI's history has no record of them.

## Options

**Track the files Claude changes** (Options > Chat > Files) switches the list off. It sits next to
**Keep file checkpoints (Rewind)**, which it depends on. **Track the files commands change** switches
off only the part that follows shell commands, and with it the snapshots. **Open the next changed file after
Keep or Undo** turns off [going on to the next file](#going-on-to-the-next-file). See
[Options](/cv4vs-agents/options/).
