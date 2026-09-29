/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>Parsed `.gitignore` files for the `@` picker, re-read only when they change.</summary>
internal static class GitIgnoreCache
{
    private static readonly Dictionary<string, (DateTime Mtime, GitIgnore Ignore)> _cache
        = new(StringComparer.OrdinalIgnoreCase);
    // Keyed by the .gitignore's own path: a nested file is read once and reused across listings.
    private static readonly Dictionary<string, (DateTime Mtime, GitIgnore Ignore)> _nested
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>The workspace's own `.gitignore` plus git's global excludes, or null when neither
    /// exists.</summary>
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
            if (_cache.TryGetValue(root, out var cached) && cached.Mtime == mtime) { return cached.Ignore; }
            try
            {
                // git's global excludes hold personal rules like `**/.claude/settings.local.json`:
                // a file hidden from the repo has no business showing up in the picker either.
                var text = hasLocal ? File.ReadAllText(path) : "";
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

    private static DateTime _configuredMtime = DateTime.MaxValue;
    private static GitIgnore _configured;

    /// <summary>The picker's own rules (<see cref="IgnoreRulesStore"/>). Keyed on the file's mtime
    /// so editing it in VS takes effect on the next `@` without a restart.</summary>
    public static GitIgnore GetConfigured()
    {
        var mtime = IgnoreRulesStore.LastWriteUtc;
        lock (_lock)
        {
            if (_configuredMtime == mtime) { return _configured; }
            try
            {
                var text = IgnoreRulesStore.Read();
                _configured = string.IsNullOrWhiteSpace(text) ? null : GitIgnore.Parse(text);
                _configuredMtime = mtime;
                return _configured;
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("GitIgnoreCache.GetConfigured", ex);
                return null;
            }
        }
    }

    /// <summary>The `.gitignore` sitting in <paramref name="dir"/>, or null when there is none. Git's
    /// global excludes are not folded in here: they belong to the workspace once.</summary>
    public static GitIgnore GetNested(string dir)
    {
        var path = Path.Combine(dir, ".gitignore");
        if (!File.Exists(path)) { return null; }

        var mtime = File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_nested.TryGetValue(path, out var cached) && cached.Mtime == mtime) { return cached.Ignore; }
            try
            {
                var ignore = GitIgnore.Parse(File.ReadAllText(path));
                _nested[path] = (mtime, ignore);
                return ignore;
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("GitIgnoreCache.GetNested", ex);
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
/// <c>*</c>/<c>?</c>/<c>**</c> globs, negations, and character classes. Skips brace expansions —
/// rare enough that a full gitignore implementation would not pay for itself here.
/// <para>Paths are relative to the directory the rules came from, forward-slashed.</para>
/// </summary>
internal sealed class GitIgnore
{
    // Exclusions are bucketed rather than tried one regex per rule: a path no rule excludes — the
    // common case, and the one that pays for every rule — costs a few hash lookups and at most two
    // regex runs instead of ~250. Measured on 100,000 files: ~11 s per rule, ~1 s bucketed and
    // walked in parallel.
    private readonly Bucket _any = new();
    private readonly Bucket _dirOnly = new();
    // Few, and the last one that matches decides, so they stay one regex each.
    private readonly List<Pattern> _negations;
    // Literal leading directories of the negations, e.g. `.vscode` for `!.vscode/settings.json`.
    private readonly List<string> _negatedPrefixes;

    private GitIgnore(List<Pattern> patterns)
    {
        foreach (var p in patterns.Where(p => !p.Negate)) { (p.DirOnly ? _dirOnly : _any).Add(p); }
        _any.Seal();
        _dirOnly.Seal();
        _negations = [.. patterns.Where(p => p.Negate)];
        _negatedPrefixes = [.. _negations
            .Select(p => LiteralPrefix(p.Glob))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The part of a glob before its first wildcard, trimmed to whole path segments —
    /// <c>src/Foo/Debug/**</c> gives <c>src/Foo/Debug</c>. Empty when the glob starts with a
    /// wildcard, which is the "could be anywhere" case and prunes nothing.</summary>
    private static string LiteralPrefix(string glob)
    {
        var wildcard = glob.IndexOfAny(['*', '?', '[']);
        var head = wildcard < 0 ? glob : glob.Substring(0, wildcard);
        var lastSlash = head.LastIndexOf('/');
        return wildcard < 0 ? head : (lastSlash < 0 ? "" : head.Substring(0, lastSlash));
    }

    /// <summary>True when a negation could re-include something inside <paramref name="rel"/>, so
    /// the directory must be walked even though a rule excludes it: git resolves `.vscode/*` plus
    /// `!.vscode/settings.json` file by file, and a pruned directory would take the file with it.</summary>
    public bool KeepsSubtree(string rel)
    {
        foreach (var prefix in _negatedPrefixes)
        {
            // Either the directory contains the negation (`.vscode` for `.vscode/settings.json`)
            // or it is inside one (`Mcp/Tools/Debug/Sub` for `Mcp/Tools/Debug/**`).
            if (prefix.StartsWith(rel, StringComparison.OrdinalIgnoreCase)
                && (prefix.Length == rel.Length || prefix[rel.Length] == '/'))
            {
                return true;
            }
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (rel.Length == prefix.Length || rel[prefix.Length] == '/'))
            {
                return true;
            }
        }
        return false;
    }

    public static GitIgnore Parse(string content)
    {
        var patterns = new List<Pattern>();
        foreach (var raw in content.Split(['\n'], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') { continue; }
            // Kept rather than skipped: this repository's `[Dd]ebug/` + `!…/Mcp/Tools/Debug/**`
            // only give the right answer together.
            var negate = line[0] == '!';
            if (negate) { line = line.Substring(1); }
            if (line.IndexOfAny(['{', '}', ',']) >= 0) { continue; } // braces: skip

            // git: a separator anywhere but at the end ties the rule to this file's folder. Read
            // before `/*` is stripped below, or `logs/*` would turn into a bare `logs` matching a
            // folder of that name at any depth.
            var innerSlash = line.TrimEnd('/').IndexOf('/') >= 0;

            // `dir/*` and `dir/**` mean "everything inside dir", so the rule is really about dir:
            // the walk asks about the directory itself to decide whether to descend.
            if (line.EndsWith("/**", StringComparison.Ordinal)) { line = line.Substring(0, line.Length - 3); }
            else if (line.EndsWith("/*", StringComparison.Ordinal)) { line = line.Substring(0, line.Length - 2); }

            var dirOnly = false;
            if (line.EndsWith("/", StringComparison.Ordinal))
            {
                dirOnly = true;
                line = line.Substring(0, line.Length - 1);
            }

            // An anchored rule must be matched against the PATH, never against a bare name —
            // `/build/` compared by name would hide Mcp/Tools/Build/ like the unanchored form.
            var anchored = line.StartsWith("/", StringComparison.Ordinal);
            if (anchored) { line = line.Substring(1); }

            if (line.Length == 0) { continue; }

            // A malformed character class would throw out of the Regex constructor and take the
            // whole file with it; dropping the one rule leaves the rest working.
            string regexText;
            Regex regex;
            try
            {
                regexText = GlobToRegex(line);
                regex = new Regex("^" + regexText + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                OutputWindowLogger.Global.Warn($"[picker] skipped .gitignore pattern '{raw.Trim()}': {ex.Message}");
                continue;
            }

            patterns.Add(new Pattern
            {
                DirOnly = dirOnly,
                Negate = negate,
                // A pattern with no separator matches a NAME at any depth, one with a separator
                // matches the path. An anchored rule is a path rule even with no separator left.
                NameOnly = !anchored && !innerSlash,
                Glob = line,
                RegexText = regexText,
                Regex = regex,
            });
        }
        return new GitIgnore(patterns);
    }

    /// <summary>The verdict of THIS file's rules, and whether it has one at all. False when no rule
    /// here mentions the path, which lets nested files be consulted deepest first: a file that says
    /// nothing hands the question to the one above it, while a `!` here answers "not ignored" and
    /// stops the search. Comparison is case-insensitive (Windows).</summary>
    public bool TryMatch(string rel, bool isDirectory, out bool ignored)
    {
        ignored = false;
        if (rel.Length == 0) { return false; }

        var slash = rel.LastIndexOf('/');
        var name = slash < 0 ? rel : rel.Substring(slash + 1);
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? null : name.Substring(dot);

        var matched = _any.IsMatch(name, extension, rel) || (isDirectory && _dirOnly.IsMatch(name, extension, rel));
        ignored = matched;
        // A nested file's `!` also has to answer for a path excluded further up, so negations are
        // checked whether or not something here excluded it.
        foreach (var p in _negations)
        {
            if (p.DirOnly && !isDirectory) { continue; }
            if (p.Regex.IsMatch(p.NameOnly ? name : rel)) { matched = true; ignored = false; break; }
        }
        return matched;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            // `**` spans separators where `*` stops at one. An optional group so the pattern also
            // matches at the root, where there is no directory before it to consume.
            if (ch == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; }
                sb.Append("(?:.*/)?");
                continue;
            }
            // `[Dd]ebug` is a character class in a glob exactly as it is in a regex.
            if (ch == '[')
            {
                var close = IndexOfClassEnd(glob, i);
                // Unterminated: git treats the bracket as an ordinary character, so escape it.
                if (close < 0) { sb.Append("\\["); continue; }
                var body = glob.Substring(i + 1, close - i - 1);
                // git negates a class with '!', .NET with '^'.
                if (body.Length > 0 && body[0] == '!') { body = "^" + body.Substring(1); }
                // A class must not match a separator.
                sb.Append("(?!/)[").Append(body).Append(']');
                i = close;
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
                case ']':
                case '\\':
                    sb.Append('\\').Append(ch); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Index of the <c>]</c> closing the class opened at <paramref name="open"/>, or -1
    /// when there is none. A <c>]</c> in first position (after an optional negator) is a literal
    /// member of the class, not its end — <c>[]]</c> matches a bracket.</summary>
    private static int IndexOfClassEnd(string glob, int open)
    {
        var i = open + 1;
        if (i < glob.Length && (glob[i] == '!' || glob[i] == '^')) { i++; }
        if (i < glob.Length && glob[i] == ']') { i++; }
        return glob.IndexOf(']', i);
    }

    // `[Dd]` compared case-insensitively is just `d`: folding it lets `[Dd]ebug/` land in the
    // name set instead of the regex.
    private static readonly Regex TwoCaseClass = new(@"\[([A-Za-z])([A-Za-z])\]", RegexOptions.CultureInvariant);

    /// <summary>Exclusions of one kind (any path, or directories only), split by how cheaply they
    /// can be answered.</summary>
    private sealed class Bucket
    {
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _suffixes = [];
        // `**/a/b` with nothing else wild: the path is `a/b` or ends in `/a/b`. Kept out of the regex,
        // where each `(?:.*/)?` would be retried at every slash of every path.
        private readonly List<string> _pathTails = [];
        private readonly List<string> _nameParts = [];
        private readonly List<string> _pathParts = [];
        private Regex _nameRegex;
        private Regex _pathRegex;

        public void Add(Pattern p)
        {
            if (p.NameOnly)
            {
                var glob = TwoCaseClass.Replace(p.Glob, m =>
                    char.ToLowerInvariant(m.Groups[1].Value[0]) == char.ToLowerInvariant(m.Groups[2].Value[0]) ? m.Groups[1].Value : m.Value);
                var wildcard = glob.IndexOfAny(['*', '?', '[']);
                if (wildcard < 0) { _names.Add(glob); return; }
                if (wildcard == 0 && glob[0] == '*' && glob.IndexOfAny(['*', '?', '['], 1) < 0)
                {
                    var suffix = glob.Substring(1);
                    // `*.log` is an extension; `*.sln.docstates` or `*_i.c` a plain suffix.
                    if (suffix.Length > 1 && suffix[0] == '.' && suffix.IndexOf('.', 1) < 0) { _extensions.Add(suffix); }
                    else { _suffixes.Add(suffix); }
                    return;
                }
            }
            else if (p.Glob.StartsWith("**/", StringComparison.Ordinal) && p.Glob.IndexOfAny(['*', '?', '['], 3) < 0)
            {
                if (!_pathTails.Contains(p.Glob.Substring(3), StringComparer.OrdinalIgnoreCase)) { _pathTails.Add(p.Glob.Substring(3)); }
                return;
            }
            (p.NameOnly ? _nameParts : _pathParts).Add(p.RegexText);
        }

        // Compiled: one regex per bucket, run for every path of every listing, so the IL is
        // amortised — unlike ~250 per-rule regexes, each run a few times.
        public void Seal()
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
            // Distinct: a global excludes file holding the same line 32 times (seen in the wild) would
            // otherwise make every path pay for 32 identical alternatives.
            if (_nameParts.Count > 0) { _nameRegex = new Regex("^(?:" + string.Join("|", _nameParts.Distinct()) + ")$", options); }
            if (_pathParts.Count > 0) { _pathRegex = new Regex("^(?:" + string.Join("|", _pathParts.Distinct()) + ")$", options); }
        }

        public bool IsMatch(string name, string extension, string rel)
        {
            if (_names.Contains(name)) { return true; }
            if (extension != null && _extensions.Contains(extension)) { return true; }
            foreach (var suffix in _suffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            foreach (var tail in _pathTails)
            {
                if (rel.Length == tail.Length
                        ? rel.Equals(tail, StringComparison.OrdinalIgnoreCase)
                        : rel.Length > tail.Length && rel[rel.Length - tail.Length - 1] == '/' && rel.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (_nameRegex != null && _nameRegex.IsMatch(name)) { return true; }
            return _pathRegex != null && _pathRegex.IsMatch(rel);
        }
    }

    private sealed class Pattern
    {
        public bool DirOnly;
        /// <summary>The glob carries no separator, so it matches a bare name at any depth.</summary>
        public bool NameOnly;
        /// <summary>A <c>!</c> rule: matching re-includes the path instead of ignoring it.</summary>
        public bool Negate;
        /// <summary>The glob as parsed, prefixes stripped; its literal head decides which
        /// directories a negation keeps walkable.</summary>
        public string Glob;
        public string RegexText;
        public Regex Regex;
    }
}
