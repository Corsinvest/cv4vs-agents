<!--
SPDX-FileCopyrightText: Copyright Corsinvest Srl
SPDX-License-Identifier: GPL-3.0-only
-->

# Clickable file references

When Claude mentions a source location in its answer — `ClientEvents.cs:208`,
`Core/Stats/StatsService.cs:45`, a whole list like `McpServerHost.cs:192,214,230` — the extension
turns it into a **link**. Click it and the file opens in Visual Studio at that line, in your own
editor, with your own extensions and theme.

No copy-pasting a path into Go-To-File, no hunting for the line. The reference the model already
wrote *is* the navigation.

## What gets linked

The parser follows the same two-phase approach Visual Studio's terminal uses (peel the `:line`
suffix off the end, validate the path in front) and recognises every shape Claude actually writes:

| You see | Opens |
|---|---|
| `ClientEvents.cs:208` | that file at line 208 |
| `Core/Stats/StatsService.cs:45` | relative path, at line 45 |
| `StatsService.cs:35–48` | a range — opens at the first line |
| `AgentsPackage.cs:124,185,202` | **each line is its own link** |
| `(NdjsonTransport.cs:91)` · `foo.cs(339)` · `bar.ts[45:12]` | parentheses/brackets, in prose |
| `[McpServerHost.cs:192](src/…/McpServerHost.cs:192)` | a markdown link — uses the full path |
| `file://C:\…\report.html` | absolute path (the `file://` is dropped from the label) |

Only real code/text extensions are linked (`.cs`, `.ts`, `.css`, `.xaml`, `.json`, `.md`, `.cpp`,
`.py`, …), so a clock (`10:30`), a host:port (`localhost:4040`) or a bare file name mid-sentence
never becomes a dead link. Anything inside a fenced or inline **code block is left untouched** — a
path in an example command stays literal text.

## Where it opens

A click routes by what the reference is:

- an **`.html` report or a `file://` link** (e.g. the page `/insights` produces) opens in your
  **default browser** — you want it rendered, not its source in the editor;
- **everything else** (code and text) opens in **Visual Studio** at the line.

## How the file is found

For an editor open, the click carries the raw reference to Visual Studio, which resolves it against
your solution:

1. an **absolute path** that exists → opened directly;
2. a **path relative to the working directory** → resolved and opened;
3. a **bare file name** (`ClientEvents.cs`, no folders) → searched under the working directory
   (skipping `bin` / `obj` / `.git` / `node_modules`), first match wins.

Because Claude usually writes just the file name, step 3 is what makes the common case work — the
model means "the `ClientEvents.cs` in this project", and that is exactly what opens.

## Not in the other tools

The multi-line list — `AgentsPackage.cs:124,185,202` rendered as three separate, individually
clickable links to the same file — is something neither the Claude Code VS Code extension nor Claude
Desktop do: they link the first location and leave the rest as text. Here every line in the list is
its own jump.
