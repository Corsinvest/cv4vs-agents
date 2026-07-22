# CLAUDE.md

Guidance for Claude Code working in this repository. Only what you can't infer from the code —
architecture is documented in [docs/architecture.md](docs/architecture.md) and the rest of `docs/`.

## What it is

A VS 2022/2026 extension (VSIX, C#/.NET Framework 4.8, `Corsinvest.VisualStudio.Agents`) that hosts
two pane types plus an in-process MCP server exposing the IDE to the CLI. The chat UI is a WebView2
app in TypeScript + Lit.

It **drives** the real `claude.exe` (npm `@anthropic-ai/claude-code`) — never bundled (licensing),
never forked. Version differences are handled by feature-detection, not by pinning a CLI version.

## Build

```powershell
msbuild cv4vs-agents.sln /t:Build /p:Configuration=Debug   # WebView build is hooked into MSBuild
```

WebView (`src/Corsinvest.VisualStudio.Agents/Chat/WebViewSrc/`): `npm run build` / `dev` /
`typecheck` / `lint`.

**Installs stack up.** VS keys extensions by `Identity Id`, so a build with a changed identity —
or a changed display name — installs *alongside* the old one: duplicate menu entries, two MCP
servers, and symptoms that look like bugs in the code. `tools\extension.ps1` is the test cycle
(remove every copy, then install into the Exp hives); `-Uninstall` clears them and refreshes the
hives, which deleting the folder alone does not — VS keeps serving the cached menu entries.

**No test project.** The gate is a green MSBuild plus manual verification in the Exp instance
(F5 → `devenv /rootsuffix Exp`). A green build proves less than it looks: XAML `x:Class`, `.vsct`
ids and the manifest fail at *runtime*, not compile time — a mismatched `.vsct` id gives a silent
no-op menu entry.

## Traps

Things that break in ways the compiler won't tell you about:

- **New `.cs` files must be added by hand to `<Compile>` in the `.csproj`** — explicit items, no
  glob. A file that compiles in VS can be silently missing from the MSBuild VSIX.
- **`CLAUDE_CODE_ENTRYPOINT=claude-vscode`** is mandatory when launching the CLI: without it
  `initialize` returns a reduced payload (no Fable / `unavailable_models`).
- **`Newtonsoft.Json` pinned to 13.0.1** — the version VS forces at runtime; a higher one throws
  `MissingMethodException`. Use `JsonExtensions.ToIndentedString`, not `JToken.ToString(Formatting)`.
- **Target framework v4.8**, not 4.7.2 — required by `Community.VisualStudio.Toolkit`.
- **`bridge-messages.ts` is generated** from `Chat/Host/BridgeMessages.cs` by
  `WebViewSrc/tools/gen-bridge.mjs` (part of `npm run build`). The C# file is the single source of
  truth — never edit the `.ts`.
- **`~/.claude/` paths and `claude.exe` names are the CLI's contract**, not ours. `ClaudePaths`,
  `ClaudeClient` and `ClaudeInstall` are named after what they drive: leave them alone.
- **`AppConstants.AppId`** names `%LOCALAPPDATA%\Corsinvest\<AppId>\` (profiles, WebView2 profile,
  caches). Changing it moves the user's data.
- **`.gitignore` excludes `[Dd]ebug/`** with an explicit exception for `Mcp/Tools/Debug/`. Renaming
  paths without updating it silently untracks those tools.

## Architecture notes

Full description in [docs/architecture.md](docs/architecture.md). What matters when editing:

- **The two startup paths are deliberately separate** — Chat (stream-json + in-process SDK MCP) and
  CLI (ConPTY + `--ide` WebSocket). Do **not** try to unify them.
- **Hot-swap, not respawn**: `set_model`, `set_permission_mode` and `interrupt` go to the live
  process on stdin. Only a working-directory change, `--resume` of another session or a fork may
  respawn it. Changing model or permission mode must never kill the process.
- **The wire is the reference.** For protocol questions, conform to what the CLI actually
  emits/accepts on stdio for the `claude-vscode` entrypoint — including its deliberate
  camelCase/snake_case inconsistencies.
- **Sessions are the CLI's own `.jsonl` files**, read with head+tail 64 KB windows (never the whole
  file). The scan matches on strings for speed, so it must stay whitespace-tolerant: use `IsType` /
  `IsFlagTrue`, not a literal `"type":"x"` — a pretty-printed writer is valid JSON too.

## Conventions

- SPDX header on every source file (`GPL-3.0-only`, Copyright Corsinvest Srl).
- **Comments in English**, always — including in files whose prose is Italian. Only the non-obvious
  *why*, kept short; no narration of what the code already says. Applies to C#, TS and CSS.
- **MCP tools are language-agnostic.** Roslyn per-document language services (via reflection) or
  language-agnostic APIs (`EnvDTE`, VS commands) — never a C#/VB-only path (`SymbolFinder`,
  `Renamer`, `ICallHierarchyService`). Where a capability isn't available, feature-detect and return
  `supported=false`. Naming: `domain_verb[_object]`, snake_case, domain first (≥3 tools to earn a
  domain, else `ide`); the `mcp__vs__` prefix is added automatically.
- **List output must be sorted** — Roslyn collects in parallel and VS collections don't guarantee
  order. Typically file (OrdinalIgnoreCase), then line, then column/name.
- **Fluent UI components stay pure** — only layout CSS (display, flex, gap, padding, position,
  width) on `<fluent-*>`; never colours, borders, shadows or token overrides.
- **Logging** (`OutputWindowLogger`, gated by Options → Debug → Log level, default None):
  - `LogException(ctx, ex)` always written; `Perf(...)` gated by EnablePerfLog.
  - **Warn** — a recovered anomaly on a user-facing path ("why the thing you asked for didn't
    happen"). **Info** — few, key lifecycle events. **Debug** — the internal flow of one feature.
    **Trace** — raw wire traffic. Use the lazy `() => $"..."` overloads for Debug/Trace.
  - Prefix new logs with an `[area]` tag (`[client]`, `[mcp]`, `[cli]`, `[sessions]`, …).
  - A catch returning a default on a user-facing path must `LogException` or `Warn` — never swallow.

## Docs

`docs/*.md` is public and **written in English** (README, options, mcp-tools, sub-agents,
architecture, context-and-usage, settings-and-data).

`docs/marketplace-overview.md` is the odd one out: it is not documentation but the listing text,
kept in git so edits are reviewable. Nothing reads it at build time — it is pasted into the portal
by hand, and its images need absolute `raw.githubusercontent.com` URLs to resolve there.

`docs/internal/` is **gitignored** and written in **Italian**: `TODO.md` (only things still to do —
delete an item when done, git history keeps the record) and `specs/` (design + plan docs; never
commit them).
