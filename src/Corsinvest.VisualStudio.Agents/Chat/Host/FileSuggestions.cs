/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
    public static List<FileSuggestionItem> Get(string root, string query)
    {
        var result = new List<FileSuggestionItem>();
        try
        {
            if (!Directory.Exists(root)) { return result; }

            var opts = AgentsOptions.Chat;
            var patterns = ParsePatterns(opts.IgnoredPatterns);
            var ignore = opts.UseGitIgnore ? GitIgnoreCache.Get(root) : null;

            // Backslashes accepted as separators so a path pasted from Explorer still matches.
            var qLower = query.Replace('\\', '/').ToLowerInvariant();

            // One walk of the whole tree, filtered on the relative path. A depth cap or a minimum
            // query length would only mean a file that exists cannot be found, which is the one
            // thing this menu is for. Pruning ignored directories keeps the cost off node_modules.
            // Keyed by relative path so directories and files can be interleaved in tree order at
            // the end: a folder immediately followed by what matched inside it. Listing every
            // directory first reads fine with five of them and not at all once they nest, which is
            // what the recursive walk now produces.
            var hits = new SortedList<string, FileSuggestionItem>(StringComparer.OrdinalIgnoreCase);
            var visited = 0;

            foreach (var file in EnumerateFiles(root, root, patterns, ignore))
            {
                // This runs on every keystroke, so the walk is bounded by how much of the tree it
                // touches, not by how deep it goes: depth is what hides files, sheer volume is what
                // costs. Reached only by trees far past the point where a flat list is any use.
                // One budget for every row, files and derived folders alike, and the walk stops when
                // it is gone. Counting only files let folders keep coming after the cut, leaving a
                // tail of directories whose contents had all been dropped — `tools/cli-probe/` and
                // nothing under it. Alphabetical order means the cut lands wherever it lands, which
                // is the trade any capped file picker makes.
                if (hits.Count >= MaxRows || ++visited > MaxVisited) { break; }

                var rel = PathHelpers.Relative(root, file);
                // Matched against the whole query, not just the part after the last slash: the
                // relative path carries the directories with it, so `src/foo` can be typed as one
                // filter instead of being split into a folder to walk into and a name to match.
                if (!string.IsNullOrEmpty(qLower) && !rel.ToLowerInvariant().Contains(qLower)) { continue; }

                // Directories on the way to a match are suggestions too — derived rather than
                // enumerated, so the two lists can never disagree and empty (or fully ignored)
                // folders stay out: selecting those gives you nothing. Each still has to match
                // the query on its own, or `foo` would offer every ancestor of every hit.
                for (var slash = rel.IndexOf('/'); slash >= 0; slash = rel.IndexOf('/', slash + 1))
                {
                    var dirRel = rel.Substring(0, slash);
                    // Sorted under the trailing slash, which is what interleaves the two kinds:
                    // "docs/" sorts before "docs/chat/" and both before "docs/readme.md", because
                    // '/' orders below every letter.
                    var dirKey = dirRel + "/";
                    if (hits.ContainsKey(dirKey)) { continue; }
                    if (!string.IsNullOrEmpty(qLower) && !dirRel.ToLowerInvariant().Contains(qLower)) { continue; }
                    hits.Add(dirKey, new FileSuggestionItem
                    {
                        // Whole relative path on one line, no parent column beside it — nested
                        // folders are the common case here, and a bare leaf leaves you guessing
                        // which of the four `cv4vs-*` you are looking at.
                        Name = dirKey,
                        Path = Path.Combine(root, dirRel.Replace('/', Path.DirectorySeparatorChar)),
                        Dir = string.Empty,
                        IsDir = true,
                    });
                }

                hits.Add(rel, new FileSuggestionItem
                {
                    Name = Path.GetFileName(file),
                    Path = file,
                    Dir = PathHelpers.Relative(root, Path.GetDirectoryName(file) ?? root),
                    IsDir = false,
                });
            }

            result.AddRange(hits.Values);
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("FileSuggestions.Get", ex); }
        return result;
    }

    /// <summary>Rows shown, files and derived directories together. Deliberately generous: the walk
    /// is alphabetical, so a low cap does not sample the tree, it stops partway through and hides
    /// everything from there to the end of the alphabet — `tools/` never showed up. Bounded all the
    /// same: cv-popover-list renders every row it is given, with no virtualisation.</summary>
    private const int MaxRows = 600;

    /// <summary>Files the walk will look at before giving up, ignored ones excluded. Guards the
    /// per-keystroke cost on a huge tree; it is not a depth limit.</summary>
    private const int MaxVisited = 20000;

    /// <summary>Files under <paramref name="dir"/>, at any depth, ignored ones left out.
    /// Directories are pruned rather than filtered so their contents cost nothing.</summary>
    private static IEnumerable<string> EnumerateFiles(string dir, string root, List<IgnorePattern> patterns, GitIgnore ignore)
    {
        foreach (var file in Directory.GetFiles(dir).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            if (IsIgnored(Path.GetFileName(file), file, root, isDir: false, patterns, ignore)) { continue; }
            yield return file;
        }

        foreach (var sub in Directory.GetDirectories(dir).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            if (IsIgnored(Path.GetFileName(sub), sub, root, isDir: true, patterns, ignore)) { continue; }
            foreach (var file in EnumerateFiles(sub, root, patterns, ignore))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Tests <paramref name="name"/> against user patterns and the workspace `.gitignore`.
    /// Patterns: glob (<c>*</c>/<c>?</c>), exact name (case-insensitive), or <c>.ext</c> shorthand.
    /// </summary>
    private static bool IsIgnored(string name, string fullPath, string root, bool isDir, List<IgnorePattern> patterns, GitIgnore ignore)
    {
        foreach (var p in patterns)
        {
            if (p.Regex != null)
            {
                if (p.Regex.IsMatch(name)) { return true; }
                continue;
            }
            if (string.Equals(name, p.Text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!isDir && p.IsExtension && string.Equals(Path.GetExtension(name), p.Text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return ignore?.Matches(fullPath, root, isDir) == true;
    }

    /// <summary>
    /// Compiles configured patterns into matchers: exact name, <c>.ext</c> shorthand, or glob regex.
    /// </summary>
    private static List<IgnorePattern> ParsePatterns(string[] entries)
    {
        var list = new List<IgnorePattern>();
        if (entries == null) { return list; }
        foreach (var raw in entries)
        {
            if (raw == null) { continue; }
            var t = raw.Trim();
            if (t.Length == 0) { continue; }

            if (t.IndexOf('*') >= 0 || t.IndexOf('?') >= 0)
            {
                list.Add(new IgnorePattern { Regex = GlobToRegex(t) });
                continue;
            }

            // ".ext" shorthand also matches as a name, so ".env" still matches the file `.env`.
            var isExt = t.StartsWith(".") && t.IndexOf('.', 1) < 0
                        && t.IndexOf('/') < 0 && t.IndexOf('\\') < 0;
            list.Add(new IgnorePattern { Text = t, IsExtension = isExt });
        }
        return list;
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var ch in glob)
        {
            switch (ch)
            {
                case '*': sb.Append("[^/\\\\]*"); break;
                case '?': sb.Append("[^/\\\\]"); break;
                case '.':
                case '+':
                case '(':
                case ')':
                case '|':
                case '^':
                case '$':
                case '{':
                case '}':
                case '[':
                case ']':
                case '\\':
                    sb.Append('\\').Append(ch); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private sealed class IgnorePattern
    {
        /// <summary>Exact-match text (null when this is a glob pattern).</summary>
        public string Text;
        /// <summary>True when <see cref="Text"/> should also match as a file extension.</summary>
        public bool IsExtension;
        /// <summary>Compiled regex for glob patterns (null for plain names).</summary>
        public Regex Regex;
    }
}

/// <summary>
/// Per-root cache of parsed `.gitignore` files. Re-parses only when the
/// file's last-write-time changes; per-keystroke `Get()` calls are O(1).
/// </summary>
internal static class GitIgnoreCache
{
    private static readonly Dictionary<string, (DateTime Mtime, GitIgnore Ignore)> _cache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public static GitIgnore Get(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        var globalPath = GlobalExcludesFile.Value;
        var hasLocal = File.Exists(path);
        var hasGlobal = globalPath != null && File.Exists(globalPath);
        if (!hasLocal && !hasGlobal) { return null; }

        // Both files in the freshness key: editing either one has to invalidate the cache.
        var mtime = (hasLocal ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue)
                    + (hasGlobal ? File.GetLastWriteTimeUtc(globalPath).TimeOfDay : TimeSpan.Zero);
        lock (_lock)
        {
            if (_cache.TryGetValue(root, out var cached) && cached.Mtime == mtime)
            {
                return cached.Ignore;
            }

            try
            {
                // The workspace file plus git's global excludes, which is where a personal rule
                // like `**/.claude/settings.local.json` lives: git honours both, so a file hidden
                // from the repo has no business showing up in the picker either.
                var text = hasLocal ? File.ReadAllText(path) : string.Empty;
                if (hasGlobal) { text += "\n" + File.ReadAllText(globalPath); }
                var ignore = GitIgnore.Parse(text);
                _cache[root] = (mtime, ignore);
                return ignore;
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("GitIgnoreCache.Get", ex);
                return null;
            }
        }
    }

    /// <summary>Git's global excludes file. Resolved once: `core.excludesFile` can point anywhere,
    /// so it is asked of git rather than guessed, with git's own default as the fallback.</summary>
    private static readonly Lazy<string> GlobalExcludesFile = new(() =>
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "config --global core.excludesFile")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            var configured = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(2000)) { return null; }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (configured.Length > 0)
            {
                return configured.StartsWith("~/", StringComparison.Ordinal)
                        ? Path.Combine(home, configured.Substring(2).Replace('/', Path.DirectorySeparatorChar))
                        : configured;
            }
            return Path.Combine(home, ".config", "git", "ignore");
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("GitIgnoreCache.GlobalExcludesFile", ex);
            return null;
        }
    });
}

/// <summary>
/// Lightweight `.gitignore` matcher: plain names, anchored <c>/</c>, dir-only trailing <c>/</c>,
/// basic <c>*</c>/<c>?</c> globs. Skips negations and brace expansions — rare enough that a full
/// gitignore implementation would not pay for itself here.
/// </summary>
internal sealed class GitIgnore
{
    private readonly List<Pattern> _patterns;

    private GitIgnore(List<Pattern> patterns) => _patterns = patterns;

    public static GitIgnore Parse(string content)
    {
        var patterns = new List<Pattern>();
        foreach (var raw in content.Split(['\n'], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') { continue; }
            if (line[0] == '!') { continue; }                 // negations: skip
            if (line.IndexOfAny(['{', '}', ',']) >= 0) { continue; } // braces: skip

            var dirOnly = false;
            if (line.EndsWith("/", StringComparison.Ordinal))
            {
                dirOnly = true;
                line = line.Substring(0, line.Length - 1);
            }

            var anchored = false;
            if (line.StartsWith("/", StringComparison.Ordinal))
            {
                anchored = true;
                line = line.Substring(1);
            }

            if (line.Length == 0) { continue; }

            patterns.Add(new Pattern
            {
                Anchored = anchored,
                DirOnly = dirOnly,
                Regex = GlobToRegex(line),
            });
        }
        return new GitIgnore(patterns);
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> (an absolute path) matches
    /// any non-negated pattern. Comparison is case-insensitive (Windows).
    /// </summary>
    public bool Matches(string fullPath, string root, bool isDirectory)
    {
        var rel = PathHelpers.Relative(root, fullPath);
        if (rel.Length == 0) { return false; }

        // Each path segment is also a candidate for unanchored patterns
        // (so `node_modules` matches `src/foo/node_modules`).
        var segments = rel.Split('/');

        foreach (var p in _patterns)
        {
            if (p.DirOnly && !isDirectory) { continue; }

            if (p.Anchored)
            {
                if (p.Regex.IsMatch(rel)) { return true; }
            }
            else
            {
                if (p.Regex.IsMatch(rel)) { return true; }
                foreach (var seg in segments)
                {
                    if (p.Regex.IsMatch(seg)) { return true; }
                }
            }
        }
        return false;
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('^');
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            // `**` spans separators where `*` stops at one — `**/.claude/settings.local.json` has
            // to reach a file at any depth. Written as an optional group so the pattern also
            // matches at the root, where there is no directory before it to consume.
            if (ch == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; }
                sb.Append("(?:.*/)?");
                continue;
            }
            switch (ch)
            {
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]"); break;
                case '.':
                case '+':
                case '(':
                case ')':
                case '|':
                case '^':
                case '$':
                case '{':
                case '}':
                case '[':
                case ']':
                case '\\':
                    sb.Append('\\').Append(ch); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private sealed class Pattern
    {
        public bool Anchored;
        public bool DirOnly;
        public Regex Regex;
    }
}
