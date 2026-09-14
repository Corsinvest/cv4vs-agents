/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>What the CLI's ExitPlanMode expects back, decided without VS so it can be tested.
/// <para>The CLI injects `plan` (read from disk) and `planFilePath` into the request. Allowing with an
/// empty updatedInput, or with a plan equal to that snapshot, makes it re-read the file; any other
/// plan string is written over the file and approved as "edited by user". So a plan goes back only
/// when the text really changed, and only as read at the moment of answering: a stale one would
/// overwrite the user's edits.</para></summary>
internal static class PlanApproval
{
    /// <summary>The CLI's tool name — its contract, not ours.</summary>
    public const string ToolName = "ExitPlanMode";

    /// <summary>The plan file the CLI named, or null when it named none (a CLI predating the field).</summary>
    public static string PlanFilePathOf(JObject input)
        => input?["planFilePath"] is JValue { Type: JTokenType.String } v && (string)v is { Length: > 0 } s ? s : null;

    /// <summary>The plan snapshot the CLI sent with the request, "" when there is none.</summary>
    public static string PlanOf(JObject input)
        => input?["plan"] is JValue { Type: JTokenType.String } v ? (string)v ?? "" : "";

    /// <summary>Strip a BOM and fold CRLF to LF. A VS save can add either without the user changing a
    /// word: compared raw, an untouched plan would come back "edited by user", and sent raw it would
    /// leave the file with mixed line endings — which VS then stops on with a modal.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        if (text[0] == '﻿') { text = text.Substring(1); }
        return text.Replace("\r\n", "\n");
    }

    /// <summary>Whether <paramref name="current"/> differs from the snapshot in anything but what a
    /// save adds. Null means nothing could be read, which is not an edit.</summary>
    public static bool IsEdited(string snapshot, string current)
        => current != null && !string.Equals(Normalize(snapshot), Normalize(current), StringComparison.Ordinal);

    /// <summary>The updatedInput to allow with: empty when the text is the snapshot, so the CLI re-reads
    /// the file; otherwise the new text, normalized.</summary>
    public static JObject UpdatedInputFor(string snapshot, string current)
        => IsEdited(snapshot, current) ? new JObject { ["plan"] = Normalize(current) } : new JObject();

    /// <summary>Whether two spellings name the same plan file: the RDT moniker and the CLI's path can
    /// differ in case, separators and a trailing slash. The rule IdeContextService matches frames by.</summary>
    public static bool SamePath(string a, string b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
           && string.Equals(a.Replace('/', '\\').TrimEnd('\\'),
                            b.Replace('/', '\\').TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase);
}
