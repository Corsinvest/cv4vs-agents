# MCP tools

The extension runs an in-process **[MCP](https://modelcontextprotocol.io/) server** that hands
Visual Studio's own understanding of your code to the agent: navigation, references, rename,
diagnostics, build and the live debugger. Not a text search over source files — the IDE's semantic,
running view of your program.

The 50 tools below are exposed automatically; there is nothing to configure. They are prefixed
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

## Document (5)

| Tool | What it does |
|---|---|
| `document_check_dirty` | Check whether an open file has unsaved changes. Returns isOpen=false when the file isn't open in any editor; otherwise isDirty true/false. |
| `document_format` | Format a file using the IDE's built-in formatter. Equivalent to Ctrl+K, Ctrl+D in Visual Studio. |
| `document_organize_imports` | Organize and remove unused using/import directives in a file via the IDE's Edit.RemoveAndSort command. |
| `document_run_cleanup` | Run the IDE's Code Cleanup on a file (Ctrl+K, Ctrl+E): formatting plus the fixers of the user's default cleanup profile. Richer than document_format, but the extra fixers are language-dependent (C#/VB get the most). |
| `document_save` | Save an open file if it has unsaved changes. Returns saved=true if a save happened, false if the file wasn't open or was already saved. |

## Build (3)

| Tool | What it does |
|---|---|
| `build_project` | Build a single project (by name) in the active configuration and return whether it succeeded plus the list of compiler errors. Blocks until done. |
| `build_set_startup_project` | Set the solution's startup project — the one debug_start (F5) launches. Pass the project name; returns ok plus the resolved startup project, or ok=false with the list of available projects if the name doesn't match. |
| `build_solution` | Build the entire solution and return whether it succeeded plus the list of compiler errors (file, line, description). Blocks until the build ends. |

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

## IDE (9)

| Tool | What it does |
|---|---|
| `ide_activate_output` | Bring a Visual Studio Output window pane (by name) to the foreground so the user sees it. Use at a debug checkpoint to show the relevant build/debug output before asking the user to confirm. The pane name is required. Returns ok; ok=false with availablePanes when the pane isn't found. |
| `ide_clear_output` | Clear a Visual Studio Output window pane (by name). Run it before an action so a later ide_read_output returns only the fresh output, not the old history. The pane name is required (no clear-all). Returns ok; ok=false with availablePanes when the pane isn't found. |
| `ide_execute_code` | Submit a code snippet to the IDE's interactive REPL (C# Interactive in Visual Studio). Returns whether the snippet was submitted; it does not capture the REPL's output. |
| `ide_get_diagnostics` | Get language diagnostics from the IDE's Error List. Pass uri (file://...) to limit to one file; omit it to get all. Returns an array of files, each with its diagnostics ([] when there are none). |
| `ide_get_edition` |  |
| `ide_get_project_structure` | Get the solution structure: each project with its name, path, and the files it contains. Recurses solution folders. Useful to learn the layout. |
| `ide_get_version` |  |
| `ide_get_workspace_folders` | Get the workspace folders currently open in the IDE. Returns the solution folder for Visual Studio. |
| `ide_read_output` | Read text from a Visual Studio Output window pane (e.g. 'Build', 'Debug', or the running program's output). Omit 'pane' to list the available pane names first. 'tailLines' caps how many lines are returned from the end (default 200). Useful to see build/debug output or the debuggee's console writes that don't go through the shell. |
