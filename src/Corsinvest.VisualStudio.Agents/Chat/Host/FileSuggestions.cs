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

            var q = query.Replace('\\', '/');
            var qLower = q.ToLowerInvariant();

            var searchDir = root;
            var namePart = qLower;
            var lastSlash = q.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                var sub = q.Substring(0, lastSlash);
                searchDir = Path.Combine(root, sub.Replace('/', Path.DirectorySeparatorChar));
                namePart = qLower.Substring(lastSlash + 1);
            }

            if (!Directory.Exists(searchDir)) { return result; }

            var dirs = new List<FileSuggestionItem>();
            var files = new List<FileSuggestionItem>();

            foreach (var dir in Directory.GetDirectories(searchDir).OrderBy(a => a))
            {
                var name = Path.GetFileName(dir);
                if (IsIgnored(name, dir, root, isDir: true, patterns, ignore)) { continue; }
                if (!string.IsNullOrEmpty(namePart) && !name.ToLowerInvariant().Contains(namePart)) { continue; }
                var parentRel = PathHelpers.Relative(root, Path.GetDirectoryName(dir) ?? root);
                dirs.Add(new FileSuggestionItem
                {
                    Name = name + "/",
                    Path = dir,
                    Dir = parentRel,
                    IsDir = true,
                });
                if (dirs.Count >= 10) { break; }
            }

            // files: recursive only when searching with 3+ chars; flat (root + 1 subdir level) otherwise
            var deepSearch = namePart.Length >= 3;
            var allFiles = deepSearch
                            ? GetFilesRecursive(root, root, 4, patterns, ignore)
                            : GetFilesRecursive(searchDir, root, 1, patterns, ignore);

            foreach (var file in allFiles.OrderBy(a => a))
            {
                var name = Path.GetFileName(file);
                if (IsIgnored(name, file, root, isDir: false, patterns, ignore)) { continue; }
                if (!string.IsNullOrEmpty(namePart) && !name.ToLowerInvariant().Contains(namePart)) { continue; }
                var relDir = PathHelpers.Relative(root, Path.GetDirectoryName(file) ?? root);
                files.Add(new FileSuggestionItem
                {
                    Name = name,
                    Path = file,
                    Dir = relDir,
                    IsDir = false,
                });
                if (files.Count >= 30) { break; }
            }

            result.AddRange(dirs);
            result.AddRange(files);
        }
        catch (Exception ex) { OutputWindowLogger.LogException("FileSuggestions.Get", ex); }
        return result;
    }

    private static IEnumerable<string> GetFilesRecursive(string dir, string root, int depth, List<IgnorePattern> patterns, GitIgnore ignore)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            yield return file;
        }

        if (depth <= 0)
        {
            yield break;
        }

        foreach (var sub in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (IsIgnored(name, sub, root, isDir: true, patterns, ignore)) { continue; }
            foreach (var file in GetFilesRecursive(sub, root, depth - 1, patterns, ignore))
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
        if (!File.Exists(path)) { return null; }

        var mtime = File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_cache.TryGetValue(root, out var cached) && cached.Mtime == mtime)
            {
                return cached.Ignore;
            }

            try
            {
                var ignore = GitIgnore.Parse(File.ReadAllText(path));
                _cache[root] = (mtime, ignore);
                return ignore;
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("GitIgnoreCache.Get", ex);
                return null;
            }
        }
    }
}

/// <summary>
/// Lightweight `.gitignore` matcher: plain names, anchored <c>/</c>, dir-only trailing <c>/</c>,
/// basic <c>*</c>/<c>?</c> globs. Skips negations and brace expansions (as the VS Code extension does).
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
        foreach (var ch in glob)
        {
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
