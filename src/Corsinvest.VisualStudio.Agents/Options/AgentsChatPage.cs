/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.Drawing.Design;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>Permission mode a new chat session starts in; maps to the CLI's
/// <c>--permission-mode</c> (see Core/Client/PermissionMode.cs). The CLI also accepts
/// `manual` as an alias for `default` — one behaviour under two names, so it is deliberately
/// not repeated here ("Manual" is already the label of Default). `auto` stays toolbar-only:
/// it depends on the model supporting it, which isn't known until the catalogue arrives.</summary>
public enum InitialPermissionMode
{
    /// <summary>Manual — approval required for each edit (prudent default). The name stays
    /// `Default` because it is what goes on the wire and what VS serializes into the settings
    /// store: renaming it would invalidate the saved preference of everyone who already chose
    /// a mode. "Manual" is only the user-facing label.</summary>
    Default,
    /// <summary>Edit automatically — accept Edit/Write without asking.</summary>
    AcceptEdits,
    /// <summary>Plan — explore and present a plan before editing.</summary>
    Plan,
    /// <summary>Bypass permissions — nothing is ever asked. Requires
    /// <see cref="AgentsChatPage.AllowDangerouslySkipPermissions"/>: without it the mode is
    /// hidden from the selector, so starting in it would leave the session in a mode the user
    /// cannot see or leave. Falls back to Default in that case.</summary>
    BypassPermissions,
}

[ComVisible(true)]
public class AgentsChatPage : AgentsOptionsPage
{
    [Category("Display")]
    [DisplayName("Show cost and duration")]
    [Description("Show cost in USD and duration after each response.")]
    public bool ShowCostAndDuration { get; set; } = false;

    [Category("Display")]
    [DisplayName("Show relative paths in tool rows")]
    [Description("Show file paths relative to the working directory in tool rows. If the file is outside the working directory, the full path is shown.")]
    public bool ShowRelativePaths { get; set; } = true;

    [Category("Display")]
    [DisplayName("Select lines when opening file")]
    [Description("When opening a file from a tool row, select the relevant lines in the editor.")]
    public bool SelectLinesOnOpen { get; set; } = true;

    [Category("Display")]
    [DisplayName("Preview lines")]
    [Description("Number of lines shown in preview areas (tool output and user messages). 0 = no preview.")]
    public int PreviewLines { get; set; } = 3;

    [Category("File links")]
    [DisplayName("Extra linkable extensions")]
    [Description("Extensions to also turn into clickable links when Claude mentions a file in prose (e.g. `render.wgsl:20`), on top of the ~270 built-in ones. Needed only for a language we don't ship yet — a markdown link written by the model is always linked, whatever its extension. Click `…` to edit one entry per line, without the leading dot.")]
    [Editor("System.Windows.Forms.Design.StringArrayEditor, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", typeof(UITypeEditor))]
    public string[] ExtraLinkableExtensions { get; set; } = [];

    [Category("Display")]
    [DisplayName("Chat font size")]
    [Description("Font size (px) of the chat message text. Default: 13.")]
    public int ChatFontSize { get; set; } = 13;

    [Category("Display")]
    [DisplayName("Show WebView developer entries")]
    [Description("Add \"WebView DevTools\" and \"WebView task manager\" to the chat toolbar's \"More\" (…) menu. DevTools opens the WebView2 developer tools (browser console, DOM, network) on the chat itself — how you diagnose a chat that renders wrongly or stops responding. The task manager lists the browser's processes with their memory and CPU. Pre-release builds always offer both.")]
    public bool ShowWebViewDevEntries { get; set; } = false;

    [Category("Files")]
    [DisplayName("Autosave before Claude reads/writes")]
    [Description("Automatically save a file with unsaved changes before Claude reads or writes it, so Claude sees your in-editor edits (not the stale on-disk version).")]
    public bool Autosave { get; set; } = true;

    [Category("Files")]
    [DisplayName("Send post-edit diagnostics to Claude (experimental)")]
    [Description("EXPERIMENTAL, off by default. After Claude edits a file, send back the new errors/warnings that edit introduced. Currently unreliable: Visual Studio only analyses files open in an editor, so the Error List is often still empty when we read it right after the edit — Claude then gets nothing. Use the ide_get_diagnostics MCP tool instead, which works because it runs when the Error List has settled.")]
    public bool PostEditDiagnostics { get; set; } = false;

    [Category("Files")]
    [DisplayName("Allowed upload file extensions")]
    [Description("Extensions accepted when uploading/dropping files into the chat. Images (.png/.jpg/.gif/.webp) are sent as images, .pdf as a PDF document, everything else as text. Anything not listed is rejected with a notice. One entry per line, with or without the leading dot. Click `…` to edit.")]
    [Editor("System.Windows.Forms.Design.StringArrayEditor, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", typeof(UITypeEditor))]
    public string[] AllowedUploadFileExtensions { get; set; } =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf",
        ".html", ".htm", ".xhtml", ".xml", ".svg", ".css", ".scss", ".sass", ".less",
        ".vue", ".svelte", ".astro", ".razor", ".cshtml", ".xaml",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".mts", ".cts",
        ".py", ".pyw", ".rb", ".go", ".rs", ".java", ".kt", ".kts", ".scala",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".cs", ".fs", ".fsx",
        ".swift", ".php", ".pl", ".pm", ".lua", ".r", ".jl", ".ex", ".exs",
        ".clj", ".cljs", ".elm", ".hs", ".ml", ".sql", ".graphql", ".gql",
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd",
        ".json", ".jsonc", ".json5", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".env", ".properties", ".gradle", ".editorconfig",
        ".md", ".markdown", ".txt", ".text", ".log", ".csv", ".tsv",
    ];

    [Category("Display")]
    [DisplayName("Sticky user messages")]
    [Description("Keep the user message of the current exchange pinned at the top while the assistant reply / tool rows scroll below. Disable to make all messages scroll naturally.")]
    public bool StickyUserMessages { get; set; } = true;

    [DisplayName("Show tool errors inline")]
    [Description("Show the tool error message inline below the diff/output. When disabled, only the alert icon (on hover) is shown — click it to open the full error in VS.")]
    public bool ShowInlineToolErrors { get; set; } = false;

    [Category("Display")]
    [DisplayName("Compact Ask answers")]
    [Description("After an Ask (AskUserQuestion) is answered, show only the chosen option per question (compact, like VS Code). When disabled, all offered options are listed with the picked one highlighted (detailed).")]
    public bool CompactOutputAskAnswers { get; set; } = true;

    [Category("Input")]
    [DisplayName("Use Ctrl+Enter to send")]
    [Description("When enabled, Ctrl+Enter sends the prompt and Enter inserts a new line. When disabled, Enter sends and Shift+Enter inserts a new line.")]
    public bool UseCtrlEnterToSend { get; set; } = false;

    [Category("Input")]
    [DisplayName("Initial permission mode")]
    [Description("Permission mode every new chat session starts in. You can still change it per-session from the toolbar. \"Manual\" (Default) is the most cautious. \"BypassPermissions\" also requires \"Allow dangerously skip permissions\" below — without it, sessions start in Manual.")]
    public InitialPermissionMode InitialPermissionMode { get; set; } = InitialPermissionMode.Default;

    [Category("Input")]
    [DisplayName("Allow dangerously skip permissions")]
    [Description("When enabled, the toolbar's permission menu offers \"Bypass permissions\", which never asks for approval — even for commands that can destroy data. Enabling it does not skip anything by itself: the mode still has to be selected. Takes effect on new sessions (the CLI decides at launch whether the mode can ever be entered). Off by default.")]
    public bool AllowDangerouslySkipPermissions { get; set; } = false;

    [Category("Diff")]
    [DisplayName("Preview context lines")]
    [Description("Number of context lines shown around changes in the inline diff preview. The expand dialog always shows the full diff. Default: 10.")]
    public int DiffContextLines { get; set; } = 10;

    [Category("Diff")]
    [DisplayName("Ignore whitespace")]
    [Description("Ignore leading and trailing whitespace when computing diff.")]
    public bool DiffIgnoreWhitespace { get; set; } = false;

    [Category("Diff")]
    [DisplayName("Show \"Open diff in Visual Studio\" button")]
    [Description("Show the Visual Studio icon button on Edit/Write tool rows that opens the change in VS's native side-by-side diff viewer. Disable to hide it.")]
    public bool ShowOpenDiffInVsButton { get; set; } = true;

    [Category("Ignore")]
    [DisplayName("Respect .gitignore")]
    [Description("Also hide files/folders matched by the workspace's `.gitignore`, and by git's global excludes file (`core.excludesFile`), from the `@` file picker. Re-read on every change to either file (cached when unchanged).")]
    public bool UseGitIgnore { get; set; } = true;

    [Category("Ignore")]
    [DisplayName("Ignored patterns")]
    [Description("Patterns hidden from the `@` file picker. Each entry can be: an exact folder/file name (e.g. `node_modules`, `.DS_Store`), an extension prefixed with `.` (e.g. `.exe` matches `*.exe`), or a glob with `*`/`?` (e.g. `*.bak`). Click `…` to edit one entry per line.")]
    [Editor("System.Windows.Forms.Design.StringArrayEditor, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", typeof(UITypeEditor))]
    public string[] IgnoredPatterns { get; set; } =
    [
        ".git", ".vs", ".vscode", ".idea", ".hg", ".svn",
        "node_modules", "bin", "obj", "dist", "build", "out", "target",
        ".next", ".nuxt", ".svelte-kit",
        "__pycache__", ".venv", "venv", ".pytest_cache",
        ".gradle", ".terraform", ".cache",
        ".DS_Store", "Thumbs.db", ".env",
        ".exe", ".dll", ".pdb", ".ilk", ".suo", ".user",
        ".log", ".tmp", ".bak",
    ];
}
