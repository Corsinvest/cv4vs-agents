# Changelog

All notable changes to cv4vs Agents will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Added

- **Seven new debug tools, and the ones around them answer better.** `debug_expand` opens a value
  into its members, so seeing inside an object is one call instead of an evaluate per field — around
  eighteen of them for a class with sixteen properties, and only after reading the source to learn
  what to ask for. `debug_select_frame` points the inspection tools at another frame of the call
  stack, which is what makes a caller's variables reachable at all: stopped on a throw inside a
  method that was called, they were simply out of scope with no way to say so. `debug_run_to_line`
  is Run to Cursor, and `debug_set_next_statement` moves the instruction pointer without running
  what lies between — the one debug tool with side effects, and it says so. `debug_get_locals` also
  returns the method's **parameters**, which were missing entirely, and can walk the members in the
  same call. And because all of that reads one thread, `debug_list_threads` and
  `debug_select_thread` say which — on multi-threaded code the rest of the program had no id to be
  named by — while `debug_freeze_thread` holds one still, which is how a race is pinned down.

- **Files can be dropped on the chat, and pasted whatever they are.** Dragging one onto the pane
  handed it to Visual Studio, which opened it in an editor — the composer never saw it. Pasting
  anything that was not an image did nothing at all, silently, including the `.txt` and `.pdf` the
  allow-list accepts. What reaches the CLI is now chosen by the file's media type rather than by its
  extension, which is how a video came through as 1.4 MB of replacement characters. Video
  extensions are attachable too: the model cannot watch them, but a refused attachment that says
  nothing is worse than one that arrives named.

- **The open panes are named by their session.** Both menus that list them showed "Chat 3
  (Default)" — a number and a profile, identical for every chat on the same profile. The View menu
  was the odder of the two: its whole reason for existing is that the docked tab caption is stuck at
  "Chat 1", and it showed the same number the tab does.

- **A message written during a turn says so, and lands where it was sent.** Typing while Claude is
  still answering has always held the message back until the turn ended, but its bubble went in at
  once and looked exactly like a sent one — then stayed above a reply it had not prompted. Since an
  exchange begins at each user message, that reply was also filed under the wrong question, and a
  reopened session showed a different order from the live one, because the `.jsonl` records what was
  actually sent. The bubble is now greyed out while it waits and drops below the running reply when
  it goes, which is the order the session file already had. Stopping the turn takes those bubbles
  away rather than leaving them looking sent — the model was never given them. The "N messages
  queued" bar above the composer is gone with this: it counted what the bubbles now show, and its
  Clear discarded the whole queue, which was never the thing anyone wanted to do.

- **A turn tells you what it cost and how long it took.** The spinner counts the seconds while the
  turn runs and while it is thinking, and the cost lands on the hover-actions row when it finishes.
  An Agent row does the same for its own run — elapsed while it works, cost when it is done — in
  history too, where the numbers come back off the `.jsonl`. Sub-agent and thinking badges now read
  the value they are given instead of parsing their own label.

- **A message the CLI retracts leaves the chat.** When the model refuses, the CLI falls back to
  another one and withdraws the partial answer the first had already sent; we kept showing it. The
  user would read that text and reply to it while the model had no memory of writing it, so the
  transcript and the model's context quietly disagreed from there on. Both signals the CLI sends are
  honoured (`supersedes` on the replacing message, `retracted_message_uuids` at the end of the turn),
  at any depth — a sub-agent's reply is as retracted as a main-thread one.

- **The model selector follows a refusal fallback.** The same swap is persistent for the rest of the
  session, so the selector kept naming a model that was no longer answering, and the next model
  change would have sent the old id back.

### Fixed

- **Breaking on a thrown exception has never worked.** `debug_set_exception_breakpoint` answered
  "Exception settings not available (no solution loaded?)" for every call, with any solution open,
  and the guess in that message sent whoever read it looking in the wrong place. It reached the
  setting by name on an object that does not carry it under that name, so nothing was ever
  configured. Two of its own error branches were unreachable for a second reason, and a mistyped
  exception came back as a generic failure instead of saying what was wrong.

- **Half the debug tools reported the state they were leaving.** Start, stop, restart, break and
  continue all return before the transition they ask for, so the mode read straight after was the
  previous one — `debug_start` could answer "design" for a session it had just started, and the next
  call would say "already debugging" about a mode it had reported as stopped. They report no mode
  now, which is the honest answer, and point at the tool that knows.

- **`debug_step` returned the line it started from.** The same cause, and the worst of them: the
  position came back before the step had landed, so the answer was identical to the input. Measured
  on a step out of a method with a second of work left, it named the line inside that method while
  the program went on to the caller — wrong file, wrong method. It waits for the step to land now,
  and after ten seconds answers without a position rather than with a stale one.

- **The paused position came from the editor's caret, not the debugger.** Which reads as the same
  place nearly always — except `debug_run_to_line` and `debug_set_next_statement` move the caret
  themselves, so after either of those the reported position was the one they had just set: a break
  on line 19 was reported as line 17, a comment. A breakpoint knows its own line, and that is what
  answers when one is what stopped us.

- **A permission request no longer disappears behind the next one.** The banner held a single
  request and assigned straight into it, so a second `can_use_tool` overwrote the first: it vanished
  from the screen while the CLI was still waiting for an answer that could no longer be given, and
  that tool hung until the turn was interrupted. Not an edge case — a turn firing parallel tools, or
  several sub-agents, does it routinely.

- **A tool's chip no longer lies about what was sent.** An attachment whose base64 failed to decode
  went to the CLI as an empty document block: the chip sat on the message, Claude answered as if no
  file were there, and nothing anywhere explained it.

- **The token count on a turn counts what the turn cost.** It read `input_tokens` alone, which is
  what is left once prompt caching takes the rest — a turn that pushed a 540k-token video into the
  context reported `↑ 2`, and so did every other turn of that session. Content entering the context
  for the first time is counted now; context replayed from earlier turns still is not, or every turn
  would report the same five-figure number.

- **A session title is one line.** With no custom or generated title it falls back to the last
  prompt — a whole message, newlines and all — and the path feeding the pane menus never cut it. In
  a menu those newlines break the row instead of wrapping, and everything after the first line
  disappears without saying so.

- **The IDE-context tooltip stays inside the pane.** It opened towards the left edge, where there is
  no room: with a deep path it ran past the edge and covered the textarea and the toolbar buttons,
  and the start of the path — the part that says where the file lives — was the first thing to go.

- **Two IDE tools answer predictably.** `editor_get_open_files` returned whatever order the shell
  enumerator happened to give, while claiming to match VS's tab order: two consecutive calls could
  differ with no tab having moved.

- **Coming back to a pane lands the caret in the composer.** Focus went to the toolbar instead, so
  the first thing typed after switching panes went nowhere. The pane was being focused while the
  shell was still activating the frame, and the shell then moved it — a hidden pane does that
  activation for real, which is why an already-visible one appeared to work.

- **The spinner stays up for the whole turn.** It disappeared as soon as the first text arrived and
  came back between blocks, so a turn that was still working looked finished. It now goes when the
  turn does, or when a permission is waiting for an answer.

- **A build through the MCP tools no longer freezes the IDE.** It ran the build on the UI thread and
  then waited for it there, so Visual Studio was unusable until it finished — while the same build
  started from the IDE leaves it responsive.

- **Not every line on stderr is a failed session.** Anything `claude.exe` wrote to stderr became the
  red error banner: no step in the chain ever decided it was an error. An untrusted workspace, or one
  of Node's own deprecation warnings, would raise three banners over a chat that was working fine.
  A line now reaches the chat only if the process actually died — the CLI's own verdict on how bad it
  was, rather than us matching its wording release by release. Nothing is lost: every line was
  already logged.

- **An API failure looks like a failure.** Nothing on the wire says "error" when the API refuses a
  turn — the CLI fabricates an assistant message whose text *is* the error and sends it like any
  reply, so the chat drew it as one, grey dot included. The frame does carry which failure it was,
  and it is now read and shown as such.

- **Opening a tool's input opens something the editor can read.** The temporary file it was written
  to had a name Visual Studio would not open.

- **Every client operation reports its own failure.** A session respawn, an interrupt, a model or
  permission-mode change, a thinking-budget change: each could fail with nothing written anywhere,
  leaving a pane that looked fine and did nothing. Each now logs its own failure, at the point where
  it knows what went wrong.

- **Three catches on user-facing paths stopped swallowing.** A file that would not open and a
  debugger operation that failed both returned quietly, so "nothing happened" was all you got.

- **A selection no longer claims a line it never reached.** Dragging to the *start* of a line leaves
  the end offset on a line holding none of the selected characters, and it was reported anyway — so
  selecting lines 5 to 8's first column told Claude the selection spanned 5-8, one line more than
  was highlighted. The text was always right; only the line numbers Claude was asked to reason about
  were off.

### Changed

- **Every MCP tool now says what to reach for around it.** Twenty-eight of the fifty-eight named no
  other tool, so an agent needing two of them in order had to guess it or find it by failing. Three
  were not merely thin but wrong, which is what the pass turned up: `editor_close_tab` claimed to
  close any tab by its caption when it only closes a diff the extension itself opened — the user's
  documents were never within its reach, which is a guarantee worth stating — and
  `ide_get_diagnostics` never mentioned that Visual Studio only analyses files open in an editor, so
  it can come back empty for a file nothing has looked at while a build reports plenty.

- **Streaming markdown stops re-parsing what has already settled.** Each delta re-rendered the whole
  message, so the cost grew with its length: a 22k-character answer spent 697 ms of parsing to add
  180 characters. Everything up to the last blank line outside a code fence is parsed once and kept.

- **The icon buttons are Fluent's, not ours.** Nine of them were bare `<button>`s carrying ninety
  lines of hand-written CSS in two copies — one for Shadow DOM, one for the light DOM — that had to
  be kept in step by eye, and no longer were: Copy and Fork sit next to each other and had drifted
  to different colours. Hover, focus and disabled come from Fluent now, and both copies are gone.

- **The message that refuses a file opens the setting it names.** It recited a four-level Options
  path to walk by hand, and claimed binary files cannot be read — which stopped being true when
  attachments started travelling as documents with their own media type. Shorter, with a button.

- **Markdown is rendered once per message**, not on every re-render — the transcript stops re-parsing
  text that has not changed.

- **The transcript scrolls once per streaming delta**, not three times plus a timer.


## [1.2.0] - 2026-08-05

The chat pane stops fighting Visual Studio for the screen, the `@` menu finds a file wherever it
sits, and the MCP tools tell the agent what a build is actually complaining about.

### Added

- **The `@` menu searches the whole tree.** It used to look one directory deep until three
  characters were typed and four levels after that, matching on the file name alone — so a file that
  exists could not be found, and the only way down to it was picking folder after folder. It now
  walks the whole workspace with no depth limit and filters on the path, so `ctrl` finds
  `ChatPaneControl.cs` and `src/foo` works as one filter. Folders come from the files that matched,
  so the two lists cannot disagree and empty ones stay out. Git's global excludes file joins the
  workspace `.gitignore`, which is where a personal rule like `**/.claude/settings.local.json`
  lives.

- **Every log line says which pane it came from.** With several chats open the Output window is a
  single stream and two sessions interleave; per-session lines now carry `[chat#N]`/`[cli#N]` before
  the area tag, which is what makes diagnosing anything multi-instance possible at all.

- **`document_read_buffer`**: an MCP tool that reads what is in the editor, unsaved edits included.
  "Look at what I'm writing" had no answer — the `Read` tool sees the file on disk, and the autosave
  hook writes the user's buffer whether they wanted that or not.

- **`build_clean`**: rebuilding from scratch was the one build step the agent could not reach.

- **The build tools report warnings.** They used to keep only errors, so in this repository — where
  the gate is a green build and there is no test project — the whole quality signal was being thrown
  away. `severity` is a floor, like a log level; errors stay the default because a solution carries
  a hundred pre-existing warnings, and the message now names how many were left out and how to ask
  for them.

- **`ide_get_diagnostics` filters by severity and caps its result**, instead of carrying every
  warning back to read three errors.

- **The chat can report what the page weighs** — DOM nodes broken down by what a row contains,
  plus the CLI state the UI believes it has, context usage and history paging. Appended to the
  pane's Info dialog, so collecting it takes no DevTools.

- **Dialog title bars follow the VS theme.** A dark VS on a light Windows used to draw a white bar
  around themed content, the title bar being Windows' rather than WPF's.

- **Each renderer says which pane it belongs to** in the browser's task manager, instead of three
  identical rows named after the same `index.html`.

### Fixed

- **The chat pane no longer draws over Visual Studio.** WebView2 hosts its browser in a child window
  that always paints above WPF content, so VS's floating tool windows and notification bars were
  overlapped by the pane everywhere except where the WebView was. It renders inside the WPF tree
  now, which also brings back the mouse events the old hosting could not deliver — and with them the
  attention notice clearing when you click into a finished pane. `Home` and `End` move the caret
  again rather than jumping to the start or end of the document, the pane no longer flashes white
  while opening, double-click selects the word instead of the paragraph, and the browser's task
  manager opens with a window frame.

- **Arrow keys walk a wrapped draft before reaching the history.** A long paragraph with no newline
  in it counted as one line, so a single `ArrowUp` replaced the draft with the previous prompt.
  Recalling an entry now parks the caret on the edge line the arrow came from, matching VS Code and
  zsh.

- **The first history a pane shows had absolute paths**, and stayed that way while every history
  loaded afterwards was correct: those rows shorten their paths against the working directory, which
  did not arrive until the CLI had answered, seconds later.

- **A solution reload no longer blinks the panes off and on.** VS models a reload as close-then-open
  and nothing in its API says which of the two a close will turn out to be, so the hide now waits
  long enough for the answer to arrive.

- **Sessions in a very long project path could not be read at all.** Past 200 characters the CLI
  truncates its folder name, so we looked under the full name and reported a project with no
  sessions; past 260 characters .NET Framework then refuses the file itself, as "cannot find part of
  the path" for a file plainly sitting there. Both handled, and the catch that hid this now names
  the file and the exception.

- **The statistics cache stops failing on long paths**, its folders being named after the project
  rather than the CLI's directory — the data folders no project claims are removed on the way, and a
  folder that is also a project is one row rather than two.

- **The daily-tokens chart dates are readable again** — they came out as "2/", "6/", "1/", each
  label cut to fit a cell one bar wide.

- **Dismissing the rate-limit notice keeps it dismissed.** The CLI re-sends it every turn with the
  same key, and the ✕ never told anyone it had been clicked.

- **A write to the CLI can no longer be lost to a respawn**, which used to leave the caller waiting
  for a timeout rather than failing at once.

- **The lightbox stops clipping the image against its right edge**, and follows the pane when it is
  widened.

- **The composer is ready to type when a chat opens** — on first open and on a new session, where
  the caret used to be nowhere until you clicked — and no longer jumps when it regains focus.

- **`ide_get_project_structure` no longer returns a file twice**, nor a lowercased drive letter that
  disagreed with every other path in the same response. The solution walks are guarded against a
  cyclic `.sln`, which would otherwise take `devenv.exe` down with the user's unsaved work.

- **The formatting tools stay inside the open solution.** `document_format`,
  `document_organize_imports` and `document_run_cleanup` validated nothing but that the path
  existed, then rewrote it — a wrong path that happens to exist was enough to reformat a file in
  another repository.

- **The autosave hook waits for the save.** It answered "go ahead" while the write was still queued,
  so Claude could read a stale file — or, on an edit, rewrite one under a buffer VS still held. A
  failure now says so instead of vanishing into a log that is off by default.

- **A session id that is not a plain token is refused before it reaches a path**, closing a
  traversal that could return an arbitrary file to the chat as transcript.

- **The Info dialog reports the renderer process.** It asked the WebView on the thread the answer
  needed in order to arrive, so it printed "(unknown)" every single time.

- **The composer's left-hand tooltips no longer open onto the placeholder** you are about to type
  over.

- **The diff dialog's title row lines up**, with its mode icons drawn at the size of every other
  icon in the chat.

### Changed

- **The inline diff preview builds only the lines it can show.** A large edit mounted hundreds of
  rows to display twelve, none of the rest reachable — the heaviest row in a day-long chat went from
  1468 nodes to 229. Drops the "Diff - preview context lines" option, which fed two unrelated things
  at once and whose default of 10 was the reason patches were long in the first place; the preview
  now shows 3 lines of context, which is what git and GitHub show.

- **`ide_execute_code` is gone.** It reported the snippet as submitted but only ever opened the C#
  Interactive pane, so an agent would build on state that never changed. `debug_evaluate` already
  covers "run this, give me the value", typed and inside the real program.

- **The MCP documentation leads with the fact that a live IDE is on the other end** — that it has
  compiled this code and holds the semantic model — rather than with which tool beats which shell
  command. Seeing a name in a list of fifty is not the same as thinking of it, and what an agent
  brings by default are a terminal's habits.

- **Comments across the codebase justify a choice by the CLI's wire contract**, the behaviour wanted
  or the constraint that forced it, instead of by how another editor's extension does it. No
  rationale was dropped — where the attribution *was* the rationale, it is replaced by the real one.

## [1.1.2] - 2026-08-01

The chat could still stop rendering. 1.1.1 said that was fixed; it was not, and this is the actual
cause.

### Fixed

- **Hovering a timestamp stopped the chat from rendering.** The "x ago" stamp under a message wrote
  its own text into the element on hover — and that element's content belonged to the rendering
  engine, which was left holding references to nodes that no longer existed. From that moment every
  later update threw, and the pane went on looking alive while showing nothing new. One hover was
  enough: no click, no command, nothing in any log, and the failure surfaced on whatever came next,
  often minutes later. That delay is why the two fixes in 1.1.1 landed elsewhere — both were sound
  in themselves, and neither was this.

  The stamp is now a component that re-renders itself, so the text changes the only way it safely
  can.

### Changed

- **A Debug build produces the readable WebView bundle**, with sourcemaps, instead of the minified
  one. A stack trace from the chat now names real functions — which is most of the reason the crash
  above took two days to find. Release is unchanged, and still ships without sourcemaps.

## [1.1.1] - 2026-07-31

Ways the chat could stop showing what the CLI was doing, a dialog that froze the IDE, and a dead
link that said nothing.

### Fixed

- **The chat stops rendering mid-conversation.** With sub-agents running, the transcript could reach
  a state where every later update threw and nothing appeared again — a pane that looked alive but
  was frozen, with the reason visible only in the browser console. The tree was updated in place
  while Lit still held references into it, so a row skipped its update and was left rendering into
  nodes that no longer existed. The transcript is now rebuilt rather than mutated, which the type
  system enforces instead of a comment.

  The same failure had a second way in, one level down: opening **Show all** on an Agent row *while
  the sub-agent was still working* replaced its children with the fetched transcript, and those
  entries were not in the lookup the live events use. The next event for one of them reached the
  wrong branch. Both paths are covered by tests now.

- **Session info could freeze Visual Studio.** `More → Info` asked the WebView for the process
  hosting the pane and waited for the answer on the UI thread — the same thread the answer needed in
  order to arrive. A busy or missing renderer left nothing to break the wait. It now gives up after
  two seconds and reports the process as unknown.

- **Options → Apply during a turn made the running reply disappear.** Applying options re-read the
  transcript from the session file, where a reply still streaming does not exist yet. Mid-turn the
  settings are now applied on their own and the transcript is left alone.

- **A file link that cannot be opened says so.** Clicking a path that no longer resolves did nothing
  at all, which reads as a broken feature rather than a missing file.

## [1.1.0] - 2026-07-31

A pass over the chat's composer, the permission modes, and the sub-agents — plus the panes finally
behaving like the rest of the IDE's windows.

### Added

- **Back to the latest message**: scrolling up during a turn no longer means scrolling all the way
  down again. A button appears once you are far enough up, and goes away when you are back.

- **Clicking an edit selects the lines that changed**, not just the file. The range comes from the
  patch the CLI itself computed, so it is right even after the edit has landed and the file has
  moved on.

- **Active sessions in the menu**: View → cv4vs Agents now lists the open panes by their full title,
  session and profile included. Docked panes hide each other behind tabs captioned "Chat 1"/"Chat 2",
  and this is the way to tell them apart and to reach one that drifted behind its siblings.

- **The session info dialog names the processes behind the pane** — the CLI's own PID for both pane
  kinds, and for the chat the WebView2 browser plus the renderer that pane actually runs in, matched
  by frame rather than guessed from a list. A new option adds the WebView DevTools and the browser's
  task manager to the "More" menu on stable builds, for when a chat renders wrongly and the browser
  console is the only way to find out why.

- **Bypass permissions can be chosen** from the composer and set as a profile's initial mode, gated
  on a new "Allow dangerously skip permissions" option — and on the CLI's own policy, which an
  organisation can impose.

- **Written files are syntax-highlighted** by their extension, including the ones the .NET world
  uses (`csproj`, `xaml`, `props`, `targets`, `resx`, `vsct`, `psm1`, `psd1`).

- A profile's **Description is now multi-line Notes** — what the account is, when the token expires,
  why the profile exists.

### Changed

- **The composer is one row again.** Attach, commands and the file in context on the left — what
  goes into the message; model, effort, permission mode and send on the right — how and when it
  leaves. The mode names are shorter, so it all fits in a docked pane.

- **Statistics, Usage and Context usage are tool windows**, not document tabs. None of them owns a
  file, and each used to write an empty placeholder to disk purely to have something to open.

- **The context gauge and the effort control open menus** like everything else in the composer,
  instead of hand-written panels of their own.

- **The eye on the context chip appears only when sharing is paused.** Sharing is the ordinary state
  and needs no badge; not sharing is the exception, and without a mark a chip carrying a file name
  reads as "this goes with your message" when it doesn't. The tooltips say what happens rather than
  naming the machinery — "File.cs goes with every message".

- **`Write` shows the file, not a diff.** A diff where every line is an addition distinguishes
  nothing; the header now carries the size instead, the way `Read` shows its line range.

- **A sub-agent's report reads as a report** — rendered as markdown, untruncated, with its file
  links opening in the editor.

- Tool output gained back the 52px of width that the IN/OUT gutter was spending on two three-letter
  words — width being what a docked pane hasn't got.

### Fixed

- **Adding a project to the solution cost you the conversation.** Visual Studio reloads the solution
  whenever the `.sln`/`.slnx` changes on disk, and the panes were closed on the way down — taking
  the running `claude.exe`, and the turn in flight, with them. Nothing in the SDK distinguishes a
  reload from a real close at the time it happens, so the close is now deferred: the panes go out of
  sight immediately but stay alive, and come back where they were if the same solution returns.

- **A closed chat pane left its renderer running.** The WebView2 control was never disposed, so the
  browser accumulated one renderer process per pane ever opened, for the lifetime of the IDE.

- **Panes vanished when the debugger started.** Visual Studio keeps a separate window layout for
  run time, and a pane opened while writing code was simply absent from it. They now come back, and
  dock against windows VS owns — Chat beside Solution Explorer, CLI beside Output.

- **In Bypass, Claude could not ask you anything.** The flag that registers the question tool was
  withheld in that mode, so the model did not merely lose the right to ask — it lost the tool. Turns
  ended successfully with nothing surfaced.

- **Shift+Tab in Bypass threw you into the most restrictive mode.** It cycled a hand-written list
  that honoured none of the gates the rest of the UI applies.

- **A permission mode that failed to change still showed as changed** — you would believe you were
  in Plan while the CLI wrote. The same applied to the model. Both now roll back on failure.

- **A nested sub-agent opened its parent's transcript** when expanded, and its own tools could not
  be loaded at all.

- **A sub-agent that failed left its row green.** The row settled on launch metadata, which is never
  an error; the real outcome arrives later and was being dropped.

- **Answering a permission closed whatever diff you had open**, including one you opened yourself.
  Diffs are now identified by the tool call they preview rather than by being the most recent.

- **Icons, themes and the light theme in general**: file icons kept the theme they were first drawn
  under; a new pane opened dark whatever theme VS was in; the hover on every icon button was
  invisible on light; the effort slider was styled with tokens that only exist in VS Code's webview
  and had never been wired to our theme at all.

- **A file link VS won't open now says so** instead of doing nothing — a `.csproj` the solution
  already owns cannot be opened as a document, and the failure went nowhere.

- **The context gauge gave up before a resuming CLI could answer.** On a large transcript the reply
  arrived correct and complete, fifty seconds after a ten-second timeout.

- Clicking into a pane left its OS balloon toast on screen after the in-IDE notice had gone.

- The Sessions list counted every `.jsonl` the CLI had ever opened — sessions where nothing was ever
  asked, and sub-agent transcripts — which is why it read as a wall of dates.

- The image lightbox opened far wider than the image inside it.

- The "x ago" stamp on a message never updated; it now refreshes when the pointer reaches it.

- The effort popover grew off the edge of the composer, and its slider's fill stopped short of its
  own track.

- A failed turn drew two red rails a few pixels apart.

- WebView DevTools disappeared from development builds when the 1.0.0 release dropped its
  `-rc1` suffix.

- A file type VS declines to rasterise (`.sh`) showed a broken-image glyph.

## [1.0.0] - 2026-07-27

First stable release. Everything from the preview, plus the work below.

### Added

- **Statistics**: a full window showing where your tokens went — by profile, folder, project, day
  or single session. Daily chart, activity heatmap, per-model breakdown, and a tile summary.
  Reads the session files already on your disk; nothing is sent anywhere.

- **Usage**: your plan's limits and how much of them you have used, per profile.

- **Context usage**: what is actually filling the model's context in a session — system prompt,
  tools, memory files, messages — as a map you can read at a glance, so it is clear what to trim
  before compacting.

- **Turn settings in the composer**: thinking, effort, model and permission mode now sit on their
  own row under the message box. What the model is and what it may do without asking used to be
  three levels down a menu.

- **Read a reply aloud**: any assistant message can be spoken, with pause and resume.

- **Clickable file references**: when the assistant mentions a file — with or without a line number
  — it becomes a link that opens in the editor at that spot. Recognises around 270 extensions.

- **Session notices**: rate limits, CLI advisories and warnings stack in one place instead of
  replacing each other, and can be dismissed individually.

### Changed

- **The context gauge is always there**, empty until the first reply instead of appearing
  afterwards and shifting the toolbar around it.

- **The model picker no longer lists the same model twice.** "Default" is hidden when it is just
  another name for a model already in the list — which also means a reopened session shows the
  model you actually picked.

- **Renaming a session** now goes through the CLI rather than editing its file directly, so the
  running session and what you see agree.

- The attach button is a plus rather than a paperclip: its menu adds content as well as files.

### Fixed

- **Stopping a turn no longer looks like a crash.** It used to print a red error with an internal
  diagnostic line underneath the notice that already said you had interrupted it.

- The Copy icon in the Info dialog was nearly invisible on a dark theme.

- The image viewer opened with the wrong window chrome, and its copy button did nothing.

- Slash-command output (`/config`, `/context`, …) is shown instead of being silently dropped.

## [1.0.0-rc1] - 2026-07-22

First public preview. Requires the Claude Code CLI, installed separately with
`npm i -g @anthropic-ai/claude-code` — the extension drives it and never bundles it.

### Added

- **Chat pane**: a WebView2 chat wired into the IDE. Streaming replies, thinking blocks, tool calls
  with collapsible output, image attachments, and a prompt composer with slash commands, `@` file
  picker and prompt history.

- **CLI pane**: the real `claude.exe` in an embedded terminal (ConPTY), connected to the IDE over
  the same WebSocket channel the official VS Code extension uses. Both pane types can be open at
  once, on different working directories.

- **Diffs in the chat**: file edits render inline, switching between line-by-line and side-by-side
  as the pane is resized. Opening one gives a full viewer with four modes — auto, split, unified and
  raw patch — and an **Open in VS** button hands the same comparison to the editor's own diff viewer.

- **MCP server**: around 50 tools exposing the IDE to the agent — editor and selection, solution and
  project structure, symbol navigation, diagnostics, build, debugger, and test runner. Runs
  in-process, so the agent sees the live state rather than the files on disk.

- **IDE context**: the active document and selection are offered to the agent as context, shown as a
  chip in the composer and toggleable from the toolbar.

- **Sub-agents**: nested runs are shown inline with their own transcript, and can be opened in a
  pane of their own.

- **Sessions and history**: past conversations are read from the CLI's own `.jsonl` files — the same
  sessions the terminal `claude` sees. Resume, fork, or open in a new pane.

- **Profiles**: named configurations for working directory, model, permission mode and environment,
  so a pane can run against a different setup (including Anthropic-compatible endpoints) without
  touching the global settings.

- **Context and usage**: a live context gauge in the composer, and a stats dialog aggregating token
  usage and cost from the local session files.

- **Permission prompts**: tool approvals appear inline in the chat, with the affected file and a
  preview of the change.

- **Options**: four pages — General, Chat, Profiles and Debug — under Tools → Options → cv4vs
  Agents.

### Notes

- Visual Studio 2022 (17.0) and later, x64.
- The extension is GPL-3.0. `claude.exe` is Anthropic's and is not distributed here.
