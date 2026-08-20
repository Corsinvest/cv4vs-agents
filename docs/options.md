# Options

All settings live under **Tools → Options → cv4vs Agents**, split into five pages: **General**,
**Chat**, **Debug**, **Editor prompts** and **Profiles**.

Visual Studio persists them in its own settings store; profiles, per-solution state and caches
go to `%LOCALAPPDATA%` — see [Settings and data](settings-and-data.md).

## General

| Setting | Type | Default | Description |
|---|---|---|---|
| Restore panes on solution open | bool | `false` | Reopen the panes (with their sessions) that were open for a solution when it is reopened. |
| Default new session | `Chat` / `CLI` | `Chat` | Which kind the "New" button creates by default (the dropdown still lets you pick the other). |
| Claude executable path | file path | *(empty)* | Override auto-detection with a specific `claude.exe` (browse with `…`). Empty = auto-detect via PATH / native installer / npm. Must be the real `.exe` — `.cmd`/`.bat`/`.ps1` shims can't be launched. |

## Chat

| Setting | Type | Default | Description |
|---|---|---|---|
| Show cost and duration | bool | `false` | Show cost (USD) and duration after each response. |
| Show relative paths in tool rows | bool | `true` | File paths relative to the working directory (full path if outside it). |
| Select lines when opening file | bool | `true` | When opening a file from a tool row, select the relevant lines in the editor. |
| Preview lines | int | `3` | Lines shown in preview areas (tool output, user messages). `0` = no preview. |
| Chat font size | int (px) | `13` | Font size of the chat message text. |
| Show WebView developer entries | bool | `false` | Add "WebView DevTools" and "WebView task manager" to the chat toolbar's "More" (…) menu — the browser console/DOM/network on the chat itself, and the browser's processes with their memory and CPU. Pre-release builds always offer both. |
| Autosave before Claude reads/writes | bool | `true` | Save a dirty file before Claude reads/writes it, so it sees your in-editor edits, not the stale on-disk version. |
| Send the selected text with the message | bool | `false` | Attach the selected code itself, not just its file and line numbers. Off, the message names the lines and Claude opens the file to read them — the same content, but only if it needs it, and only once. On, the code travels with **every** message sent with a selection. The composer's context chip shows which of the two is going out (🔖 position / 🧾 position + code). See [Spending less context](chat/context-and-usage.md#spending-less-context). |
| Keep file checkpoints (Rewind) | bool | `true` | Let Claude copy a file before editing it, so [`/rewind`](chat/rewind.md) can restore it. Copies live under `~/.claude/file-history` and are never cleaned up — the reason to turn this off if you do not use Rewind. Read when a chat starts, so it applies to the next one you open. |
| Send post-edit diagnostics to Claude (experimental) | bool | `false` | Feed back the new errors/warnings an edit introduced. Experimental — unreliable because VS only analyses files open in an editor (see IDE integration). |
| Allowed upload file extensions | string[] | 93 defaults | Extensions accepted on upload/drop. Images → images, `.pdf` → document, rest → text; anything else rejected. Editable list. |
| Sticky user messages | bool | `true` | Pin the current exchange's user message at the top while the reply/tool rows scroll below. |
| Show tool errors inline | bool | `false` | Show the tool error inline below the diff/output; off = alert icon only (click to open in VS). |
| Compact Ask answers | bool | `true` | After an `AskUserQuestion`, show only the chosen option per question (compact); off = all options with the pick highlighted. |
| Use Ctrl+Enter to send | bool | `false` | On: Ctrl+Enter sends, Enter = newline. Off: Enter sends, Shift+Enter = newline. |
| Initial permission mode | `Default` / `AcceptEdits` / `Plan` | `Default` | Mode every new chat starts in (changeable per-session from the toolbar). `Default` = ask before edits. |
| Allow dangerously skip permissions | bool | `false` | Enables the toolbar's "Bypass permissions" (never asks — even for dangerous commands). |
| Diff — ignore whitespace | bool | `false` | Ignore leading/trailing whitespace when computing the diff. |
| Diff — show "Open diff in Visual Studio" button | bool | `true` | Show the VS-icon button on Edit/Write rows that opens the change in VS's native diff viewer. |
| Respect `.gitignore` | bool | `true` | Also hide `.gitignore`-matched files/folders from the `@` picker (re-read on change, cached otherwise). |
| Ignored patterns | file path | shipped defaults | Extra rules hiding files from the `@` picker, written as a `.gitignore` and kept as one — the row shows where the file is and `…` opens it in the editor. Applied only where the workspace's own ignore rules say nothing, so they are the fallback for a project that ships none. |
| Extra linkable extensions | string[] | *(empty)* | Extensions to also linkify when Claude names a file **in prose** (`render.wgsl:20`), on top of the ~270 built-in ones — needed only for a language not shipped yet. A markdown link written by the model is always linked, whatever its extension. One per line, without the dot. See [Clickable file references](file-links.md). |

## Debug

| Setting | Type | Default | Description |
|---|---|---|---|
| Log level | `None`…`Trace` | `None` | Output-window verbosity. `None` = silent; `Trace` = include bridge traffic. Lines are prefixed with the originating pane (`[chat#2]`, `[cli#1]`) so several open panes can be told apart in the single Output pane. |
| Enable performance logging | bool | `false` | Performance-span logging in the Output window (C#) and browser console (JS). Requires a VS restart. |

## Profiles

Not a settings table but an editor: each profile is a named set of environment variables (e.g.
`ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, model overrides) injected into that pane's
`claude.exe`, so a pane can run on a different provider while the IDE MCP tools keep working.

![Profiles page](images/options-profiles.png)

Profiles are listed on the left (the checkbox enables one), and edited on the right: a name, an
optional description, and the environment grid — pre-filled with the keys you are most likely to
need, so a new profile is usually just a matter of pasting two values. **Available environment
variables** links to Anthropic's reference for everything else the CLI understands.

Enabled profiles appear under **View → cv4vs Agents**, and the active one is shown in the pane
caption and toolbar.

Unlike the other three pages, profiles are **not** stored in the VS settings store: they live in
`profiles.json` so the menu can list them without opening the Options page first — see
[Settings and data](settings-and-data.md).

### Paste from JSON

The editor's **Paste from JSON** button fills the env grid from the clipboard, so you can lift a
provider's snippet straight from its docs. It accepts either a full settings block —
`{ "env": { "ANTHROPIC_BASE_URL": "…", "ANTHROPIC_AUTH_TOKEN": "…" } }` — or a plain key/value map
`{ "ANTHROPIC_BASE_URL": "…", … }`; the `env` object is used when present, otherwise the whole
object.

Profiles are not the only way: the CLI reads these variables from the process environment like any
shell would, so setting them **at the OS level** works too. Profiles are usually preferable — one
pane per provider, switchable without touching your system environment.

> **Heads-up:** pointing `ANTHROPIC_BASE_URL` at a custom host can disable the IDE MCP tools. That
> is a CLI-side restriction, not something the extension controls.

### Provider setup guides

- [z.ai / GLM](https://docs.z.ai/devpack/tool/claude) — GLM, Kimi, DeepSeek, Qwen, MiniMax
- [Qwen](https://qwenlm.github.io/qwen-code-docs/en/users/configuration/model-providers/) — DashScope Anthropic API
- [MiniMax](https://www.minimaxi.com/) — Anthropic-compatible models
- [DeepSeek](https://api-docs.deepseek.com/guides/anthropic_api/) — direct Anthropic API compatibility
- [OpenRouter](https://openrouter.ai/blog/tutorials/claude-code-openrouter/) — multi-provider gateway
- [Ollama](https://docs.ollama.com/api/anthropic-compatibility) — local open-source models
- [Complete alternative models guide](https://github.com/Alorse/cc-compatible-models) — comprehensive provider list

## Editor prompts

Not a settings table but an editor: the entries offered when you right-click code, under
**cv4vs Agents**. Picking one writes it into a chat pane's composer — it is not sent, so you can
add the half line that matters before it goes.

| Column | Meaning |
|---|---|
| Title | What the menu item reads. |
| Prompt | What reaches the composer. The instruction alone: which file and which lines travel with it through the IDE context, and Claude reads the symbol itself with the `nav_*` tools, so pasting code in here only duplicates what the pane already points at. |
| Needs selection | Greys the entry out when nothing is selected, the way Copilot greys "Optimize selection". Leave it off for prompts that read fine against the whole file. |

Rows appear in the menu in the order listed, so the one you reach for most belongs at the top;
**Restore defaults** puts back the prompts the extension ships with.

Which pane receives: the last one you worked in, brought to the front. With none open, one is
opened. If the IDE-context eye was shut, it is re-opened with the prompt — asking about this code
with nothing saying which file it is would reach the CLI as a question about nothing.

Like profiles, these are **not** in the VS settings store: they live in `editor-prompts.json` so
the menu can be built without opening the Options page first — see
[Settings and data](settings-and-data.md).
