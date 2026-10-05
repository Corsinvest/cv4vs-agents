---
title: Teach the agent about the IDE
description: A few lines of CLAUDE.md that make the agent reach for Visual Studio instead of the shell.
---

The tools announce themselves: the agent sees the list without being told. Seeing a name in a
list of fifty is not the same as thinking of it, though, and the habits it brings are the ones
of a terminal: for a build it reaches for `msbuild`, for an error it re-reads the source, for
the callers of a method it greps.

What is worth telling it once is the thing all fifty have in common: **there is a live Visual
Studio behind this session, and it already understands the code.** It has compiled the solution,
it holds the semantic model, it knows where a symbol is used and what the compiler thinks. From
that one fact the rest follows on its own: that a build should go through the IDE, that the
Error List answers faster than re-reading a file, that references come from the language service
rather than a text search.

A build is the clearest example. The agent knows `msbuild` and `dotnet build`, so that is what it
runs: it guesses at the MSBuild path, and if you are mid-F5 the build fails on a locked assembly
in a way that reads like a code error. `build_solution` drives the Visual Studio you already have
open, so there is no path to guess and no conflict with a running session, and the errors come
back as file, line and message rather than as text to be scraped. None of that is inferable from
the tool's description.

## A few lines of CLAUDE.md

That is what a `CLAUDE.md` in your own repository is for. A few lines are enough: with the
`mcp__vs__` prefix, which is how the agent sees the names:

```markdown
## Visual Studio
This solution is open in a Visual Studio you can talk to through the `mcp__vs__*` tools: it has
built the code and holds its semantic model. Prefer asking it over reading files or shelling out:
diagnostics, references and definitions come from the language service, not from a text search.

## Build
Build with `mcp__vs__build_solution` (or `build_project` for one project), not msbuild or
dotnet build from the shell. It uses the open IDE, so no path to resolve and no clash with a
debug session, and it returns structured errors.

## Tests
Run tests with `mcp__vs__test_run`, not dotnet test from the shell. It goes through the Test
Explorer, so it uses the build the IDE already has and the configuration it is actually on, and
`mcp__vs__test_get_results` gives the failures with their message and stack rather than console
text to scrape. Works for C++ and every other framework the IDE supports, not only .NET.

## Debugging
Do not call `mcp__vs__debug_start` / `debug_stop` without asking: they take over the IDE.
`mcp__vs__debug_set_next_statement` skips code rather than running it, so ask before that one too.
After editing during a session, `mcp__vs__debug_apply_hot_reload` applies the change without
a restart.
```

## Worth writing down

- **That the IDE is there at all**, and that it already knows the code. One line, and the most
  useful of the lot: the specific rules below are consequences of it.
- **Which tool wins over the obvious shell command**, and why: build, output reading, diagnostics.
- **What needs asking first.** Anything that takes over the IDE or is slow to undo: starting and
  stopping the debugger, `document_run_cleanup`, `nav_rename_symbol` across a solution.
- **Project-specific gotchas.** A startup project that must be set before F5; a pane whose name
  the agent would not guess; a build configuration that is the only supported one.

## Leave out the tool list

Leave out the tool list itself. It arrives with the extension, it changes as the extension is
updated, and a copy in your repository is one more thing to keep true.

Every tool is listed in [MCP tools](/cv4vs-agents/mcp-tools/).
