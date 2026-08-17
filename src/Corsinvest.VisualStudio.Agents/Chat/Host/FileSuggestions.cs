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
        using var _ = OutputWindowLogger.Global.PerfSpan($"FileSuggestions.Get(q={query?.Length ?? 0} chars)");
        var result = new List<FileSuggestionItem>();
        try
        {
            if (!Directory.Exists(root)) { return result; }

            var opts = AgentsOptions.Chat;
            // The picker's own rules, read from their file and parsed by the same .gitignore
            // parser as the workspace's — one syntax, and the one already written in every repo.
            var configured = GitIgnoreCache.GetConfigured();
            // The workspace rules are the bottom of the stack; EnumerateFiles pushes a nested
            // .gitignore onto it for the subtree that declares one.
            var ignores = new List<(string Root, GitIgnore Ignore)>();
            var ignore = opts.UseGitIgnore ? GitIgnoreCache.Get(root) : null;
            if (ignore != null) { ignores.Add((root, ignore)); }

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
            // Which budget ended the walk, if either — the alphabetical cut it implies is why a
            // folder late in the alphabet can be missing entirely.
            string cappedBy = null;

            foreach (var file in EnumerateFiles(root, root, configured, ignores))
            {
                // This runs on every keystroke, so the walk is bounded by how much of the tree it
                // touches, not by how deep it goes: depth is what hides files, sheer volume is what
                // costs. Reached only by trees far past the point where a flat list is any use.
                // One budget for every row, files and derived folders alike, and the walk stops when
                // it is gone. Counting only files let folders keep coming after the cut, leaving a
                // tail of directories whose contents had all been dropped — `tools/cli-probe/` and
                // nothing under it. Alphabetical order means the cut lands wherever it lands, which
                // is the trade any capped file picker makes.
                if (hits.Count >= MaxRows || ++visited > MaxVisited)
                {
                    cappedBy = hits.Count >= MaxRows ? nameof(MaxRows) : nameof(MaxVisited);
                    break;
                }

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

            // capped is the one worth reporting: it says the list is a prefix of the alphabet
            // rather than the whole tree, which is how a folder late in the alphabet goes missing
            // with nothing else to show for it.
            OutputWindowLogger.Global.Perf(() =>
                $"[picker] rows={result.Count} visited={visited} capped={cappedBy ?? "no"}");
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("FileSuggestions.Get", ex); }
        return result;
    }

    /// <summary>Rows shown, files and derived directories together. Deliberately generous: the walk
    /// is alphabetical, so a low cap does not sample the tree, it stops partway through and hides
    /// everything from there to the end of the alphabet — at 600 this repository's own `tools/` was
    /// cut, 31 rows short of the 631 it produces. Bounded all the same: cv-popover-list renders
    /// every row it is given, with no virtualisation.
    /// <para>Affordable at this size only because the ignore rules now prune what they always meant
    /// to: the same walk was 1,165 files and ~1.4s of pattern matching before the character classes
    /// were fixed, and is 563 files and ~190ms after.</para></summary>
    private const int MaxRows = 2000;

    /// <summary>Files the walk will look at before giving up, ignored ones excluded. Guards the
    /// per-keystroke cost on a huge tree; it is not a depth limit.</summary>
    private const int MaxVisited = 20000;

    /// <summary>Files under <paramref name="dir"/>, at any depth, ignored ones left out.
    /// Directories are pruned rather than filtered so their contents cost nothing.
    /// <para><paramref name="ignores"/> holds the workspace rules plus one entry for every
    /// <c>.gitignore</c> on the way down, each paired with the directory it was read from — its
    /// patterns are relative to that directory, not to the workspace. The walk is already
    /// recursive, so a nested file costs one lookup when its directory is entered rather than a
    /// search per path. Without this, `WebViewSrc/.gitignore` went unread and its `dist/` — named
    /// nowhere else — put 13 build artefacts in the menu.</para></summary>
    private static IEnumerable<string> EnumerateFiles(
        string dir, string root, GitIgnore configured, List<(string Root, GitIgnore Ignore)> ignores)
    {
        foreach (var file in Directory.GetFiles(dir).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            if (IsIgnored(file, root, isDir: false, configured, ignores)) { continue; }
            yield return file;
        }

        foreach (var sub in Directory.GetDirectories(dir).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            // An excluded directory is still walked when a `!` rule names something inside it:
            // `.vscode/*` with `!.vscode/settings.json` means the directory goes, its settings
            // file stays, and pruning here would take the file with it. The files inside are
            // filtered individually, so only what the negations rescue comes back.
            if (IsIgnored(sub, root, isDir: true, configured, ignores)
                && !ignores.Any(x => x.Ignore.KeepsSubtree(sub, x.Root))
                && configured?.KeepsSubtree(sub, root) != true)
            {
                continue;
            }

            // A .gitignore here applies to this subtree only, so it is pushed for the descent and
            // popped after: the list is shared down the recursion rather than copied per level.
            var nested = GitIgnoreCache.GetNested(sub);
            if (nested != null) { ignores.Add((sub, nested)); }
            foreach (var file in EnumerateFiles(sub, root, configured, ignores))
            {
                yield return file;
            }
            if (nested != null) { ignores.RemoveAt(ignores.Count - 1); }
        }
    }

    /// <summary>
    /// Tests a path against the workspace's ignore rules, then against the configured patterns.
    /// Both are read by the same parser, so there is one syntax here and it is git's.
    /// </summary>
    private static bool IsIgnored(string fullPath, string root, bool isDir, GitIgnore configured, List<(string Root, GitIgnore Ignore)> ignores)
    {
        // The workspace rules are asked FIRST, deepest file first: they are what this project says
        // about itself, while the configured patterns are a default for a project that says
        // nothing. Where the two disagree the specific answer is the right one — the defaults hide
        // `.vscode/`, which this repository re-includes a settings.json from on purpose.
        for (var i = ignores.Count - 1; i >= 0; i--)
        {
            if (ignores[i].Ignore.TryMatch(fullPath, ignores[i].Root, isDir, out var ignored)) { return ignored; }
        }

        // Nothing had an opinion, so the configured patterns decide. Skipping them inside a
        // repository — on the argument that git's silence means "tracked" — was measured on a
        // .gitignore with no node_modules rule: the walk listed 2,000 rows of dependencies and hit
        // the cap, which is the alphabetical truncation this whole change set removed. They are
        // the guard for exactly that, so they stay.
        return configured != null
            && configured.TryMatch(fullPath, root, isDir, out var byPattern)
            && byPattern;
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
    // Keyed by the .gitignore's own path: a nested file is read once and reused across walks,
    // which is what keeps the per-directory lookup from costing a parse each time.
    private static readonly Dictionary<string, (DateTime Mtime, GitIgnore Ignore)> _nested
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

    /// <summary>The picker's own rules (<see cref="IgnoreRulesStore"/>), parsed as a `.gitignore`.
    /// Keyed on the file's mtime so editing it in VS takes effect on the next `@` without a
    /// restart, while an unchanged file costs one stat per walk.</summary>
    private static DateTime _configuredMtime = DateTime.MaxValue;
    private static GitIgnore _configured;

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

    /// <summary>The `.gitignore` sitting in <paramref name="dir"/>, or null when there is none —
    /// git's global excludes are NOT folded in here, unlike <see cref="Get"/>: those belong to the
    /// workspace once, and re-applying them at every level would only repeat work. Called once per
    /// directory the walk enters, so the absent case (the overwhelming majority) is one
    /// File.Exists.</summary>
    public static GitIgnore GetNested(string dir)
    {
        var path = Path.Combine(dir, ".gitignore");
        if (!File.Exists(path)) { return null; }

        var mtime = File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_nested.TryGetValue(path, out var cached) && cached.Mtime == mtime)
            {
                return cached.Ignore;
            }
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
/// basic <c>*</c>/<c>?</c> globs, negations, and character classes. Skips brace expansions — rare
/// enough that a full gitignore implementation would not pay for itself here.
/// </summary>
internal sealed class GitIgnore
{
    // Split at construction rather than filtered per path: the exclusions answer almost every
    // call on their own, and a path they don't match never touches the negation list at all.
    private readonly List<Pattern> _excludes;
    private readonly List<Pattern> _negations;
    // Literal leading directories of the negations, e.g. `.vscode` for `!.vscode/settings.json`.
    // Read by KeepsSubtree to tell a directory that must be descended into from one that can be
    // pruned; empty for the overwhelming majority of files, where the test costs nothing.
    private readonly List<string> _negatedPrefixes;

    private GitIgnore(List<Pattern> patterns)
    {
        _excludes = patterns.Where(p => !p.Negate).ToList();
        _negations = patterns.Where(p => p.Negate).ToList();
        _negatedPrefixes = _negations
            .Select(p => LiteralPrefix(p.Glob))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
    /// the directory must be walked even though a rule excludes it.
    /// <para>git resolves `.vscode/*` plus `!.vscode/settings.json` by listing the directory and
    /// filtering file by file; this walk prunes instead, and a pruned directory takes its
    /// re-included files with it. Only directories that actually sit on a negation's path pay the
    /// extra descent.</para></summary>
    public bool KeepsSubtree(string fullPath, string root)
    {
        if (_negatedPrefixes.Count == 0) { return false; }
        var rel = PathHelpers.Relative(root, fullPath);
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
            // A '!' re-includes what an earlier rule excluded. Kept rather than skipped because
            // the two halves of this repository's own `[Dd]ebug/` + `!…/Mcp/Tools/Debug/**` pair
            // only give the right answer together: honouring the first without the second hides
            // 32 tracked source files.
            var negate = line[0] == '!';
            if (negate) { line = line.Substring(1); }
            if (line.IndexOfAny(['{', '}', ',']) >= 0) { continue; } // braces: skip

            // `dir/*` and `dir/**` mean "everything inside dir", so the rule is really about dir:
            // git never lists an ignored directory, while this walk asks about the directory
            // itself to decide whether to descend. Left as written, `**/[Bb]in/*` matched neither
            // the `bin` directory nor anything below its first level, and 450 build outputs of
            // 1,165 listed files came from that one rule.
            if (line.EndsWith("/**", StringComparison.Ordinal)) { line = line.Substring(0, line.Length - 3); }
            else if (line.EndsWith("/*", StringComparison.Ordinal)) { line = line.Substring(0, line.Length - 2); }

            var dirOnly = false;
            if (line.EndsWith("/", StringComparison.Ordinal))
            {
                dirOnly = true;
                line = line.Substring(0, line.Length - 1);
            }

            // A leading '/' anchors the pattern to the root. Stripping it is not enough: what
            // decides is that the rule must then be matched against the PATH, never against a
            // bare name — `/build/` compared by name would hide Mcp/Tools/Build/ exactly as the
            // unanchored form does, which is the difference the anchor exists to express.
            var anchored = line.StartsWith("/", StringComparison.Ordinal);
            if (anchored) { line = line.Substring(1); }

            if (line.Length == 0) { continue; }

            // A malformed character class would throw out of the Regex constructor and take the
            // whole file with it; dropping the one rule leaves the rest working.
            Regex regex;
            try { regex = GlobToRegex(line); }
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
                // matches the path. git's own rule, and what decides which string to test below.
                // An anchored rule is a path rule even when it has no separator left in it.
                NameOnly = !anchored && line.IndexOf('/') < 0,
                Glob = line,
                Regex = regex,
            });
        }
        return new GitIgnore(patterns);
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> (an absolute path) is ignored: the LAST rule that
    /// matches decides, so a later <c>!</c> re-includes what an earlier rule excluded — git's own
    /// precedence. Comparison is case-insensitive (Windows).
    /// </summary>
    public bool Matches(string fullPath, string root, bool isDirectory)
    {
        var rel = PathHelpers.Relative(root, fullPath);
        if (rel.Length == 0) { return false; }

        // One test per rule, against the string that rule is about: the name for a
        // separator-less pattern, the relative path otherwise.
        //
        // A name pattern used to be tried against every path segment as well, so that
        // `node_modules` would match `src/foo/node_modules`. That was reachable only for a path
        // whose ANCESTOR matched, and the walk prunes an ignored directory before descending into
        // it (FileSuggestions.EnumerateFiles) — so those segments had already been ruled out by the
        // time this saw them.
        //
        // The exclusions are walked first and stop at the first match, which is the common case:
        // a path nothing excludes is the one that ends up in the list, and it costs one pass.
        // Only a path that IS excluded pays for the negations — 11 rules of 273 here — and only
        // the LAST rule that matches decides, so those are walked in full.
        return TryMatch(fullPath, root, isDirectory, out var ignored) && ignored;
    }

    /// <summary>The verdict of THIS file's rules, and whether it has one at all. False when no rule
    /// here mentions the path, which is what lets a stack of nested files be consulted deepest
    /// first: a file that says nothing hands the question to the one above it, while a `!` here
    /// answers "not ignored" and stops the search rather than falling through to a parent that
    /// would exclude it.</summary>
    public bool TryMatch(string fullPath, string root, bool isDirectory, out bool ignored)
    {
        ignored = false;
        var rel = PathHelpers.Relative(root, fullPath);
        if (rel.Length == 0) { return false; }

        var name = NameOf(rel);

        var matched = false;
        foreach (var p in _excludes)
        {
            if (p.DirOnly && !isDirectory) { continue; }
            if (p.Regex.IsMatch(p.NameOnly ? name : rel)) { matched = true; ignored = true; break; }
        }
        // Negations are only consulted for a path something excluded — except that a nested file's
        // `!` also has to answer for a path excluded further up, so they are checked either way.
        foreach (var p in _negations)
        {
            if (p.DirOnly && !isDirectory) { continue; }
            if (p.Regex.IsMatch(p.NameOnly ? name : rel)) { matched = true; ignored = false; break; }
        }
        return matched;
    }

    /// <summary>The last segment of a forward-slashed relative path.</summary>
    private static string NameOf(string rel)
    {
        var slash = rel.LastIndexOf('/');
        return slash < 0 ? rel : rel.Substring(slash + 1);
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
            // `[Dd]ebug` is a character class in a glob exactly as it is in a regex, so it goes
            // through rather than being escaped. Escaping it was why every bracketed rule in the
            // stock VisualStudio .gitignore — [Dd]ebug/, [Rr]elease/, [Bb]in, [Oo]bj — was inert:
            // they compiled to the literal text "[Oo]bj", which names no directory, so 615 build
            // artefacts of 1,165 listed files were offered by the picker.
            if (ch == '[')
            {
                var close = IndexOfClassEnd(glob, i);
                // Unterminated: git treats the bracket as an ordinary character, so escape it.
                if (close < 0) { sb.Append("\\["); continue; }
                var body = glob.Substring(i + 1, close - i - 1);
                // git negates a class with '!', .NET with '^'.
                if (body.Length > 0 && body[0] == '!') { body = "^" + body.Substring(1); }
                // A class must not match a separator: a path is compared whole, and `[Bb]in` has
                // no business matching the '/' that ends a directory name.
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
        sb.Append('$');
        // Interpreted, not Compiled: these are ~270 DIFFERENT patterns, each run a few hundred
        // times per walk, so the IL each Compiled regex emits on first use is never amortised —
        // it is paid once per pattern and thrown away with the cache.
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
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

    private sealed class Pattern
    {
        public bool DirOnly;
        /// <summary>The glob carries no separator, so it matches a bare name at any depth.</summary>
        public bool NameOnly;
        /// <summary>A <c>!</c> rule: matching re-includes the path instead of ignoring it.</summary>
        public bool Negate;
        /// <summary>The glob as parsed, prefixes stripped. Kept for the negations, whose literal
        /// head decides which directories cannot be pruned.</summary>
        public string Glob;
        public Regex Regex;
    }
}
