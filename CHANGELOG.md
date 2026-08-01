# Changelog

All notable changes to cv4vs Agents will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

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
