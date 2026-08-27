# Changelog

All notable changes to cv4vs Agents will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Added

- **A table has a Copy button.** Hover a markdown table and it appears over its top-right corner,
  the way a code block's already did. It copies the markdown the model wrote — pipes, alignment row
  and empty cells — so what you paste parses back as the same table. The DOM could not supply it:
  a `<table>`'s text content is the cells run together, with no pipes and no line breaks, which is
  why the fence's approach of reading the rendered element does not carry over.

## [1.6.0] - 2026-08-26

The inline diff was rebuilt around the patch the CLI already sends, so it shows the file's real line
numbers instead of numbering every edit from 1 — and the full-screen viewer we drew ourselves is
gone, because Visual Studio's own diff was one click away the whole time. The debugger tools stopped
answering with the editor's caret instead of their own position, and four of them gained the piece
that was missing. Plus the fixes you find by reading transcripts: a file path in backticks is now a
link, a shell command is highlighted, and scrolling up for older history no longer moves the page
under you.

### Added

- **The diff shows the file's line numbers, and the context around the change.** An Edit's input
  carries two fragments, not the file, so diffing them produced a patch that started at line 1 every
  time: line 219 appeared as line 1, with nothing around it. The CLI computes the right patch when
  it applies the edit and sends it on the tool result — real line numbers, three lines of context
  either side. That patch now travels whole to the preview instead of being read for one number and
  discarded. The jump and the preview read the same hunks, so the two can no longer disagree about
  where a change is.
- **Long lines wrap.** Measured on a real C# diff: median line 59 characters against roughly 45 that
  fit in a docked tool window. More than half of every diff needed sideways scrolling to read.
- **The changed words keep their syntax colours.** A changed piece used to be highlighted on its
  own, and a highlighter given `boolean` alone sees a word rather than a keyword — so the one place
  worth reading came back plain. The line is highlighted whole and the word-diff's marks are laid
  over it.
- **Counts in the title, the way git reports them.** An edit that replaced fourteen lines with one
  said "Removed 13 lines"; git calls that +1 −14, and so does anyone reading the row.
- **Shell commands render as code.** The IN cell of a Bash or PowerShell row was a flat block of
  text, and a command is the thing that gets re-read most — pipes, redirections, quoting. It is now
  highlighted, and never truncated: measured over 6839 shell commands in this project's own
  transcripts, 73% already fit the three-line cap, so showing the rest whole costs almost nothing.
- **A newer Claude Code gets a notice.** The chat says so at the top when the npm registry has a
  release newer than the installed CLI. It only says so — the CLI is not ours, and replacing it
  under a live session would break that session. Once per Visual Studio, and detached from startup:
  the pane opens at the same speed whether the registry answers in 50ms or never.
- **`debug_enable_breakpoint`.** Turning a breakpoint off meant removing it, which took its
  condition and hit-count rule with it and could not put them back. The case that wants this is
  ordinary: a breakpoint in hot code that keeps interrupting while you are trying to reach a
  different one.
- **`document_read_buffer` reads a range.** Only a line cap existed, counted from the top, so lines
  400–450 of a large file meant pulling 450 and discarding 400.
- **`ide_read_output` filters by pattern.** On a Debug pane holding tens of thousands of lines there
  was no way to say "the lines that mention X". The filter runs *before* the tail, which is the
  order that matters — "the last N matching lines", not "the matches among the last N".
- **`solution_get_configuration` reports the startup project.** It could be set but never read, so
  the only way to use it was blind, with no way to put the previous one back.
- **A file reference in backticks is a link.** Counted over 25 real sessions in this repository: 33
  references written inside inline code against 2 written bare — the feature worked on one case in
  seventeen. Only when the whole span is a reference: `Foo.cs:12` qualifies, `cat Foo.cs:12` does
  not. On the sample, 33 links and no false positives.
- **The mode that never asks now looks like it.** Four of the five permission modes tinted the
  composer border; `bypassPermissions` — the one that runs everything without asking, dangerous
  commands included — had no rule at all and inherited the resting grey. It gets the red, and its
  name on the toolbar in red, which is the part that answers "how did I leave this pane".

### Changed

- **Clicking a diff opens Visual Studio's diff viewer.** The full-screen dialog we drew ourselves is
  gone, and with it the library behind it: 89.6 KB minified plus 16.9 KB of CSS, replaced by about
  forty lines of grid. That library rendered a GitHub-shaped page — side-by-side panes, synchronised
  scroll, a file list — none of which fits a tool window 350px wide, and everything built to make it
  fit was compensation. **Options → Chat → Diff** goes with it: its one setting governed a patch we
  no longer compute, so it had stopped changing the diff it named. So does *Show the "open in VS"
  button*, which existed to hide a duplicate and would now hide the only route.
- **Every icon-only button in the composer has a name a screen reader can reach.** A tooltip is
  drawn next to a control, not attached to it, so eleven buttons were labelled in a way assistive
  technology never saw. The worst was the Send button, which had no name by any route.
- **The IN/OUT copy button stopped covering the code.** It sat over the first line of the very text
  it copies, and on a long cell it scrolled out of view while the code was still being read.

### Fixed

- **The debugger answered with the editor's caret, not its own position.** Where execution is paused
  was read from the active document's caret. The two agree most of the time, which is why it
  survived — they part company exactly when a tool moves the caret itself, so after `run_to_line` or
  `debug_set_next_statement` the state reported the line we had just planted. Opening any file while
  paused was enough to make it point somewhere else entirely. Every frame of a call stack now
  carries its own file and line too, which is what `debug_get_thread_callstack` exists for.
- **A locals walk could freeze Visual Studio.** Depth and member caps bound the *shape* of a
  capture, not its cost: reading a value runs a getter inside the debugged program, and one that
  takes a lock or calls out to the network left the walk sitting there with no limit exceeded,
  because none of them measured time. There is now a deadline, checked where a single slow getter is
  actually reached.
- **Evaluating an expression had no time limit at all** — the member walk had a ceiling for exactly
  this reason; the evaluation it starts from did not.
- **A breakpoint that could never fire looked like any other.** Setting one answered "ok" whether
  the line held executable code or not, and a function breakpoint answered the same for a method
  name that matched nothing. `debug_list_breakpoints` now reports how many locations each one bound
  to, which is half of "why did it not break".
- **Asking to pause an already-paused program was an error** — that is the state the caller was
  asking for.
- **`document_read_buffer` said "not open" for the file most worth reading.** It looked the document
  up by a route that misses preview tabs, so a file open in one had `document_check_dirty` answering
  "open, and dirty" while this one sent the caller to disk — for exactly the file whose buffer
  differs from it.
- **Loading older history dropped the reading position.** Scrolling up left the transcript a couple
  of paragraphs below where it had been: blocks that have never been on screen are counted at a
  guess until they are laid out for real, and on a real transcript those guesses were off by 504px
  over twelve blocks. Two supporting bugs went with it — the observer meant to re-anchor while
  images settle watched an element whose height never changes, and a second page could be fetched
  while the first was still being placed.
- **Expanding a failed Agent hid the button explaining the failure.** The row's action slot was
  replaced rather than extended, so opening the one row you open *because* it failed swapped the
  error button away.
- **The rewind dialog read its two counts backwards.** "+2 −5" described the change in the wrong
  direction: those are the lines the button is about to add and remove. Now spelled out in the
  future tense — "5 lines will be removed and 2 lines added across 1 file".
- **A word-diff marked whole lines as changed.** Pairing each removed line with the added one that
  follows treats two unrelated lines as one edit: measured on a real patch, 24 rows out of 28 were
  marked end to end, which says nothing at all. A pair is now word-diffed only when most of its
  words already match — same measurement afterwards, a single word inside a ninety-character line.
- **The range in a file link never worked.** `x.cs:35-48` opened at line 35 and selected nothing —
  the attribute carrying the end of the range was being stripped before it reached the page. A range
  selected downwards also left Visual Studio scrolled to its *end*, with the beginning off the top
  of the screen.
- **Diff temp files were never deleted** — 213 of them in `%TEMP%`, the oldest four months old. They
  now leave with the window that showed them.

## [1.5.0] - 2026-08-20

Claude can put your files back where they were without touching the conversation, and the chat
stops being slow in the two places it was: a long transcript no longer re-renders itself on every
token, and opening a pane no longer waits three seconds on a name lookup that was never going to
resolve. The composer is redrawn around one border instead of six, and four bugs turned up while
doing it.

### Added

- **`/rewind` — put the files back, leave the conversation alone.** Claude edits a handful of files
  across a long turn and one of them was a mistake; undo in the editor only reaches what is open,
  and re-asking rarely restores exactly what was there. The command opens a picker of the session's
  user messages, and choosing one shows **what going back to it would change before anything is
  written**: which files, how many lines each. Clicking a file opens it in **Visual Studio's own
  diff** — its copy from before that message against what is on disk now — so the decision is made
  looking at the change, not at a filename. Only the Rewind button touches disk.
  - **The conversation is never modified.** VS Code's version rewinds the transcript too, and forks
    it; this one restores files and nothing else, so the chat you are reading stays the chat you
    were having.
  - Only messages that actually changed files are offered — a question that changed nothing is not
    a place to go back to.
  - A file the turn *created* has no earlier copy, so rewinding past it deletes the file and the
    diff shows an empty left side. Said in the dialog rather than discovered afterwards.
  - The command hides itself when the pane has no snapshots behind it, which is the honest answer
    to a command that could only reply "nothing to restore".
  - Snapshots come from the CLI, under `~/.claude/file-history` — now documented in
    [Settings and data](docs/settings-and-data.md), as the one folder there that grows on its own
    and is never cleaned up. **Options → Chat → Keep file checkpoints** turns it off; it is read
    when a chat starts, so flipping it mid-session leaves that session as it was and says so.
  - See [Rewind](docs/chat/rewind.md).

- **Send the selected code with the message, for the buffer you have not saved.** The block that
  travels with each prompt named the lines — *lines 40 to 78 from Foo.cs* — and carried nothing
  between them, so on an unsaved buffer the model opened the file and read the stale version from
  disk. **Options → Chat → Send the selected text with the message** attaches the selection itself.
  Off by default: the code goes out again with *every* message while that selection stands,
  including "yes" and "go on", and with it off the model opens the file only when it needs to.
  The context chip says which of the two is going out — a bookmark for the position alone, a code
  block when the text rides along. See
  [Spending less context](docs/chat/context-and-usage.md#spending-less-context), which is new and
  covers what actually saves context and what only looks like it does.

### Changed

- **The composer, redrawn around one border.** Six borders became one, on the text field itself;
  the model, effort, permission and context controls sit outside it as flat subtle buttons, and
  attachment chips moved to their own band above the text — a long list now pushes the field down
  instead of eating the first line you type. The slash button is gone: its job is the third item in
  the `+` menu, next to attaching a file and referencing one, which is where you were already
  looking. Tooltips stopped repeating what clicking obviously does.

### Fixed

- **Ask: one answer ticked several options.** The chosen-option test searched the whole result
  text, so "Tab to indent, spaces to align" also lit *Tab* and *Spaces*, and "Dark High Contrast"
  lit *Dark*. With several questions it reached across them and ticked options from the wrong one.
  The copy button had the same bug, and additionally listed nothing ticked when the answer came
  through Other.

- **An Ask answered with Other showed no answer at all.** Typing a free-text answer rendered as
  unanswered — an em dash in the compact cell, no chosen row in the full list, a dash in the copied
  markdown. The answer had always reached the CLI; only the rendering lost it.

- **Enter in the last question's Other box did nothing.** It submits now.

- **The `@` menu opened on email addresses, and half-referenced paths with spaces.** Typing
  `mario@rossi.it` opened the file picker, and Enter or Tab then picked a suggestion instead of
  sending the message; the trigger now follows the CLI's own rule (start of text, or whitespace
  before the `@`). And a picked file whose path contains a space reached the CLI cut short —
  `docs/my notes/todo.md` arrived as `docs/my` — so the menu now quotes it.

- **The IDE-context chip vanished when a session was reopened.** A submitted message can reach the
  CLI as several text blocks, and the replay kept only the last one — which meant the block holding
  the context tag was the one overwritten. Live it was fine; on reopening it was gone.

- **The chevron on Ask and Update Todos toggled nothing.** Both rows looked open-and-active at rest
  and did nothing when clicked, because neither has a second view to expand into. The Agent row
  keeps its chevron, which works.

- **The confirmation dialogs were drawn by Windows, not by Visual Studio.** Deleting a session,
  restoring the default editor prompts, pasting a malformed profile — each threw up a white Windows
  box in the middle of a dark IDE. All nine now use the shell's own dialog, themed like everything
  else, and the Yes/No ones default to **No**: a stray Enter no longer deletes a session.

- **The session picker froze Visual Studio while it read the folder.** Opening the list scanned
  every session file on the UI thread — on this repo's own working directory, 2031 sessions across
  520 MB. The popup now opens immediately and fills in behind: **508ms cold, 40ms warm**, and the
  IDE stays responsive throughout. While it is still reading, the empty list says so instead of
  claiming there are no sessions; and a session whose only prompt was blank lines no longer shows
  as an empty row.

- **The microphone stayed on after closing a pane mid-dictation**, and **reopening a dialog left a
  phantom entry** that swallowed the next Escape.

- **A bogus ARIA role on every chat bubble.** Five of the seven message kinds were opening a nested
  live region inside one that already existed, so screen readers announced things twice or not at
  all.

- **The queue row, in the two things it is for.** Its bin now lines up with the send button below
  it, all three bins light up on hover, a long queued message clamps at three lines instead of six
  — one pasted function used to bury everything else waiting — and a queued message with
  attachments shows the same chip the composer and the sent bubble show.

- **A queued message scrolled out from under you.** Sending one slid its bubble below the reply and
  left you looking at a gap; if you were already at the bottom, the view follows it now. Scrolled
  up, nothing moves.

### Performance

- **Opening a chat pane is about three seconds faster.** The page had its HTML in 4ms and then sat
  there for two and a half seconds — the same ±3ms gap however loaded the machine was, which is the
  shape of a fixed wait rather than of work. It was the hostname: the WebView is served from a
  virtual host, and `.local` is reserved for mDNS, so Windows answered it with a multicast query and
  waited out the timeout before WebView2 served the file from disk as it was always going to.
  Opening a pane goes from **~4.5s to ~1.5s**, and the number stops moving — 1215/1255/1334ms across
  three panes, against 2.1–4.3s before.

- **A long transcript no longer re-renders itself on every token.** One streaming delta used to
  update every message and every tool row in the conversation, and force a reflow per user bubble.
  Measured on a real 24.5k-node transcript — 318 tool rows, 402 messages — median full layout fell
  from **46.4ms to 8.8ms (81%)**, and a token now updates only the message that changed. Code stops
  re-highlighting once its fence closes.

### Internal

- Twenty optional parameters that no call site relied on, removed from the C# side. No behaviour
  change: the signatures now say what the calls actually do.

## [1.4.0] - 2026-08-17

A session can be driven from somewhere else — claude.ai/code, or a phone — and the code you are
looking at can start one, from the right-click menu. The MCP catalogue reaches past reading the
solution into changing it: files into projects, projects into the solution, the configuration
builds go through. And an Output window that is not in English stops hiding the build log.

### Added

- **Remote Control: drive a session from claude.ai/code or your phone.** A pane can hand its
  session to the CLI's bridge and post the URL and a QR into the transcript, so the same
  conversation continues from a browser or a phone while Visual Studio keeps it. The QR is for the
  phone; the link beside it now has a copy button, because sending it to someone meant selecting a
  URL wrapped across three lines on purpose. Opening it also closes the command menu — the other
  toggles act in place and leave it up, but this one posts a card into the transcript and the menu
  was sitting on top of what you had just asked for. If the bridge drops, the banner says so rather
  than advertising a session that has gone. See [Remote Control](docs/chat/remote-control.md).

- **Ask Claude about the code you are looking at, from the right-click menu.** A **cv4vs Agents**
  submenu on the editor's context menu — Explain, Review, Find bugs, Write tests, Simplify — writes
  the prompt into a chat pane's composer instead of sending it, since the canned text is generic
  and usually wants half a line added first. Entries that need a selection are greyed out rather
  than hidden, the way Copilot's are: one that comes and goes reads as a bug. The prompts carry no
  code — the file and selection reach the CLI through the IDE context, and Claude reads the symbol
  with the `nav_*` tools — so **Options → Editor prompts** lets you write your own: title, prompt,
  and whether it needs a selection, in the order they appear. Which pane receives is the last one
  worked in, brought forward; with none open, one is opened. The IDE-context eye is re-opened with
  the prompt, since asking about this code with nothing saying which file it is would reach the CLI
  as a question about nothing.

- **Take one message back out of the queue.** Writing while a turn runs queues the message, and
  until now regretting one meant Stop — which clears the queue but interrupts the answer with it,
  rarely what you wanted. A row above the composer now holds what is still waiting: with one
  message it shows its text and a bin, with more a count that opens the list, each entry with a bin
  of its own and numbered in the order it will go out. That order is the one thing the greyed-out
  bubbles never showed, and by the time a turn has been running they have usually scrolled out of
  view anyway. The row is there only while something is queued, so it costs no space the rest of
  the time, and the bin keeps its place in both forms so it can be hit without looking. Nothing
  reaches the model either way: a queued message was never given to the CLI.

- **The agent can change the solution, not just read it.** A `.cs` written to disk compiles in the
  IDE and is missing from a command-line build, because this project lists every file explicitly —
  the first trap in our own contributor notes. `project_add_file` goes through the same path
  Solution Explorer's "Add → Existing Item" takes, so the item lands where the project system wants
  it; an SDK-style project that globs its files says the file is already included rather than
  adding a duplicate. With it come `project_remove_file` and `solution_add_project` /
  `solution_remove_project` one level up. All four leave the files on disk: taking something out of
  the build and deleting it are different intents, and only the first is reversible.

- **Which configuration a build went through, and switching it.** Builds followed whichever
  configuration the IDE happened to have active, and nothing in the exchange said which — with the
  toolbar on Release and the caller assuming Debug there was no way to tell. `build_solution` and
  `build_project` now report it, `solution_get_configuration` reads the active one plus the names
  that can be asked for without building anything, and `solution_set_configuration` switches it.
  Switching is a tool of its own rather than an argument on the build, because it changes the IDE
  and leaves it changed: the toolbar dropdown moves and the user's next manual build follows it.

- **`build_cancel`.** A build had a thirty-minute ceiling and then gave up, leaving the IDE
  compiling with nothing able to stop it — the next build found the road occupied. This closes that
  path, including a build the user started outside the chat. Finding nothing running reports
  success, since that is the state the caller asked for, and the build timeout message now names
  the tool, so the model meets it at the moment it needs it.

- **`ide_write_output`.** Writes into an Output window pane, creating it on demand — somewhere to
  leave a note where the user will see it, that outlives the turn.

- **Every tool says whether it reads or writes.** Without the standard MCP annotations the CLI
  cannot tell one from the other, so `nav_find_references` cost the same permission prompt as
  `debug_stop`. Three independent flags rather than one value, because the axes combine: read-only
  and idempotent, or destructive and idempotent. The stepping tools stay deliberately bare — they
  advance execution, which is neither.

- **Backgrounded sub-agents are visible in the chip.** Sub-agents that outlive the turn had nowhere
  to be seen: the chat looked finished while three of them were still working. They render as a
  section under the foreground rows rather than behind a tab, which would hide half the answer
  behind a click, and the heading counts the whole set — Background is a subset, not a rival
  category. One clock drives every elapsed badge on the page instead of a timer per row.

### Fixed

- **Chat and CLI panes started in the wrong folder with a folder open.** With Visual Studio in Open
  Folder mode — no `.sln` — the panes fell back to the user profile, so the CLI worked from the home
  directory rather than the folder on screen. `IVsSolution` reports no directory there, because
  there is no solution file backing it; the folder now comes from the workspace itself. A missing
  Open Folder assembly degrades to the old fallback instead of failing package activation.
  Thanks to [@olxsoft01-boop](https://github.com/olxsoft01-boop).

- **The transcript was yanked to the bottom while reading back.** New assistant text, tool rows,
  thinking blocks, compaction notices and errors all scrolled to the bottom unconditionally,
  fighting anyone who had scrolled up to read an earlier message. They are gated on the same
  near-bottom check the streaming deltas already used.
  Thanks to [@olxsoft01-boop](https://github.com/olxsoft01-boop).

- **A queued message ended the running turn's exchange.** Write a second prompt while a turn is
  answering and the header of the turn actually in flight came unstuck halfway through its own
  reply. A message waiting in the queue heads no turn — the CLI has not been given it — but it
  opened an exchange all the same, ending the running one early, and a pinned user bubble only
  holds within its own exchange. It now stays with the turn in progress and opens its own once sent.

- **Built-in Output panes were unreachable outside an English IDE.** The Build pane is
  "Compilazione" on an Italian install, and the panes were matched by display name — so
  `ide_read_output`, `ide_clear_output` and `ide_activate_output` all answered "no output pane named
  'Build'" with a list in which Build indeed was not there, indistinguishable from a build that
  produced nothing. The four built-in panes now resolve through a GUID that does not change with the
  IDE language, and a pane the Output window has never displayed is realised before being read —
  it holds what the build wrote, but refuses to hand over its text until it has been shown once.

- **The `@` picker's ignore rules never matched what they were written to match.** Patterns were
  tested against the wrong form of the path, so rules that looked right in the settings did nothing
  in the picker.

- **Project-level diagnostics were dropped, and the workspace folder was wrong.** The Error List
  carries entries that name no file — an unresolved reference, a file the project would not add —
  and every one was discarded: 14 of 232 on this solution, and the ones a caller cannot find another
  way, since a diagnostic attached to a file surfaces again when that file is opened and these never
  do. Separately, the workspace folder was re-derived from the solution path, which in Open Folder
  mode handed the CLI the *parent* of the open folder — wrong in a way that reads as right.

- **A wrapped permission label was centred.** "Yes, allow Bash(cd …) for this project (just you)"
  is long enough to wrap in a narrow pane, and the second line came out centred under the first, so
  the tail of a sentence read as an element of its own. The two short buttons never showed it
  because they never wrap.

- **A freshly opened pane's composer took the caret but not the keyboard.** Only on the first open:
  the composer drew its focus border while the keystrokes went to Visual Studio.

### Changed

- **The `@` picker's ignore rules are a `.gitignore` file, not a list of patterns.** They were a
  string array in the Visual Studio settings store, each entry an exact name, an extension with a
  leading dot, or a `*`/`?` glob — three conventions of our own for something every developer
  already knows how to write. They now live in a real `.gitignore` under the app data folder, with
  its syntax, its comments and its negations; **Options → Chat → Ignored patterns** shows the path,
  and the `…` button opens the file in the editor. Two consequences worth knowing: the rules now
  apply **only where the workspace's own ignore rules say nothing**, so a repository that ignores
  its own build output decides for itself and these are the fallback for one that ships no rules at
  all; and **a customised list from 1.3.0 does not carry over** — the file starts from the shipped
  defaults, which are the same set the array held, so only a hand-edited list needs redoing.

- **`build_set_startup_project` is now `solution_set_startup_project`.** The startup project is a
  property of the solution, not of a project, and the API says so. Nothing outside the catalogue
  refers to tools by name, and the model reads the catalogue fresh each session.

## [1.3.0] - 2026-08-10

The debugger is reachable: the agent can look inside an object, move up the call stack, and say
which thread it is talking about. Navigation stops lying — searching the solution for a symbol
never worked at all, and go-to-definition gave up at the edge of your own code. Files can be
dragged onto the chat, and every tool now says what to reach for around it.

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

- **Go-to-definition stopped at the edge of the solution.** Ask `nav_go_to_definition` where
  `DialogWindow` or `string` is defined and the answer was "No definition found" — which is not
  merely incomplete, it is wrong: the definition exists, it just isn't a file on disk. The service
  behind it only covers code with source in the solution, and returns nothing at all for anything
  else, so a model reading that answer concludes the symbol does not exist and stops looking. The
  comment in our code said those results were being skipped; there were never any to skip. VS's own
  F12 has a second half we did not have — it resolves the symbol, asks Roslyn to write the
  declaration out, and navigates to the file that comes back. That now happens here too, taking
  about two seconds the first time an assembly is decompiled and nothing on later hits. A definition
  found this way says which kind it is: `decompiled`, rebuilt from IL with the names of locals lost,
  or `source`, the real thing fetched through SourceLink — the difference decides how much of what
  you are reading can be trusted. The files are generated, and the tool says so, because nothing
  should try to edit them.

- **Searching the solution for a symbol has never worked.** `nav_search_workspace_symbols` answered
  `supported=false` on every call, in every language, and the reason it gave — that this Visual
  Studio has no NavigateTo — was wrong: the service was there and handed out on request. It was
  looked up under `SearchProjectAsync`, a name Roslyn dropped for `SearchProjectsAsync`, so the
  probe stopped one letter short. Two more faults were waiting behind it. The kinds of symbol to
  match were passed as an empty set, which reads as "no kind at all" rather than "any", and returned
  nothing without failing; they now come from the service's own `KindsProvided`, so each language
  offers what it actually has. And the results arrive as a private type that implements its
  interface explicitly — nothing is visible on the type itself, so every field read came back null
  and four found symbols mapped to zero. A hit now also carries the declaration line, which was
  being computed and discarded, so choosing between twenty of them no longer means opening files.

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
