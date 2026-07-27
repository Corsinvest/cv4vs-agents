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

The **extension is the anchor**: it is the only part of a reference that says "this is a file" (a
`:` is also a host:port and a clock, a `,` is also punctuation). The parser scans for `.ext`, then
grows outwards — backwards for the path, forwards for the line — and recognises every shape Claude
actually writes:

| You see | Opens |
|---|---|
| `ClientEvents.cs:208` | that file at line 208 |
| `Core/Stats/StatsService.cs:45` | relative path, at line 45 |
| `StatsService.cs:35–48` | a range — **selects lines 35 to 48** |
| `AgentsPackage.cs:124,185,202` | **each line is its own link** |
| `StatsService.cs:100-120,130` | a list whose elements may be ranges — each keeps its own span |
| `(NdjsonTransport.cs:91)` · `foo.cs(339)` · `bar.ts[45:12]` | parentheses/brackets, in prose |
| `ClaudeInstall.cs#L103` · `bar.ts#45` · `x.ts#L35-L48` | the GitHub form |
| `markdown.ts:57,file-links.ts:130` | two files in one run — **two separate links** |
| `[McpServerHost.cs:192](src/…/McpServerHost.cs:192)` | a markdown link — uses the full path |
| `file://C:\…\report.html` | absolute path (the `file://` is dropped from the label) |

The label always keeps what the model wrote, so a range stays readable as `100-120` instead of being
trimmed to its first line. A column (`x.ts:47:12`) is parsed and dropped — it never becomes the end
of a selection.

In prose, only real code/text extensions are linked — about **270** of them, covering .NET, C/C++,
the JS/TS ecosystem, JVM, scripting, systems and functional languages, shells (POSIX and PowerShell),
shaders, data/config formats and build files. That list is what keeps a clock (`10:30`), a host:port
(`localhost.net:4040`), a version (`2.1.220:5`) or a price (`19.99:2`) from becoming a dead link:
`.net` and `.com` are lexically identical to `.cs`, so only a list can tell them apart. If a language
you use is missing, add it under **Options → Chat → Extra linkable extensions** (one per line,
without the dot) — no need to wait for a release.

A **markdown link is different**: there the model already declared "this is a link", so any
extension works — `[label](src/utils/helper.rb#L12)` links even though `.rb` is not the point. What
still does not link is a target with no plausible file shape at all (`[x](19.99#L2)`): rather than
render a blue link that does nothing when clicked, it degrades to plain text.

Anything inside a fenced or inline **code block is left untouched** — a path in an example command
stays literal text.

## Where it opens

A click routes by what the reference is:

- an **`.html` report or a `file://` link** (e.g. the page `/insights` produces) opens in your
  **default browser** — you want it rendered, not its source in the editor;
- **everything else** (code and text) opens in **Visual Studio** at the line.

A reference that names a range (`StatsService.cs:35-48`, `x.ts#L35-L48`) opens the file and
**selects those lines**, so the block Claude is talking about is highlighted rather than just
scrolled to. A single line places the caret there. Selection follows *Options → Chat → Select lines
when opening file*; with it off, the file simply opens at the first line.

## How the file is found

For an editor open, the click carries the raw reference to Visual Studio, which resolves it against
your solution:

1. an **absolute path** that exists → opened directly;
2. a **path relative to the working directory** → resolved and opened;
3. a **bare file name** (`ClientEvents.cs`, no folders) → searched under the working directory
   (skipping `bin` / `obj` / `.git` / `node_modules`), first match wins.

Because Claude usually writes just the file name, step 3 is what makes the common case work — the
model means "the `ClientEvents.cs` in this project", and that is exactly what opens.

## Adding an extension

**Options → Chat → Extra linkable extensions** (`…` to edit, one per line, no leading dot).

Use it when Claude names a file in prose and it stays plain text — the language just isn't in the
built-in list yet. Entries are added on top of the built-ins, never replace them, so a later release
that widens the list still reaches you. The change applies to the open chat immediately; no restart.

```
zig
gleam
wgsl
```

This only affects **prose**. A markdown link written by the model is linked whatever its extension,
so there is nothing to configure for that case.

## Not in the other tools

**References in prose.** The Claude Code VS Code extension only turns *markdown links* into file
links — a bare `ClaudeInstall.cs:103` written in a sentence stays plain text there. Here it is a
link, which is the case the model produces most often.

**Multi-line lists.** `AgentsPackage.cs:124,185,202` renders as three separate, individually
clickable links to the same file. The other tools link the first location, if any, and leave the
rest as text.

**No dead links.** Elsewhere every markdown link is rendered blue whether or not the target is a
file, and the check happens on click — so `[x](19.99#L2)` looks like a link and then does nothing.
Here the check happens while rendering: if it cannot be a file, it stays text.
