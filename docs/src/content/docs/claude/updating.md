---
title: "Updating Claude Code"
description: "Update the Claude Code CLI from the chat notice or the menu, and what each outcome means."
---

The extension drives the Claude Code CLI and never bundles it, so the CLI is updated separately
from the extension. You can do that without leaving Visual Studio.

## Two ways in

**The notice in the chat.** When a newer Claude Code exists, the first chat you open in a Visual
Studio session says so at the top, with an **Update** button:

```
Claude Code 2.1.288 is available (you have 2.1.287)   [Update]
```

The row turns to `Updating Claude Code...` while it runs, then to the outcome, and stays until you
close it.

**The menu.** **View → cv4vs Agents → Update Claude Code**, at any time, with or without a pane
open. The status bar shows `Updating Claude Code...` while it runs, and the outcome comes in a
message box. The entry is greyed while an update is under way.

The menu is there because the notice is easy to lose: it shows once per Visual Studio session,
only in a chat (the CLI pane has none), and it is gone once you dismiss it.

Both do the same thing, and only one update runs at a time: asking again while one is under way
joins it.

## What it runs

`claude update`, on the binary the extension uses: the one set in Tools → Options → cv4vs Agents →
General → **Claude executable path**, or the one it found by itself. Nothing else: the CLI decides
how to update the install it belongs to.

| Install | What `claude update` does |
|---|---|
| npm (`npm install -g @anthropic-ai/claude-code`) | installs the new package with `npm`, which has to be on the `PATH` Visual Studio was started with |
| native installer | downloads and installs the new version itself |
| winget, or another package manager | does **not** update: it tells you the command to run, such as `winget upgrade Anthropic.ClaudeCode`, and the extension shows it to you |

## What you are told

The outcome is decided by asking the CLI for its version again afterwards, not by whether the
command reported success: a CLI that is already current, and one owned by a package manager, both
finish "successfully" without changing anything.

| Outcome | What it means |
|---|---|
| `Claude Code updated to X. Sessions already open keep Y until they are restarted.` | the version changed |
| `Claude Code was not updated (still X).` | the CLI ran and the version is the same; its own text follows and says why |
| `Claude Code update finished, but its version could not be read.` | the CLI ran, and it did not answer `--version` afterwards |
| `Claude Code update is still running after 5 minutes. It was left to finish: check the version later.` | a slow download; the updater is not stopped half-way |
| `Claude Code update failed.` | the CLI reported an error; its own text follows, then ``Run `claude update` in a terminal to see the full output.`` In the chat the row has a **View logs** button that opens the Output window |

The text after the first sentence is the CLI's, unchanged. The message box shows up to 15 lines of it, one per row; the notice in the chat has room for one line, the cause of a failure or the
conclusion of a report.

## Sessions already open

They are left alone. On Windows a running program cannot be overwritten, so the updater sets the
old binary aside and writes the new one beside it: a pane that was open keeps working on the
version it started with.

A pane picks up the new version when its CLI process starts again: close the pane and open it
again, or open a new one.

Two things follow from that:

- **More → Info** on a pane reports the version of the binary on disk, which after an update is
  the new one, even for a pane still running the old.
- Each update leaves the previous binary behind (about 240 MB) for as long as a session is still
  using it.

## When it fails

- **`EBUSY: resource busy or locked`** (npm installs). A file from an earlier update is still in
  use by a session opened before it. Close the Claude Code sessions you no longer need, the oldest
  first, and update again. Nothing is damaged in the meantime: the install stays on the version it
  had.
- **`Unable to fetch latest version from npm registry`.** The CLI gives `npm` only a few
  seconds to answer. A machine that is busy, right after Visual Studio starts for instance, can
  miss that; trying again a little later is usually enough. Otherwise it is the network or a proxy.
- **Nothing happens, or the message is not enough.** Run `claude update` in a terminal: it prints
  everything. With the log level raised (Tools → Options → cv4vs Agents → Debug), the Output
  window pane **cv4vs Agents** records the run under the `[cli]` tag.

## Where "a newer version exists" comes from

The notice compares your version with the `latest` tag of the npm package, the same thing
`npm install -g` would give you. A native install set to follow the `stable` channel can therefore
be told that a newer version exists while its own updater says it is up to date: both are right,
they are looking at different channels.

The check runs each time a chat opens, until one of them has something to announce; from then on
that Visual Studio session says no more. It asks the installed CLI for its version and the npm
registry for the `latest` tag: the only network request the extension makes on its own. If the
registry does not answer within five seconds, there is simply no notice.
