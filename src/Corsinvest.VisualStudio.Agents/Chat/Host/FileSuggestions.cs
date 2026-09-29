/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

internal sealed class FileSuggestionItem
{
    public string Name { get; set; }
    public string Path { get; set; }
    public string Dir { get; set; }
    public bool IsDir { get; set; }
}

internal static class FileSuggestions
{
    /// <summary>The picker's rows for <paramref name="query"/> over a listing already made:
    /// substring on the relative path, directories derived from what matched, tree order, at most
    /// <see cref="MaxRows"/>. Pure, so it runs off the UI thread on every keystroke.</summary>
    public static List<FileSuggestionItem> Filter(string root, IReadOnlyList<string> relPaths, string query)
    {
        // Backslashes accepted as separators so a path pasted from Explorer still matches.
        var qLower = (query ?? "").Replace('\\', '/').ToLowerInvariant();
        // Keyed by relative path so a folder sorts immediately before what matched inside it:
        // "docs/" before "docs/chat/" before "docs/readme.md", because '/' orders below every letter.
        var hits = new SortedList<string, FileSuggestionItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in relPaths)
        {
            if (hits.Count >= MaxRows) { break; }
            // The whole path, not the part after the last slash: `src/foo` is one filter.
            if (qLower.Length > 0 && !rel.ToLowerInvariant().Contains(qLower)) { continue; }

            // Directories are derived rather than listed, so empty or fully ignored ones stay out; each
            // still has to match on its own, or `foo` would offer every ancestor of every hit.
            for (var slash = rel.IndexOf('/'); slash >= 0; slash = rel.IndexOf('/', slash + 1))
            {
                var dirRel = rel.Substring(0, slash);
                var dirKey = dirRel + "/";
                if (hits.ContainsKey(dirKey)) { continue; }
                if (qLower.Length > 0 && !dirRel.ToLowerInvariant().Contains(qLower)) { continue; }
                hits.Add(dirKey, new FileSuggestionItem
                {
                    Name = dirKey,
                    Path = Path.Combine(root, dirRel.Replace('/', Path.DirectorySeparatorChar)),
                    Dir = "",
                    IsDir = true,
                });
            }

            if (hits.ContainsKey(rel)) { continue; }
            var lastSlash = rel.LastIndexOf('/');
            hits.Add(rel, new FileSuggestionItem
            {
                Name = lastSlash < 0 ? rel : rel.Substring(lastSlash + 1),
                Path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)),
                Dir = lastSlash < 0 ? "" : rel.Substring(0, lastSlash),
                IsDir = false,
            });
        }

        return [.. hits.Values];
    }

    /// <summary>Rows shown, files and derived directories together. Deliberately generous: the listing
    /// is in path order, so a low cap does not sample the tree, it stops partway through and hides
    /// everything from there to the end of the alphabet — at 600 this repository's own `tools/` was
    /// cut, 31 rows short of the 631 it produces. Bounded all the same: cv-popover-list renders
    /// every row it is given, with no virtualisation.</summary>
    internal const int MaxRows = 2000;
}
