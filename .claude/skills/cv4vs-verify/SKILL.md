---
name: cv4vs-verify
description: Prepare and guide the manual check of this extension in the Visual Studio experimental instance — confirm what is installed, order the checks by risk, and read the log the user pastes back. Use when asked to verify, test or try the extension in VS, or after installing a build. Cannot see the tool windows itself.
---

# Verify a build in the Exp instance

The gate for this project is a green MSBuild plus a manual pass in the experimental instance. There
is no test project, and `.vsct` ids, XAML `x:Class` and the manifest all fail at *runtime*, not at
compile time.

**This skill cannot run the test.** The panes live inside Visual Studio and nothing outside the
process can see them — same limitation `cv4vs-run` states. What it does is the part around the test:
confirm the right build is installed before, and read the log after.

## Before: is the right thing installed?

```powershell
.\tools\extension.ps1
```

Prints, per hive: instance name, version, and path. What to look for:

- **One copy per hive.** More than one and VS loads both — duplicate menu entries and two MCP
  servers racing for the same lock file, which reads as a bug in the code. The script flags this in
  red; `-Reinstall` collapses them.
- **The expected hive.** Experimental unless there is a reason otherwise. `-Normal` shows the other
  side.
- **The build under test.** The status output has no date, because VSIXInstaller restamps extracted
  files with the install time. The packaging date is printed when installing, not afterwards — so
  if there is doubt about which build is on disk, reinstall rather than guess.

Then start it:

```powershell
devenv /rootsuffix Exp
```

First launch after an install is slow: VS rebuilds the MEF cache.

## The check, by risk

Not in menu order — in the order things actually break:

1. **Chat pane.** Opens without an exception, WebView renders, the welcome logo is there. This is
   the pane that breaks: everything about packaging shows up here first.
2. **A prompt round-trip.** The CLI starts, the MCP server comes up, a reply arrives.
3. **CLI pane.** The other pane type, a separate startup path (ConPTY + `--ide`).
4. **Options**, all four pages: General, Chat, Profiles, Debug.
5. **Menu entries and icons.** A mismatched `.vsct` id gives a silent no-op — the entry is there and
   does nothing.
6. **History.** Sessions listed and openable.

## After: the log

The log goes to the **Output window** (pane "cv4vs Agents"), held in memory by
`IVsOutputWindowPane`. It is not written to disk — there is no file to read, so it has to be copied
out of the window and pasted back.

Two things to say up front, because both cost a round trip when forgotten:

- **The default log level is `None`.** Without raising it in Options → Debug there is nothing to
  copy. Ask the user to set it before reproducing, not after.
- **Reproduce first, copy second.** The pane is not cleared between runs, but the interesting lines
  are the ones after the action.

Reading it: `[client]`, `[mcp]`, `[cli]`, `[sessions]` and similar tags mark the area. `PERF` lines
give timings. An exception with no context above it usually means the failure happened before
logging was set up.

## When something is wrong

Get the log before proposing a cause. On 2026-07-21 the `DirectoryNotFoundException` looked like a
code defect and was a packaging one — the diagnosis came from the log, which named the path being
resolved.

If the fault is in the package rather than the code, [cv4vs-ci-log](../cv4vs-ci-log/SKILL.md) reads
the run that built it and [cv4vs-ci-install](../cv4vs-ci-install/SKILL.md) replaces it.
