# MCP tools

The extension runs an in-process **[MCP](https://modelcontextprotocol.io/) server** that hands
Visual Studio's own understanding of your code to the agent: navigation, references, rename,
diagnostics, build and the live debugger. Not a text search over source files — the IDE's semantic,
running view of your program.

The 70+ tools below are exposed automatically; there is nothing to configure. They are prefixed
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

**Each tool says what it costs you.** Every tool carries the standard MCP annotations —
`readOnlyHint` when it changes nothing, `destructiveHint` when it can destroy or interrupt
something you'd miss, `idempotentHint` when running it twice is the same as running it once. The
CLI uses them to decide what needs your approval, so reading a symbol's references doesn't cost the
same prompt as stopping a debug session. They are hints, not enforcement: the client still decides.

The reading tools (`nav_*` except rename, every `ide_get_*` and `debug_get_*`/`debug_list_*`) are
read-only. Destructive covers what you'd want to be asked about: `debug_stop`, `build_clean`,
`build_cancel`, `nav_rename_symbol`, `document_run_cleanup`, and the breakpoint-removing pair. The
stepping tools carry nothing — they move the program forward, which is neither safe to repeat nor
destructive. `debug_evaluate` is deliberately not read-only: evaluating can call property getters,
and it accepts assignments.

**A long catalogue costs nothing per turn.** Four of these tools sit in the model's prompt at all
times — `editor_get_selection`, `editor_get_latest_selection`, `editor_get_open_files` and
`ide_get_diagnostics`. The rest are deferred: the CLI keeps their names to hand and loads a tool's
full definition when it goes looking for one. So the number of tools here is not a running cost,
and adding one does not make the model slower.

The four are the ones that answer *what is the user looking at right now* — context worth having
before deciding what to do, rather than in response to deciding. Everything else is something you
reach for once you know what you want, which is exactly when a search for it is free. A tool marked
always-loaded costs roughly fifty tokens of context on every single turn, so the bar for adding a
fifth is high.

**Naming.** `domain_verb[_object]`, snake_case, domain first — `nav_go_to_definition`,
`debug_get_locals`. A domain exists once it has three or more tools; the rest live under `ide`.

## Navigation

| Tool | What it does |
|---|---|
| `nav_find_references` | Find all references to a symbol across the solution (semantic, not text search): give the file, the 1-based line where the symbol appears, and the symbol name. Returns each reference's file/line (usages only — the symbol's own definition is excluded; use nav_go_to_definition for that). The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading — retry shortly before using grep. |
| `nav_get_document_symbols` | List a file's symbols as a tree — each with its name, kind (Class/Method/Property/…) and 1-based line, ordered top-to-bottom — the editor's navigation outline. Useful to locate members in a large file without reading it all. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading — retry shortly. This is one file; nav_search_workspace_symbols finds a name across the whole solution. |
| `nav_go_to_definition` | Find where a symbol is defined (semantic, not text search): give the file, the 1-based line where the symbol is used, and the symbol name. Returns the defining file/line. Reaches definitions in referenced assemblies too: with no source on disk, the declaration is generated under %TEMP% and the hit carries source='decompiled' (rebuilt from IL — locals renamed) or source='source' (the real thing, via SourceLink). Those files are generated and read-only: read them, never edit them. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading — safe to retry shortly before falling back to grep. nav_find_references goes the other way, from a definition to its callers, and nav_go_to_implementation past an interface to what implements it. |
| `nav_go_to_implementation` | Find the implementations of a symbol (semantic): for an interface or an interface member, the concrete classes/members that implement it; for a virtual/abstract member, the overrides. Give the file, the 1-based line where the symbol appears, and the symbol name. Use this — not nav_find_references — to see the actual code behind an interface. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for, or transiently while the solution is still loading. |
| `nav_rename_symbol` | Rename a symbol everywhere it's used across the solution (semantic, not text replace): give the file, the 1-based line where the symbol appears, its current name, and the new name. Updates the definition and every reference. Atomic — if the rename would cause unresolved conflicts nothing is applied. Writes every file it touches to disk immediately — files already open in the editor get a dirty buffer instead, but nothing is held back waiting for document_save. There is no Ctrl+Z for it: undoing means renaming back. A build, a git diff or a shell command sees the new name straight away. It reaches further than the file you name — a solution-wide rename touched four files in a two-project solution — so read changedFiles (path plus how many occurrences each, sorted by path) and totalOccurrences for what actually changed, rather than assuming it was the one file. The file must belong to a project in the open solution. Returns supported=false for languages this isn't available for; applied=false (with a reason) when the symbol can't be renamed or the new name is invalid. It changes every file it touches, so ask before running it — nav_find_references shows the same set without changing anything. |
| `nav_search_workspace_symbols` | Find a symbol by name across the entire solution (the 'Navigate To' search). Matches declarations, not text, so a hit is a real class/method/field and comes with its kind, its container and the declaration line itself. Returns up to 50 hits, each with name, kind, file, 1-based line, container_name and preview, ordered by file then line. Returns supported=false where no project provides NavigateTo — fall back to Grep then, which searches text and will also match usages and comments. This is the way in when the file is unknown: from a hit, nav_go_to_definition, nav_find_references and nav_get_document_symbols all take the file and line it returns. |

## Editor

| Tool | What it does |
|---|---|
| `editor_close_all_diffs` | Close every diff window this server opened, and only those: a window of the user's own is left alone even if its caption says Diff. Returns how many were closed. Use it to tidy up after a series of editor_open_diff calls; editor_close_tab closes one by name. |
| `editor_close_tab` | Close a diff tab this server opened, by the tabName that editor_open_diff was given. It does NOT close arbitrary editor tabs: only frames in our own diff registry are touched, so the user's documents are safe from it — and closing something they opened is not on offer. Finding no such tab is not an error. Use editor_close_all_diffs to clear them all. |
| `editor_get_latest_selection` | Get the most recent non-empty selection from any editor, even after focus has moved away — the one to use when the user selected something and then came to the chat. It is remembered by editor_get_selection, so it stays null until that has been called at least once in this session; a fresh session sees nothing here even if text is selected on screen. |
| `editor_get_open_files` | List the files currently open in the IDE's editor tabs, with active/dirty flags and language id, sorted by path. dirty means the buffer differs from disk — read that one with document_read_buffer rather than from the file, and document_save writes it out. |
| `editor_get_selection` | Get the current text selection in the active editor: the selected text and its range, or null when no editor has focus — which includes right after the user clicks elsewhere, since the selection belongs to the focused window. Calling this also remembers the answer for editor_get_latest_selection; reach for that one instead when the user may have moved on since selecting. |
| `editor_open_diff` | Open a side-by-side diff between an existing file and proposed new content, so the user can see an edit before it happens. The tabName given here is what editor_close_tab takes to close it again, and editor_close_all_diffs clears every one this server opened. |
| `editor_open_file` | Open a file in the editor. Optionally select whole lines with startLine/endLine (1-based). Set activate to focus the tab. This is for showing the user something — reading a file needs no editor: use the Read tool, or document_read_buffer when the file is open and may hold unsaved changes. |

## Document

| Tool | What it does |
|---|---|
| `document_check_dirty` | Check whether an open file has unsaved changes. Returns isOpen=false when the file isn't open in any editor; otherwise isDirty true/false. When it is dirty the Read tool gives the version on disk and document_read_buffer the one on screen — they differ, and document_save makes them agree. |
| `document_format` | Format a file using the IDE's built-in formatter. Equivalent to Ctrl+K, Ctrl+D in Visual Studio. The file must live inside the open solution's folder; success=false otherwise. Opens the file in the editor if it isn't already — the formatter needs a live document — and changes that buffer without saving. Reading it back with the Read tool saves it first when autosave is on (the default); document_read_buffer looks without writing. document_run_cleanup does this plus the user's own cleanup fixers. |
| `document_organize_imports` | Organize and remove unused using/import directives in a file via the IDE's Edit.RemoveAndSort command. The file must live inside the open solution's folder; success=false otherwise. Opens the file in the editor if it isn't already, and leaves that buffer unsaved like document_format — including that reading it back with the Read tool saves it first when autosave is on; document_read_buffer looks without writing. |
| `document_read_buffer` | Read an open document's editor buffer, including changes the user hasn't saved. Omit filePath to read the document they are currently looking at. Use the Read tool instead for the version on disk, or when the file isn't open in the IDE. Returns isDirty so you can tell whether what you read differs from disk. |
| `document_run_cleanup` | Run the IDE's Code Cleanup on a file (Ctrl+K, Ctrl+E): formatting plus the fixers of the user's default cleanup profile. Richer than document_format, but the extra fixers are language-dependent (C#/VB get the most). The file must live inside the open solution's folder; success=false otherwise. Opens the file in the editor if it isn't already, and leaves that buffer unsaved — though reading it back with the Read tool saves it first when autosave is on; document_read_buffer looks without writing. Some installations refuse the command outright: success=false then carries the IDE's own message, and document_format is the part of it that always works. |
| `document_save` | Save an open file if it has unsaved changes. Returns saved=true if a save happened, false if the file wasn't open or was already saved — the two are not told apart here, document_check_dirty separates them beforehand. Needed after document_format, document_organize_imports or document_run_cleanup, which change buffers and leave them unsaved. nav_rename_symbol does not need it — it writes to disk itself. |

### Where an edit lands: the buffer or the file

`document_format`, `document_organize_imports` and `document_run_cleanup` open the file they are
given and leave it dirty. The tab is not incidental — the IDE's formatter and cleanup act on a live
document rather than on a path — so the edit sits in the editor, invisible to a build, a `git diff`
or a shell command until `document_save`. `document_read_buffer` is what sees it meanwhile.

**Autosave cuts that short, by design.** With Options → Chat → Autosave on (the default) a
`PreToolUse` hook saves a dirty file before Claude's own `Read`, `Edit` or `Write` touches it, so
the agent never reads a stale copy of something the user is looking at. The cost is that reading
back what one of those three just changed *writes it out first*: the buffer goes clean and the next
build sees it. `document_read_buffer` avoids that — it is an MCP tool, and the hook only matches
Claude's file tools.

`nav_rename_symbol` is the exception: it writes to disk. Files that happen to be open go dirty
instead, but nothing waits for `document_save`, and there is no Ctrl+Z — undoing means renaming
back. Opening every touched file first was tried and dropped: it does buy dirty buffers, but at a
focused tab per file (four on a small solution-wide rename), and the undo is lost anyway the moment
anything reads one of them back. One predictable outcome beats two that depend on which tabs the
user left open.

## Build

| Tool | What it does |
|---|---|
| `build_cancel` | Stop the build currently running in the IDE and wait for it to actually stop. Reports ok=true once the IDE is free again — including when nothing was running, since that is the state you asked for; ok=false means the build is still going and the message says why. Use it when build_solution or build_clean reported a timeout (the build was left running), or when a build started outside the chat is in the way. A cancelled build leaves partial outputs, so run build_clean before trusting the next one. |
| `build_clean` | Clean the entire solution: delete the build outputs (bin/obj) of every project. Blocks until the clean ends. Use it when a build result looks stale, then call build_solution to rebuild — cleaning on its own produces no diagnostics. |
| `build_project` | Build a single project (by name) in the active configuration and return whether it succeeded plus what the Error List holds (file, line, description, severity). Blocks until done. Reports errors only unless severity says otherwise; the message says how many items were left out, and 'configuration' says which one it built — solution_set_configuration changes it. Trust ok/failedProjects/message rather than the length of 'errors': the Error List is filled asynchronously, so a failed build can answer before its errors have landed; ide_read_output('Build') has the compiler's own log. The name is a project name, not a path — ide_get_project_structure lists them. build_solution builds everything instead. |
| `build_solution` | Build the entire solution and return whether it succeeded plus what the Error List holds (file, line, description, severity). Blocks until the build ends. Reports errors only unless severity says otherwise; the message says how many items were left out. Prefer this to a dotnet build in the shell: it goes through the open IDE, so there is no path to resolve and no clash with a debug session. Builds whichever configuration the IDE has active and reports it back as 'configuration' — solution_set_configuration changes it. Trust ok/failedProjects/message for the outcome rather than the length of 'errors': the Error List is filled asynchronously, so a failed build can answer before its errors have landed, and an entry left by an earlier build or a debug session can outlive a build that succeeded. ide_read_output('Build') has the compiler's own log when the list disagrees. build_project builds one project instead. |

## Solution

| Tool | What it does |
|---|---|
| `solution_add_project` | Add an existing project file to the open solution, as Solution Explorer's 'Add → Existing Project' does, and save the solution. The project file must already exist — this does not scaffold one. Returns the resolved project name. solution_remove_project is the reverse; project_add_file adds a file to a project that is already in the solution. |
| `solution_get_configuration` | Get the solution's active configuration — the one build_solution and build_project compile — plus every configuration that can be asked for ('Debug\|Any CPU', 'Release\|Any CPU', …). Takes no arguments and changes nothing. Use it to check what a build will produce, or to see the valid names before solution_set_configuration, which is what actually switches it. |
| `solution_remove_project` | Remove a project from the solution and save it. The project's files stay on disk — this takes it out of the solution, it does not delete it. Returns ok=false with the available project names when the name doesn't match. The reverse of solution_add_project. |
| `solution_set_configuration` | Switch the solution's active configuration (Debug, Release, …) — the one build_solution and build_project compile and debug_start launches. Pass 'Debug' or 'Release', or the full 'Release\|Any CPU' when a name has several platforms; returns ok plus the resolved configuration, or ok=false with the available ones if the name doesn't match. This is a change to the user's IDE and it persists: the toolbar dropdown moves and their next manual build follows it, so switch only when asked, and say so. solution_get_configuration reads the current one, and the valid names, without changing anything. |
| `solution_set_startup_project` | Set the solution's startup project — the one debug_start (F5) launches. Pass the project name; returns ok plus the resolved startup project, or ok=false with the list of available projects if the name doesn't match. |

## Project

| Tool | What it does |
|---|---|
| `project_add_file` | Add a file that already exists on disk to a project, as Solution Explorer's 'Add → Existing Item' does. Needed for project types that list every file explicitly: there, a .cs written to disk compiles in the IDE but is missing from an MSBuild command-line build, which fails silently. SDK-style projects glob their files in and need no call — one made anyway reports that the file is already included. This does not create the file: write it first, then add it. project_remove_file is the reverse. |
| `project_remove_file` | Remove a file from a project. The file stays on disk — this takes it out of the build, it does not delete it. The reverse of project_add_file. |

## Debug

| Tool | What it does |
|---|---|
| `debug_apply_hot_reload` | Apply your pending code edits to the running program WITHOUT restarting it (Hot Reload / Edit-and-Continue). Use after editing a file during a debug session to see the change take effect live. Needs an active debug session. Answers ok=false when there is nothing pending or when the pending edit needs a rebuild instead — changing a method signature or adding a type is not something Hot Reload can take, and debug_restart is the way through. ide_read_output has the warnings when it did run. Differs from debug_evaluate, which changes values, not code. |
| `debug_attach` | Attach the debugger to an already-running local process, by pid (preferred) or by a unique name substring. Use this instead of debug_start when the app is already running (web server, service, console). After attaching, the session is running — use debug_break or set a breakpoint to pause it, then inspect. Find the pid with debug_list_processes. |
| `debug_break` | Pause the running program immediately (Debug > Break All), without waiting for a breakpoint, so the call stack and variables can be inspected. Non-blocking, so mode comes back null rather than a guess: poll debug_get_state to see it reach 'break' and learn where it stopped. Only valid while running. |
| `debug_clear_breakpoints` | Remove all breakpoints in the solution — every one, including any the user set themselves, so prefer debug_remove_breakpoint when you only mean to undo your own. debug_list_breakpoints shows what is there first. Works in any mode. |
| `debug_console_read` | Read what a debugged console application has written to its console window. This is the program's real stdout, which is NOT in the Debug output pane — that pane only carries Debug.WriteLine, so a Console.WriteLine prompt is invisible there. Use it to see what the program printed, and to find out whether it is sitting at a prompt waiting for input; debug_console_send answers it. Needs a running debug session and a project that has a console — a GUI or web app has none. |
| `debug_console_send` | Send input to a debugged console application, as if it were typed at its console. Use it when debug_console_read shows the program waiting at a prompt — a Console.ReadLine that nobody answers blocks the debug session indefinitely. Pass 'text' (Enter is appended unless newline is false), or 'key' for a single named key; 'ctrl+c' and 'ctrl+break' interrupt the program rather than typing a character. Needs a running debug session and a project that has a console. |
| `debug_continue` | Resume execution from a paused (break) state (like F5 while paused). The program runs until the next breakpoint or it exits. Non-blocking, so mode comes back null rather than a guess: poll debug_get_state to see where it stops next. Only valid in break mode. |
| `debug_detach` | Detach the debugger from every process it is debugging, leaving them RUNNING (Debug ▸ Detach All). Use this after debug_attach on something you did not launch — a service, a browser, a long-running host — where debug_stop would terminate it instead. Breakpoints stop being hit and the debug_* inspection tools have nothing to report once detached. Poll debug_get_state for the mode: the transition is not immediate. |
| `debug_evaluate` | Evaluate an expression in the current stack frame while paused (break mode), like the Watch window: pass something like 'order.Items.Count'. Returns the value and type. Note: evaluating can call property getters/methods in the program, so it may have side-effects — prefer reading fields/properties. You can also assign (e.g. 'x = 5') to change a variable's value while paused, which is how you fix a value and retry a block with debug_set_next_statement. To see inside an object rather than read one field, debug_expand walks its members in a single call. Reads the frame debug_select_frame chose. Only valid in break mode. |
| `debug_expand` | Expand an expression into its members while paused (break mode), so an object comes back as a tree instead of just a type name: pass 'order', 'order.Customer', 'this', or '$exception' when stopped on a throw (that one carries InnerException and the stack — expand it at depth 1, it is a framework type and depth 3 buries the message in static members). This is what debug_get_locals points at when it reports hasMembers=true — one call instead of a debug_evaluate per field. hasMembers on a returned node means there is more below it: expand that path to see it. truncated=true means a level had more members than maxMembers and what came back is a prefix. Note: reading a property runs its getter in the program, so this can have side-effects. Only valid in break mode. |
| `debug_freeze_thread` | Freeze or thaw one thread, by the id debug_list_threads reports. A frozen thread does not run when the program resumes, which is how a race is pinned down: freeze the ones that interfere and step the one being watched. It STAYS frozen until something thaws it — a forgotten one makes the program behave in ways nothing else explains, so thaw it when the investigation is over, and debug_list_threads shows isFrozen if you lose track. Freezing everything leaves nothing to run. Only valid in break mode. |
| `debug_get_callstack` | Get the call stack of the selected thread while paused (break mode): each frame's index, function and module. Index 0 is where execution is paused; isCurrent marks the frame the inspection tools are reading, which debug_select_frame moves — and file/line come with that one, since the editor follows the selection rather than the top of the stack. This is one thread's stack: debug_list_threads shows the others, debug_select_thread switches. Only valid in break mode — if the program is still running, poll debug_get_state until mode='break'. |
| `debug_get_exception_settings` | List the exception types the debugger will break on when they are THROWN, whether or not they are handled. Only those are returned: the full list runs to thousands of types that are all set to break on unhandled only, which is the default and says nothing. Use it before debug_set_exception_breakpoint to see what is already configured, and to explain why a debug session is stopping somewhere unexpected — a first-chance break the user turned on earlier looks like a crash until you know it is set. |
| `debug_get_locals` | List the parameters and local variables of the selected stack frame while paused (break mode): each with name, type and value, the parameters first and marked isArgument. Objects are flat by default — hasMembers=true means you can see inside with debug_expand("name"), or pass depth here to walk them all at once. Reads the frame debug_select_frame chose, which is the paused one until you move it. Only valid in break mode. |
| `debug_get_state` | Get the current debug state: mode is 'design' (not debugging), 'run' (running), or 'break' (paused on a breakpoint/exception). In 'break' mode also returns the current file and 1-based line where execution is paused, and — if paused ON AN EXCEPTION — its type and message. Poll this after debug_start to know when the program has hit a breakpoint or thrown. |
| `debug_get_thread_callstack` | Get one thread's call stack by id WITHOUT making it the current thread. Use it to survey several threads — chasing a deadlock, seeing what a worker is blocked on — where debug_select_thread + debug_get_callstack would move the debugger's current thread each time, taking the user's Call Stack and Locals windows with it and never putting them back. Frames carry no file or line here: the IDE reads those from the selected frame, so they would describe a different thread. Needs break mode; debug_list_threads has the ids. |
| `debug_list_breakpoints` | List all breakpoints in the solution: each with its file+line (or function name), condition and hit-count rule (if any), how many times it has been hit this session, and whether it's enabled. currentHits separates 'never reached' from 'reached, and the condition said no'. Set them with debug_set_breakpoint or debug_set_function_breakpoint, remove one with debug_remove_breakpoint or all with debug_clear_breakpoints. Worth a look when a run stops somewhere unexpected — a breakpoint left from earlier is the usual reason. |
| `debug_list_processes` | List local processes the debugger can attach to (pid + name). Optionally filter by a name substring. Use this to find the process to pass to debug_attach. |
| `debug_list_threads` | List the threads of the program being debugged while paused (break mode): each with its id, name, location and whether it is frozen. The one the inspection tools read comes FIRST and carries isCurrent — everything else in this domain looks at a single thread, and this is what shows the others exist. Most threads carry no name of their own, the main one included (it is set in code and rarely is), and come back as '(unnamed)': the location is what identifies them. Pass an id to debug_select_thread to look at one, or to debug_freeze_thread to hold it still. Only valid in break mode. |
| `debug_remove_breakpoint` | Remove the breakpoint(s) at a file and 1-based line. Use debug_clear_breakpoints to remove all. |
| `debug_restart` | Restart the current debug session (stop, then start again — like Debug > Restart). If not debugging, just starts. Non-blocking, so mode comes back null rather than a guess: poll debug_get_state for where it lands. |
| `debug_run_to_line` | Resume the paused program and stop again when it reaches this line — the Run to Cursor command. Saves stepping through a loop or a long method one statement at a time. Non-blocking: poll debug_get_state, and check WHERE it stopped, because anything on the way there — another breakpoint, a thrown exception — pauses it first, and if the line is never reached the program just runs on to the end. Adds no breakpoint of its own. Only valid in break mode. |
| `debug_select_frame` | Choose which call-stack frame debug_get_locals, debug_evaluate and debug_expand read, by the index debug_get_callstack reports (0 = where execution is paused). Locals belong to a frame: stopped inside a method that was called, the caller's variables are out of scope until you select its frame — that is what "not in scope in the current frame" means. Same as double-clicking a line in the Call Stack window. Frames belong to a thread, so this moves within the selected one — debug_select_thread first if the frame you want is on another. The selection lasts until the program runs again. Only valid in break mode. |
| `debug_select_thread` | Choose which thread debug_get_callstack, debug_get_locals and the rest read, by the id debug_list_threads reports. They all look at one thread, so on multi-threaded code the others are invisible until you switch. The frame selection starts over at the top of the new thread's stack — frames belong to a thread — so debug_select_frame after this, not before. Only valid in break mode. |
| `debug_set_breakpoint` | Add a breakpoint at a file and 1-based line. Optionally pass a condition (an expression that must be true for the breakpoint to trigger), or a hitCount to skip the first passes — the way to stop on the 500th iteration of a loop without a counter variable to test. Works whether or not a debug session is running. Combine with debug_start + debug_get_state to pause execution at this point. |
| `debug_set_exception_breakpoint` | Configure the debugger to break when a specific exception type is thrown (first-chance), even if it's caught — useful to find where an exception originates. Pass the fully-qualified type (e.g. 'System.NullReferenceException'). breakWhenThrown=false turns it off. Works in any mode; needs a solution loaded. After it breaks, debug_get_state reports the exception type/message. |
| `debug_set_function_breakpoint` | Add a breakpoint that triggers when a function is entered, identified by name (e.g. "MyClass.Calculate") instead of a file and line. Optionally pass a condition, or a hitCount to skip the first calls — the way to stop on the 500th call without a counter to test. Works whether or not a debug session is running. Use when you know the method but not the exact line, or to avoid opening the file. debug_set_breakpoint takes a file and line instead, debug_list_breakpoints shows what is set, and debug_start begins the session that will hit it. |
| `debug_set_next_statement` | Move the instruction pointer to this line WITHOUT running the code in between — the Set Next Statement command. Skips a call that would fail, or jumps back to retry a block after fixing a value with debug_evaluate. The line must be in the file execution is paused in: the jump cannot leave the method, and asking for another file is refused here because Visual Studio would not — it reads the number as a line of the CURRENT method and moves there instead. SIDE-EFFECTFUL, unlike the rest of the debug tools: the skipped statements never run, so anything they would have assigned keeps its old value, and jumping backwards runs side effects a second time. Nothing checks that the jump makes sense — prefer asking first. Stays in break mode, and only valid in break mode. |
| `debug_start_no_debugger` | Run the solution's startup project WITHOUT the debugger (equivalent to Ctrl+F5): breakpoints are ignored and exceptions do not break into the IDE, so none of the debug_get_* or debug_step tools will have anything to report afterwards — use debug_start when you need any of that. To run a different project, solution_set_startup_project first. Returns ok or ok=false with a reason. |
| `debug_start` | The debug entry point: the usual cycle is debug_set_breakpoint → debug_start → poll debug_get_state until mode='break' → debug_get_callstack / debug_get_locals → debug_step. Inspection only works in break mode, and reads the frame debug_select_frame chose on the thread debug_select_thread chose. Start debugging the solution's startup project (equivalent to F5). Non-blocking: returns once launched; the program then runs until it hits a breakpoint or exits. Poll debug_get_state to detect when it pauses (mode='break'). No-op if already debugging. |
| `debug_step` | Step the paused program by one statement. Direction: 'over' (run the line without entering called methods — default), 'into' (step into the call), 'out' (run to the end of the current method). Waits for the step to land and returns the file/line it reached, so a step over a slow call answers when it is done rather than straight away. If it has not landed within ten seconds — stepping over something that blocks, or the program ran on to a breakpoint — it comes back without a position and says to poll debug_get_state. Only valid in break mode. |
| `debug_stop` | Stop the current debug session (equivalent to Shift+F5). No-op if not debugging. What happens to the program depends on how the session began: one that debug_start launched is terminated, while one reached through debug_attach is only detached from and keeps running — Visual Studio does not kill a process it did not start. debug_detach always leaves it running and reports its PID. Non-blocking, so mode comes back null rather than a guess: poll debug_get_state to see the session reach 'design'. |

## IDE

| Tool | What it does |
|---|---|
| `ide_activate_output` | Bring a Visual Studio Output window pane (by name) to the foreground so the user sees it. Use at a debug checkpoint to show the relevant build/debug output before asking the user to confirm. The pane name is required. Returns ok; ok=false with availablePanes when the pane isn't found — ide_read_output with no pane lists them. This shows a pane to the user; reading it is ide_read_output's job and needs no activation. |
| `ide_clear_output` | Clear a Visual Studio Output window pane (by name). Run it before an action so a later ide_read_output returns only the fresh output, not the old history. The pane name is required (no clear-all). Returns ok; ok=false with availablePanes when the pane isn't found. |
| `ide_get_diagnostics` | Get language diagnostics from the IDE's Error List. Pass uri (file://...) to limit to one file; omit it to get all. Pass severity ('Error'/'Warning'/'Info') and/or maxResults to avoid pulling in hundreds of warnings when you only care about the errors. Returns an array of files, each with its diagnostics ([] when there are none). Visual Studio only analyses files that are open in an editor, so this can be empty for a file nothing has looked at — build_solution fills the same window from the compiler, for every file, which is what to run when this comes back empty. |
| `ide_get_edition` | Get the Visual Studio edition (e.g. "Enterprise", "Professional", "Community"). The edition and the installed workloads decide what the tools can do at all — a supported=false from a nav_* or debug_* tool is usually this rather than a bug. ide_get_version gives the version alongside it. |
| `ide_get_project_structure` | Get the solution structure: each project with its name, path, and the files it contains. Recurses solution folders. Useful to learn the layout, and to get the project names build_project and build_set_startup_project want — both take a name, not a path. |
| `ide_get_version` | Get the running Visual Studio version: name (e.g. "Visual Studio 2026"), marketing year, and raw DTE version (e.g. "18.0"). For what the installation can actually do, the edition matters more than the version — see ide_get_edition. |
| `ide_get_workspace_folders` | Get the workspace folders currently open in the IDE — the solution folder, for Visual Studio. Empty when no solution is loaded, which is also why the tools needing one (build_*, nav_*, document_format) would fail; ide_get_project_structure lists what is inside it. |
| `ide_read_output` | Read text from a Visual Studio Output window pane (e.g. 'Build', 'Debug', or the running program's output). Omit 'pane' to list the available pane names first — note those come back in the IDE's language ('Compilazione' for Build on an Italian VS), but the built-in panes are also reachable under their English names. 'tailLines' caps how many lines are returned from the end (default 200). Useful to see build/debug output or the debuggee's console writes that don't go through the shell — including what a frozen thread stopped printing, which is how debug_freeze_thread is checked. ide_clear_output first to read only what happens next, ide_activate_output to put a pane in front of the user. |
| `ide_write_output` | Write text to a Visual Studio Output window pane, creating the pane if it doesn't exist. Use it to leave progress or a note where the user can see it — it appears in the IDE rather than only in the conversation, and it survives the turn. Prefer a pane name of your own over 'Build' or 'Debug', which VS writes to. Set 'activate' to bring it to the front; without it the write is silent. ide_read_output reads panes back. |

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
`mcp__vs__debug_set_next_statement` skips code rather than running it, so ask before that one too.
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
