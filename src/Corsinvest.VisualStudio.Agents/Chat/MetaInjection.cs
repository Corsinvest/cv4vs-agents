/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.RegularExpressions;

namespace Corsinvest.VisualStudio.Agents.Chat;

/// <summary>Single source of truth for CLI-injected meta entries that ride in a role:user
/// line but aren't the user's own turn (command outputs, task notifications, ticks, hooks…).
/// Claude Code hides these in its own UI (see MessageSelector's filter); we do the same,
/// host-side, so the WebView never receives them and doesn't need its own filter. NOT every
/// '&lt;'-tag is meta — the user's own prompt is often prefixed with &lt;ide_selection&gt;/
/// &lt;ide_opened_file&gt;, which must still render as a turn.</summary>
public static class MetaInjection
{
    private static readonly string[] Tags =
    [
        "<task-notification>",
        "<system-reminder>",
        "<local-command-caveat>",
        // local-command-stdout/stderr are NOT filtered: they carry a slash command's own
        // output (e.g. "Set model to X") and the WebView renders them as a slash-result block.
        "<bash-stdout>",
        "<bash-stderr>",
        "<tick>",
        "<teammate-message>",
        "<post-tool-use-hook>",
    ];

    /// <summary>True if the user text is a CLI meta-injection (not the user's own words),
    /// so it should not surface as a chat bubble. An interrupt marker is NOT meta — it is a
    /// real turn that renders with an orange bar; callers that must skip it (session title)
    /// guard it explicitly.</summary>
    public static bool IsMetaText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return false; }
        var t = text.TrimStart();
        foreach (var tag in Tags)
        {
            if (t.StartsWith(tag, System.StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    // cv-prompt.ts prepends the active editor context to the user's message as an
    // <ide_selection>/<ide_opened_file> block followed by a newline. It is not the user's words:
    // strip a leading block so callers see the prompt itself — otherwise a message sent with editor
    // context looks like it starts with "<", and title generation (which rejects "<") drops it.
    private static readonly Regex LeadingIdeContext = new(
        @"^\s*<(ide_selection|ide_opened_file)>.*?</\1>\s*",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Remove a leading IDE-context block, returning the user's own text. No block → the
    /// text is returned unchanged.</summary>
    public static string StripIdeContext(string text)
        => string.IsNullOrEmpty(text) ? text : LeadingIdeContext.Replace(text, string.Empty);
}
