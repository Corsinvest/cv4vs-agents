# CLAUDE.md

Guidance for Claude Code working in this repository. Only what you can't infer from the code:
architecture is documented in [architecture.md](docs/src/content/docs/architecture.md) and the rest
of the documentation site's pages, `docs/src/content/docs/`.

## What it is

A VS 2022/2026 extension (VSIX, C#/.NET Framework 4.8, `Corsinvest.VisualStudio.Agents`) that hosts
two pane types plus an in-process MCP server exposing the IDE to the CLI. The chat UI is a WebView2
app in TypeScript + Lit.

It **drives** the real `claude.exe` (npm `@anthropic-ai/claude-code`), never bundled (licensing),
never forked. Version differences are handled by feature-detection, not by pinning a CLI version.

## The IDE is right there

When this solution is open in a Visual Studio running the extension, the `mcp__vs__*` tools reach
that IDE, the one that has built this code and holds its semantic model. Ask it rather than
shelling out or re-reading files: `ide_get_diagnostics` for what the compiler thinks,
`nav_find_references` for callers, `ide_read_output` for what a build or a debug session printed.
The extension is its own best test case, so the IDE you are talking to is usually running the
build you just made.

## Build

Use `mcp__vs__build_solution` when that IDE is open: it drives it, so there is no MSBuild path to
resolve and no clash with a debug session holding the assembly, and the errors come back as
file/line/message. Otherwise:

```powershell
msbuild cv4vs-agents.slnx /t:Build /p:Configuration=Debug   # WebView build is hooked into MSBuild
```

WebView (`src/Corsinvest.VisualStudio.Agents/Chat/WebViewSrc/`): `npm run build` / `dev` /
`typecheck` / `lint`.

**The startup project is `Corsinvest.VisualStudio.Agents`, and no file in git holds that.** VS keeps
it in the per-user `.vs/…/.suo`, and `.slnx` has no field for it, so adding a project can silently
leave F5 launching something else, and the fix is to set it back, not to look for a file to commit.

**Installs stack up.** VS keys extensions by `Identity Id`, so a build with a changed identity
(or a changed display name) installs *alongside* the old one: duplicate menu entries, two MCP
servers, and symptoms that look like bugs in the code. `tools\extension.ps1` is the test cycle
(remove every copy, then install into the Exp hives); `-Uninstall` clears them and refreshes the
hives, which deleting the folder alone does not: VS keeps serving the cached menu entries.

**Unit tests exist, and cover less than the extension does.** `tests/Corsinvest.VisualStudio.Agents.Tests`
holds the xUnit tests over the pure logic: the JSONL readers, the content-block translator, meta
injection, stats, schema building. Run them:

```powershell
dotnet test tests\Corsinvest.VisualStudio.Agents.Tests\Corsinvest.VisualStudio.Agents.Tests.csproj
```

**Build first, then test: that order IS the gate.** The project takes a `<Reference>` on the built
`Corsinvest.VisualStudio.Agents.dll`, not a `ProjectReference` (a legacy VSIX resolves its
dependencies through packages.config, and the modern SDK rebuilding it buries the run in CS0246).
So `dotnet test` after editing a `.cs` and nothing else tests the PREVIOUS build, silently, and it
will look like your change is covered when nothing ran it.

Everything else (WPF, the WebView, the MCP surface, anything touching the VS shell) is verified by
hand in the Exp instance (F5 → `devenv /rootsuffix Exp`). A green build proves less than it looks:
XAML `x:Class`, `.vsct` ids and the manifest fail at *runtime*, not compile time: a mismatched
`.vsct` id gives a silent no-op menu entry. CI runs the tests after its build (`quality.yml`,
`release.yml`, `--no-build` against the DLL it just built), so a red suite fails the PR, but only
there if you skipped the local run.

`tests/LangMatrix` is not a test project: five throwaway libraries the solution loads but never
builds, so the `nav_*` tools can be pointed at a real file in each language.

## Traps

Things that break in ways the compiler won't tell you about:

- **New `.cs` files must be added by hand to `<Compile>` in the `.csproj`**: explicit items, no
  glob. A file that compiles in VS can be silently missing from the MSBuild VSIX.
- **`CLAUDE_CODE_ENTRYPOINT=claude-vscode`** is mandatory when launching the CLI: without it
  `initialize` omits `unavailable_models`, the models the picker shows greyed out.
- **`Newtonsoft.Json` pinned to 13.0.1**: the version VS forces at runtime; a higher one throws
  `MissingMethodException`. Use `JsonExtensions.ToIndentedString`, not `JToken.ToString(Formatting)`.
- **Target framework v4.8**, not 4.7.2, required by `Community.VisualStudio.Toolkit`.
- **`bridge-messages.ts` is generated** from `Chat/Host/BridgeMessages.cs` by
  `WebViewSrc/tools/gen-bridge.mjs` (part of `npm run build`). The C# file is the single source of
  truth: never edit the `.ts`.
- **`~/.claude/` paths and `claude.exe` names are the CLI's contract**, not ours. `ClaudePaths`,
  `ClaudeClient` and `ClaudeInstall` are named after what they drive: leave them alone.
- **`AppConstants.AppId`** names `%LOCALAPPDATA%\Corsinvest\<AppId>\` (profiles, WebView2 profile,
  caches). Changing it moves the user's data.
- **`.gitignore` excludes `[Dd]ebug/`** with an explicit exception for `Mcp/Tools/Debug/`. Renaming
  paths without updating it silently untracks those tools.

## Architecture notes

Full description in [architecture.md](docs/src/content/docs/architecture.md). What matters when
editing:

- **The two startup paths are deliberately separate**: Chat (stream-json + in-process SDK MCP) and
  CLI (ConPTY + `--ide` WebSocket). Do **not** try to unify them.
- **Hot-swap, not respawn**: `set_model`, `set_permission_mode` and `interrupt` go to the live
  process on stdin. Only a working-directory change, `--resume` of another session or a fork may
  respawn it. Changing model or permission mode must never kill the process.
- **The wire is the reference.** For protocol questions, conform to what the CLI actually
  emits/accepts on stdio for the `claude-vscode` entrypoint, including its deliberate
  camelCase/snake_case inconsistencies.
- **Sessions are the CLI's own `.jsonl` files**, read with head+tail 64 KB windows (never the whole
  file). The scan matches on strings for speed, so it must stay whitespace-tolerant: use `IsType` /
  `IsFlagTrue`, not a literal `"type":"x"`; a pretty-printed writer is valid JSON too.

## Conventions

- SPDX header on every source file (`GPL-3.0-only`, Copyright Corsinvest Srl).
- **Comments in English**, always, including in files whose prose is Italian. Only the non-obvious
  *why*, kept short; no narration of what the code already says. Applies to C#, TS and CSS.
- **MCP tools are language-agnostic.** Roslyn per-document language services (via reflection) or
  language-agnostic APIs (`EnvDTE`, VS commands), never a C#/VB-only path (`SymbolFinder`,
  `Renamer`, `ICallHierarchyService`). Where a capability isn't available, feature-detect and return
  `supported=false`. Naming: `domain_verb[_object]`, snake_case, domain first (≥3 tools to earn a
  domain, else `ide`); the `mcp__vs__` prefix is added automatically.
  - **`AlwaysLoad` is for the four that answer "what is the user looking at now"** (the editor
    selections, open files, diagnostics). Everything else is deferred and costs nothing until the
    CLI looks it up, which is why the catalogue can grow without slowing anything down: an
    always-loaded tool costs ~50 tokens of context on *every* turn, so adding a fifth needs a
    reason of that size.
  - **Two tools, not one with a mode flag, when the choice changes what works afterwards.**
    `debug_start` vs `debug_start_no_debugger`: after the second, nothing in `debug_get_*` will
    ever have anything to report. A flag hides that behind a value the caller set turns ago; two
    names put the consequence in the choice. Same reason `debug_set_breakpoint` and
    `debug_set_function_breakpoint` stay apart: there, merging would also cost the schema its
    `required` fields.
- **List output must be sorted**: Roslyn collects in parallel and VS collections don't guarantee
  order. Typically file (OrdinalIgnoreCase), then line, then column/name.
- **Fluent UI components stay pure**: only layout CSS (display, flex, gap, padding, position,
  width) on `<fluent-*>`; never colours, borders, shadows or token overrides.
- **A tooltip in the WebView is a `data-tip` attribute**, never `title` and never `<fluent-tooltip>`
  (which is no longer registered: the tag would render as nothing). One tooltip serves the whole
  page, moved by `ui/reveal.ts`: `title` is drawn by the system, in its theme and not Visual
  Studio's, and a component per button is a shadow root on every row of a transcript. An icon-only
  button also needs `aria-label`: `title` was its accessible name. `.tipContent` (a function
  returning a Lit template) for more than a string, `data-tip-place="after"` to open beside the
  target, `data-tip-fast` for values read by sweeping across them, `.rowTip` on a
  `cv-popover-list`. It follows the pointer's position, not `pointerover`, which the WebView stops
  sending over a list that re-renders under the pointer: do not move it back.
- **Logging** (`OutputWindowLogger`, gated by Options → Debug → Log level, default None):
  - `LogException(ctx, ex)` always written; `Perf(...)` gated by EnablePerfLog.
  - **Warn**: a recovered anomaly on a user-facing path ("why the thing you asked for didn't
    happen"). **Info**: few, key lifecycle events. **Debug**: the internal flow of one feature.
    **Trace**: raw wire traffic. Use the lazy `() => $"..."` overloads for Debug/Trace.
  - Prefix new logs with an `[area]` tag (`[client]`, `[mcp]`, `[cli]`, `[sessions]`, …).
  - **One class, two ways to get it.** `OutputWindowLogger.For(kind, () => paneId)` builds a
    per-session logger that prepends `[chat#N]`/`[cli#N]` before the `[area]` tag: with several
    panes open the Output window is one stream, and untagged lines can't be told apart. The id is a
    `Func<int>` because `PaneId` lands *after* the control is built; capturing the value would tag
    every start-up line `#0`. `OutputWindowLogger.Global` writes untagged.
  - A pane injects its logger into what it owns (bridge, handler, client, transport, sessions), so
    those classes take it in the constructor. A class used both ways (`ClaudeClient`,
    `SessionManager`) takes it as an optional arg and falls back to `Global`.
  - Use `Global` for: paths belonging to no session (MCP, IDE, package, stats); **static members**,
    which have no instance to reach: in a primary-constructor class the parameter isn't even in
    scope there (`CS9105`); and process-wide resources reached from a pane, like `ChatWebView`'s
    shared task manager, where a pane tag would claim an ownership that isn't there.
  - The Output window pane itself is process-wide, so `EnsurePaneOnUIThread`/`ActivatePane` stay
    static: one pane for the extension, whoever logs into it.
  - When converting a class, convert **every partial** of it: the compiler won't tell you that one
    file still logs untagged.
  - A catch returning a default on a user-facing path must `LogException` or `Warn`, never swallow.

## Docs

The documentation is a Starlight site built from `docs/`, published at
`https://corsinvest.github.io/cv4vs-agents/` by `docs.yml` on every push to master that touches
`docs/`. The pages are
`docs/src/content/docs/**/*.md(x)`, **written in English**; a new page must also be added to
`sidebar` in `docs/astro.config.mjs`, and `quality.yml` fails the PR if it is not. `npm run dev` /
`build` / `preview` in `docs/`; the build validates internal links.

**Stock Starlight only**: its own components, and `src/styles/vs2026.css` holds theme variables and
nothing else. `src/components/IconCard.astro` is Starlight's `Card` for an icon its set does not
have (the bug on the home page): `Card` takes only a built-in name. Use `Card` whenever it has one.
`src/components/Motto.astro` is the motto under the home page's hero, signed with the Corsinvest
wordmark (`src/assets/corsinvest-wordmark*.svg`): a local copy of the one in
`@corsinvest/cv4pve-docs-theme`, whose text, link, fonts and colours are that suite's. The accents there are stops of the gradients in
`src/Corsinvest.VisualStudio.Agents/Resources/plugin-logo.svg`. The other exception is `docs/external-links.mjs`, which opens external
links in a new tab at build time: a link written through a component (`LinkCard`, a hero action, a
sidebar entry) does not pass through it and takes `target` and `rel` by hand. `docs/matomo.mjs`
writes the Matomo script (site 15 on `matomo.corsinvest.it`, no cookies) into the head, in the
built site only, so `npm run dev` visits are not counted. The last one is
`src/styles/corsinvest-link.css`: the Corsinvest mark in the header beside GitHub, drawn over a
stand-in social icon because `social` takes only built-in names. It is the one rule that targets a
Starlight class (`.social-icons`), so check the header after a Starlight upgrade.

Links between pages are site-absolute (`/cv4vs-agents/<slug>/`). The README, the listing, the
Documentation menu entry (`GlobalMenuCommands.DocsUrl`) and the chat's Help (`links.ts`) link to
the site by full URL, and nothing checks those in CI: rename a page or a heading and they break
silently.

`docs/images/` stays where it is: the listing and the README load those files by path, and the
pages reference them from there too, so there is one copy.

`docs/marketplace-overview.md` is the odd one out: it is not documentation but the listing text.
`vs-publish.json` points the release workflow at it, so a stable tag uploads it along with the
package: the file in git *is* what the Marketplace shows. Its images still need absolute
`raw.githubusercontent.com` URLs: the portal resolves nothing relative.

That manifest carries the rest of the listing too (categories, Q&A, price). Publishing overwrites
them, so a field changed on the portal and not there is reverted by the next release.

**No em dashes (U+2014) anywhere in the repo**: Markdown, code comments, UI text, log messages, MCP
tool descriptions, commit messages. They read as machine-written. Use a colon, a comma, parentheses
or a new sentence instead; `-` for an empty value or table cell. Code that has to recognise the
character in external text writes it as `\u2014`.

