# MCP tools

The extension runs an in-process **[MCP](https://modelcontextprotocol.io/) server** that hands
Visual Studio's own understanding of your code to the agent: navigation, references, rename,
diagnostics, build and the live debugger. Not a text search over source files — the IDE's semantic,
running view of your program.

The 51 tools below are exposed automatically; there is nothing to configure. They are prefixed
`mcp__vs__` on the wire, and appear in the CLI's `/mcp` listing.

**Language-agnostic by design.** Tools are wired through Roslyn's per-document language services
(via reflection on the assemblies VS has already loaded) or language-agnostic APIs (`EnvDTE`, VS
commands) — never a C#/VB-only path. There is no list of supported languages here: whatever your
Visual Studio can do, the agent can ask for.

That cuts both ways. A tool is only as capable as the installed workloads and the language service
behind the file: `nav_find_references` returns what *your* VS would return on that file — rich for
a language with a full language service, thinner for one without. `debug_*` needs the workload that
debugs that project type. Where a capability genuinely isn't there, the tool feature-detects and
returns `supported=false` instead of pretending it worked.

**Naming.** `domain_verb[_object]`, snake_case, domain first — `nav_go_to_definition`,
`debug_get_locals`. A domain exists once it has three or more tools; the rest live under `ide`.

## Navigation (6)

| Tool | What it does |
|---|---|
| `nav_find_references` | Find all references to a symbol across the solution (semantic, not text search): give the file, the 1-based line where the symbol appears, and the symbol name. Returns each reference's file/line (usages only — the symbol's own definition is excluded; use nav_go_to_definition for that). The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading — retry shortly before using grep. |
| `nav_get_document_symbols` | List a file's symbols as a tree — each with its name, kind (Class/Method/Property/…) and 1-based line, ordered top-to-bottom — the editor's navigation outline. Useful to locate members in a large file without reading it all. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading — retry shortly. |
| `nav_go_to_definition` | Find where a symbol is defined (semantic, not text search): give the file, the 1-based line where the symbol is used, and the symbol name. Returns the defining file/line. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading — safe to retry shortly before falling back to grep. |
| `nav_go_to_implementation` | Find the implementations of a symbol (semantic): for an interface or an interface member, the concrete classes/members that implement it; for a virtual/abstract member, the overrides. Give the file, the 1-based line where the symbol appears, and the symbol name. Use this — not nav_find_references — to see the actual code behind an interface. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading. |
| `nav_rename_symbol` | Rename a symbol everywhere it's used across the solution (semantic, not text replace): give the file, the 1-based line where the symbol appears, its current name, and the new name. Updates the definition and all references and writes the changes directly. Atomic — if the rename would cause unresolved conflicts nothing is applied. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for; applied=false (with a reason) when the symbol can't be renamed or the new name is invalid. |
| `nav_search_workspace_symbols` | Find a symbol by name across the entire solution (the 'Navigate To' search). Returns up to 50 hits, each with name, kind, file and 1-based line, ordered by file then line. Use it to locate a class/method without knowing its file. Returns supported=false for languages without NavigateTo support — fall back to Grep then. |

## Editor (7)

| Tool | What it does |
|---|---|
| `editor_close_all_diffs` | Close all diff/compare windows currently open in the IDE. |
| `editor_close_tab` | Close a tab in the IDE by its caption. |
| `editor_get_latest_selection` | Get the most recent non-empty selection from any editor, even if focus has moved away. Returns null if no selection has been made. |
| `editor_get_open_files` | List files currently open in the IDE's editor tabs, with active/dirty flags and language id. |
| `editor_get_selection` | Get the current text selection in the active editor. Returns the selected text and its range, or null if no editor is active. |
| `editor_open_diff` | Open a side-by-side diff between an existing file and proposed new content. |
| `editor_open_file` | Open a file in the editor. Optionally select whole lines with startLine/endLine (1-based). Set activate to focus the tab. |

## Document (6)

| Tool | What it does |
|---|---|
| `document_check_dirty` | Check whether an open file has unsaved changes. Returns isOpen=false when the file isn't open in any editor; otherwise isDirty true/false. |
| `document_format` | Format a file using the IDE's built-in formatter. Equivalent to Ctrl+K, Ctrl+D in Visual Studio. |
| `document_organize_imports` | Organize and remove unused using/import directives in a file via the IDE's Edit.RemoveAndSort command. |
| `document_read_buffer` | Read an open document's editor buffer, including changes the user hasn't saved. Omit filePath to read the document they are currently looking at. Use the Read tool instead for the version on disk, or when the file isn't open in the IDE. Returns isDirty so you can tell whether what you read differs from disk. |
| `document_run_cleanup` | Run the IDE's Code Cleanup on a file (Ctrl+K, Ctrl+E): formatting plus the fixers of the user's default cleanup profile. Richer than document_format, but the extra fixers are language-dependent (C#/VB get the most). |
| `document_save` | Save an open file if it has unsaved changes. Returns saved=true if a save happened, false if the file wasn't open or was already saved. |

## Build (4)

| Tool | What it does |
|---|---|
| `build_clean` | Clean the entire solution: delete the build outputs (bin/obj) of every project. Blocks until the clean ends. Use it when a build result looks stale, then call build_solution to rebuild — cleaning on its own produces no diagnostics. |
| `build_project` | Build a single project (by name) in the active configuration and return whether it succeeded plus what the Error List holds (file, line, description, severity). Blocks until done. Reports errors only unless severity says otherwise; the message says how many items were left out. |
| `build_set_startup_project` | Set the solution's startup project — the one debug_start (F5) launches. Pass the project name; returns ok plus the resolved startup project, or ok=false with the list of available projects if the name doesn't match. |
| `build_solution` | Build the entire solution and return whether it succeeded plus what the Error List holds (file, line, description, severity). Blocks until the build ends. Reports errors only unless severity says otherwise; the message says how many items were left out. |

## Debug (20)

| Tool | What it does |
|---|---|
| `debug_apply_hot_reload` | Apply your pending code edits to the running program WITHOUT restarting it (Hot Reload / Edit-and-Continue). Use after editing a file during a debug session to see the change take effect live. Needs an active debug session. Some edits (changing a method signature, adding types, etc.) can't be hot-reloaded and require a restart — check ide_read_output for warnings. Differs from debug_evaluate, which changes values, not code. |
| `debug_attach` | Attach the debugger to an already-running local process, by pid (preferred) or by a unique name substring. Use this instead of debug_start when the app is already running (web server, service, console). After attaching, the session is running — use debug_break or set a breakpoint to pause it, then inspect. Find the pid with debug_list_processes. |
| `debug_break` | Pause the running program immediately (Debug > Break All), without waiting for a breakpoint. Only valid while running. After this the debugger is in 'break' mode, so you can inspect the call stack and variables. |
| `debug_clear_breakpoints` | Remove all breakpoints in the solution. |
| `debug_continue` | Resume execution from a paused (break) state (like F5 while paused). The program runs until the next breakpoint or it exits. Only valid in break mode. |
| `debug_evaluate` | Evaluate an expression in the current stack frame while paused (break mode), like the Watch window: pass something like 'order.Items.Count'. Returns the value and type. Note: evaluating can call property getters/methods in the program, so it may have side-effects — prefer reading fields/properties. You can also assign (e.g. 'x = 5') to change a variable's value while paused. Only valid in break mode. |
| `debug_get_callstack` | Get the call stack of the current thread while paused (break mode): each frame's function, module, and (for the top frame) file/line. Only valid in break mode — if the program is still running, poll debug_get_state until mode='break'. |
| `debug_get_locals` |  |
| `debug_get_state` | Get the current debug state: mode is 'design' (not debugging), 'run' (running), or 'break' (paused on a breakpoint/exception). In 'break' mode also returns the current file and 1-based line where execution is paused, and — if paused ON AN EXCEPTION — its type and message. Poll this after debug_start to know when the program has hit a breakpoint or thrown. |
| `debug_list_breakpoints` | List all breakpoints in the solution: each with its file+line (or function name), condition (if any), and whether it's enabled. |
| `debug_list_processes` | List local processes the debugger can attach to (pid + name). Optionally filter by a name substring. Use this to find the process to pass to debug_attach. |
| `debug_remove_breakpoint` | Remove the breakpoint(s) at a file and 1-based line. Use debug_clear_breakpoints to remove all. |
| `debug_restart` | Restart the current debug session (stop, then start again — like Debug > Restart). If not debugging, just starts. |
| `debug_set_breakpoint` | Add a breakpoint at a file and 1-based line. Optionally pass a condition (an expression that must be true for the breakpoint to trigger). Works whether or not a debug session is running. Combine with debug_start + debug_get_state to pause execution at this point. |
| `debug_set_exception_breakpoint` | Configure the debugger to break when a specific exception type is thrown (first-chance), even if it's caught — useful to find where an exception originates. Pass the fully-qualified type (e.g. 'System.NullReferenceException'). breakWhenThrown=false turns it off. Works in any mode; needs a solution loaded. After it breaks, debug_get_state reports the exception type/message. |
| `debug_set_function_breakpoint` |  |
| `debug_start` | Start debugging the solution's startup project (equivalent to F5). Non-blocking: returns once launched; the program then runs until it hits a breakpoint or exits. Poll debug_get_state to detect when it pauses (mode='break'). No-op if already debugging. |
| `debug_start_no_debugger` | Start the program WITHOUT the debugger (equivalent to Ctrl+F5). Optionally pass a project name to set it as startup first. Use debug_start instead when you need breakpoints. Returns ok or ok=false with a reason. |
| `debug_step` | Step the paused program by one statement. Direction: 'over' (run the line without entering called methods — default), 'into' (step into the call), 'out' (run to the end of the current method). Returns the new file/line. Only valid in break mode. |
| `debug_stop` | Stop the current debug session (equivalent to Shift+F5). No-op if not debugging. |

## IDE (8)

| Tool | What it does |
|---|---|
| `ide_activate_output` | Bring a Visual Studio Output window pane (by name) to the foreground so the user sees it. Use at a debug checkpoint to show the relevant build/debug output before asking the user to confirm. The pane name is required. Returns ok; ok=false with availablePanes when the pane isn't found. |
| `ide_clear_output` | Clear a Visual Studio Output window pane (by name). Run it before an action so a later ide_read_output returns only the fresh output, not the old history. The pane name is required (no clear-all). Returns ok; ok=false with availablePanes when the pane isn't found. |
| `ide_get_diagnostics` | Get language diagnostics from the IDE's Error List. Pass uri (file://...) to limit to one file; omit it to get all. Pass severity ('Error'/'Warning'/'Info') and/or maxResults to avoid pulling in hundreds of warnings when you only care about the errors. Returns an array of files, each with its diagnostics ([] when there are none). |
| `ide_get_edition` |  |
| `ide_get_project_structure` | Get the solution structure: each project with its name, path, and the files it contains. Recurses solution folders. Useful to learn the layout. |
| `ide_get_version` |  |
| `ide_get_workspace_folders` | Get the workspace folders currently open in the IDE. Returns the solution folder for Visual Studio. |
| `ide_read_output` | Read text from a Visual Studio Output window pane (e.g. 'Build', 'Debug', or the running program's output). Omit 'pane' to list the available pane names first. 'tailLines' caps how many lines are returned from the end (default 200). Useful to see build/debug output or the debuggee's console writes that don't go through the shell. |

## Telling the agent when to reach for them

The tools announce themselves — the agent sees the list without being told. Seeing a name in a
list of fifty is not the same as thinking of it, though, and the habits it brings are the ones
of a terminal: for a build it reaches for `msbuild`, for an error it re-reads the source, for
the callers of a method it greps.

What is worth telling it once is the thing all fifty have in common: **there is a live Visual
Studio behind this session, and it already understands the code.** It has compiled the solution,
it holds the semantic model, it knows where a symbol is used and what the compiler thinks. From
that one fact the rest follows on its own — that a build should go through the IDE, that the
Error List answers faster than re-reading a file, that references come from the language service
rather than a text search.

A build is the clearest example. The agent knows `msbuild` and `dotnet build`, so that is what it
runs: it guesses at the MSBuild path, and if you are mid-F5 the build fails on a locked assembly
in a way that reads like a code error. `build_solution` drives the Visual Studio you already have
open, so there is no path to guess and no conflict with a running session — and the errors come
back as file, line and message rather than as text to be scraped. None of that is inferable from
the tool's description.

That is what a `CLAUDE.md` in your own repository is for. A few lines are enough — with the
`mcp__vs__` prefix, which is how the agent sees the names:

```markdown
## Visual Studio
This solution is open in a Visual Studio you can talk to through the `mcp__vs__*` tools: it has
built the code and holds its semantic model. Prefer asking it over reading files or shelling out —
diagnostics, references and definitions come from the language service, not from a text search.

## Build
Build with `mcp__vs__build_solution` (or `build_project` for one project) — not msbuild or
dotnet build from the shell. It uses the open IDE, so no path to resolve and no clash with a
debug session, and it returns structured errors.

## Debugging
Do not call `mcp__vs__debug_start` / `debug_stop` without asking: they take over the IDE.
After editing during a session, `mcp__vs__debug_apply_hot_reload` applies the change without
a restart.
```

Worth writing down, in general:

- **That the IDE is there at all**, and that it already knows the code. One line, and the most
  useful of the lot: the specific rules below are consequences of it.
- **Which tool wins over the obvious shell command**, and why — build, output reading, diagnostics.
- **What needs asking first.** Anything that takes over the IDE or is slow to undo: starting and
  stopping the debugger, `document_run_cleanup`, `nav_rename_symbol` across a solution.
- **Project-specific gotchas.** A startup project that must be set before F5; a pane whose name
  the agent would not guess; a build configuration that is the only supported one.

Leave out the tool list itself. It arrives with the extension, it changes as the extension is
updated, and a copy in your repository is one more thing to keep true.
