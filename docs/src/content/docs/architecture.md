---
title: "Architecture & build"
description: "How the cv4vs Agents extension for Visual Studio is put together, and how to build the VSIX from source."
---

How the extension is put together, and how to build it from source. For what it does, see the
[home page](/cv4vs-agents/).

## Tech & performance

- **Host**: C# / .NET Framework 4.8 Visual Studio package (VSIX) for Visual Studio 2022 and 2026 (VS 17.0+).
- **Chat UI**: a [WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/) app built
  with [Lit](https://lit.dev/) web components and
  [Fluent UI Web Components](https://storybooks.fluentui.dev/web-components/),
  so the UI matches Visual Studio's look and adapts to the active theme (light/dark). Fluent
  components are kept pure (no custom colour overrides), so theming stays consistent.
- **[TypeScript](https://www.typescriptlang.org/)** end to end, bundled by
  [esbuild](https://esbuild.github.io/) into a single IIFE `dist/bundle.js`; the host↔web message
  contracts are generated from the C# source (one source of truth, no hand-kept duplicates).
- **CLI pane**: a real terminal via
  [ConPTY](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
  (`Microsoft.Terminal.Wpf`), so the interactive CLI renders exactly as in a native shell.
- **Lazy everything, by design.** The guiding principle is *nothing is built, read or started until
  it's actually needed*: the extension is meant to be fast, fast, fast. Everything loads on demand
  and tears down when idle; work you don't do costs nothing.
- **Tuned for performance and low memory:**
  - Chat history is **lazy**: pages of 50 read from the `.jsonl` on demand, heavy blocks (images,
    sub-agent transcripts, full diffs) fetched only when opened; nothing loaded up front.
  - Session metadata is read with **head+tail 64 KB windows**, never loading whole files.
  - The [MCP](https://modelcontextprotocol.io/) server and event listeners start/stop lazily on the
    first/last open pane.
  - The WebView is **Shadow-DOM + static styles**, minimising reflow and style recalculation; CSS/TS
    are kept lean (linted, no dead rules) for fast paint and small bundles.

## How the CLI is started

- Settings are loaded from the user, project and local files (`--setting-sources
  user,project,local`), so your `permissions.allow` and `deny` apply as in a terminal.
- The IDE tools (`mcp__vs__*`) are pre-approved: they never raise a permission prompt, in any mode.
- A profile's variables are applied first and the extension's own last, so a profile cannot
  override `CLAUDE_CODE_ENTRYPOINT` (nor, in a CLI pane, `FORCE_CODE_TERMINAL` and
  `CLAUDE_CODE_SSE_PORT`). `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING` is set only while *Keep file
  checkpoints* is on.
- Visual Studio started from inside a Claude Code session does not pass that session's identity
  on: `CLAUDECODE`, `CLAUDE_CODE_SESSION_ID`, `CLAUDE_CODE_CHILD_SESSION` and the related variables
  are removed from every `claude.exe` it starts.
- A resumed session starts in the permission mode it was last in, read from its transcript; a new
  one in *Initial permission mode*.
- The CLI runs in a job object: it is killed with Visual Studio, never left behind.

## Build

Everything builds from a single solution; the WebView build is hooked into MSBuild.

```powershell
# Build the VSIX (also runs the WebView build via the BuildWebViewSrc MSBuild target)
msbuild cv4vs-agents.slnx /t:Build /p:Configuration=Debug
# F5 in Visual Studio launches a second (experimental) VS instance with the extension loaded.
```

WebView UI (`src/Corsinvest.VisualStudio.Agents/Chat/WebViewSrc/`):

```powershell
npm run build       # gen-bridge + esbuild → dist/bundle.js
npm run dev         # esbuild --watch while iterating on the chat UI
npm run typecheck
npm run lint
```

Requirements: the CLI is installed separately (`npm i -g @anthropic-ai/claude-code`) and driven with
the `claude-vscode` entrypoint.
