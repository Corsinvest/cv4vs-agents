---
name: cv4vs-run
description: Build, compile and drive cv4vs-agents, the Visual Studio 2022 (VSIX) extension for Claude Code. Use when asked to "run", "build", "compile", "launch", "test" or "screenshot" the extension, the chat WebView, or to drive claude.exe's control protocol. Verified surfaces: WebView build (TypeScript+Lit), VSIX build (MSBuild), CLI drive via probe.
---

# Run cv4vs-agents

A **Visual Studio 2022** extension (VSIX, C#/.NET Framework 4.8) that brings the Claude Code
CLI inside the IDE. The real UI (the Chat/CLI tool windows) lives inside a VS instance and is
**not headless-launchable**: only a human can open the experimental VS (`devenv /rootsuffix Exp`)
and see it. What you *can* drive from the command line — and what most changes actually touch —
are three surfaces:

1. **WebView** (TypeScript + Lit, in `Chat/WebViewSrc/`) → typecheck + esbuild bundle.
2. **VSIX** (the whole solution) → restore + build via MSBuild.
3. **Control protocol** → drive the real `claude.exe` with the same stream-json flags as
   `ClaudeClient.cs`, via `tools/cli-probe/probe.mjs`.

The `driver.mjs` wraps all three. **Paths here are relative to the repo root.**

> Environment: **Windows** (not Linux). Requires VS 2022/2026 with the VSSDK workload, Node, and
> `claude.exe` installed via npm (`@anthropic-ai/claude-code`). Every command below was run in
> this session on this machine.

## Prerequisites

- **Visual Studio 2022/2026** with the MSBuild component + VSSDK (Visual Studio extension development).
  Verified present: `C:\Program Files\Microsoft Visual Studio\18\Professional`.
- **Node** (verified v24) + npm.
- **claude.exe** installed via npm, at `%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`.
  Check: `which claude`.

No `apt-get`: this is Windows — dependencies are Windows installers + npm.

## Run (agent path) — the driver

```bash
# Everything in sequence: WebView + VSIX + probe init
node .claude/skills/cv4vs-run/driver.mjs all

# Individual surfaces:
node .claude/skills/cv4vs-run/driver.mjs webview        # typecheck + build WebView → dist/bundle.js
node .claude/skills/cv4vs-run/driver.mjs vsix           # restore + build VSIX (MSBuild)
node .claude/skills/cv4vs-run/driver.mjs probe init     # drive claude.exe: dump system/init
node .claude/skills/cv4vs-run/driver.mjs probe get_settings   # any control_request
```

Expected output:
- `webview` → `✓ dist/bundle.js produced` (bundle ~1.6mb).
- `vsix` → `Errors: 0` + `✓ VSIX produced: …\bin\Debug\Corsinvest.VisualStudio.Agents.vsix`.
- `probe init` → the `system/init` JSON (model, tools, slash_commands…) — the same payload
  `ClaudeClient` parses when the chat starts.

The driver auto-detects MSBuild via `vswhere`; override with env `MSBUILD=<path>` if needed.

### Driving the CLI directly (without the driver)

The probe is how you test the control protocol the chat uses (set_model, get_settings,
get_context_usage, and any `control_request`). **It must be launched with the right entrypoint**,
otherwise init is reduced (no Fable / `unavailable_models`):

```bash
cd tools/cli-probe
CLAUDE_CODE_ENTRYPOINT=claude-vscode node probe.mjs init
CLAUDE_CODE_ENTRYPOINT=claude-vscode node probe.mjs get_settings
```

See `tools/cli-probe/README.md` for all subtypes and env vars (`CLAUDE_CLI`, `PROBE_CWD`, `PROBE_TIMEOUT_MS`).

### WebView only (fast iteration)

```bash
cd src/Corsinvest.VisualStudio.Agents/Chat/WebViewSrc
npm run dev        # esbuild --watch — rebuilds dist/bundle.js on every save
```

## Run (human path) — the extension inside VS

Actually seeing the tool windows needs human hands (not automatable in this container):

1. Open the solution in VS, press **F5** → launches a second VS (`devenv /rootsuffix Exp`)
   with the extension loaded (already configured as `StartProgram`/`StartArguments` in the `.csproj`).
2. In the experimental VS: open a solution, then the chat tool window from the menu.

Headless this is useless: no window, no screenshot.

## Gotchas

- **Not Linux.** The README/CLAUDE.md are written for Windows; here the shell is MINGW64/MSYS
  (git-bash) but the toolchain is Windows (MSBuild, devenv, claude.exe).
- **MSBuild has spaces in its path.** `C:\Program Files\…\MSBuild.exe` launched with `shell:true`
  breaks on "Program" (`'C:\Program' is not recognized…`). The driver launches it with `shell:false`
  on purpose; if you write your own spawns, do the same or quote the path. (npm/node instead need
  `shell:true` because they're `.cmd`.)
- **`CLAUDE_CODE_ENTRYPOINT=claude-vscode` is mandatory** when driving claude.exe the way the
  extension does: without it, init comes back reduced. The driver sets it itself in the `probe` subcommand.
- **New `.cs` files must be added by hand to the `<Compile>` items in the `.csproj`** (explicit
  list, no glob). A new `.cs` compiles in VS via other paths but may not make it into the MSBuild VSIX.
- **`bridge-messages.ts` is generated** from `Host/BridgeMessages.cs` (`gen-bridge`, part of
  `npm run build`). Do not edit it by hand: it gets overwritten. The `build` prints the count
  (e.g. `21 fromWebView + 30 toWebView = 51 messages`).
- **Many VSTHRD warnings in the VSIX build** (vs-threading analyzers, ~100): they're known and
  harmless — `Errors: 0` is the line that matters. The driver uses `-clp:ErrorsOnly;Summary` to
  keep them from drowning the output.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `'C:\Program' is not recognized…` launching MSBuild | Path with spaces + `shell:true`. Use `shell:false` or quote. The driver already does. |
| `MSBuild not found` from the driver | The VS MSBuild component is missing, or `vswhere` can't find it. Set `MSBUILD=<path to MSBuild.exe>`. |
| probe: init without Fable / `unavailable_models` | Missing `CLAUDE_CODE_ENTRYPOINT=claude-vscode`. |
| probe times out | Raise `PROBE_TIMEOUT_MS` (default 25000); check `which claude`. |
| WebView: type errors | `npm run typecheck` for detail; the `build` minifies and doesn't always show the spot. |
