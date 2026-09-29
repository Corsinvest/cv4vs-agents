/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Corsinvest.VisualStudio.Agents.Chat.Host;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The picker's `.gitignore` reading. Matching is bucketed for speed — literal names and
/// extensions in hash sets, the rest in one regex per bucket — and must answer exactly as trying
/// every rule in turn did, which the reference below still does.</summary>
public class GitIgnoreTests
{
    private static string RepoGitIgnore()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cv4vs-agents.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, ".gitignore"));
    }

    private static readonly (string Rel, bool IsDir)[] Paths =
    [
        ("bin", true), ("src/Foo/bin", true), ("src/Foo/bin/x.dll", false), ("Bin", true), ("binder.cs", false),
        ("obj", true), ("Debug", true), ("debug", true), ("x64/Debug", true), ("Release", true), ("src/Release.cs", false),
        ("a.suo", false), ("A.SUO", false), ("b.user", false), ("c.sln.docstates", false), ("x_i.c", false), ("x_p.c", false),
        ("TestResult.xml", false), ("TestResults", true), ("BuildLog.htm", false), ("nunit-1.xml", false),
        ("packages", true), ("packages/build", true), ("src/packages/x.nupkg", false), ("x.nupkg", false),
        (".vs", true), (".vscode", true), (".vscode/settings.json", false), (".vscode/launch.json", false),
        ("node_modules", true), ("src/node_modules", true), ("dist", true), ("build", true), ("src/build", true),
        ("out", true), ("target", true), ("a.log", false), ("a.tmp", false), ("x.exe", false), ("x.pdb", false),
        ("src/Corsinvest.VisualStudio.Agents/Mcp/Tools/Debug", true), ("src/Corsinvest.VisualStudio.Agents/Mcp/Tools/Debug/DebugStep.cs", false),
        ("docs/internal", true), ("docs/internal/TODO.md", false), ("docs/options.md", false),
        ("Program.cs", false), ("src/App/App.csproj", false), ("README.md", false), (".editorconfig", false),
        ("__pycache__", true), ("src/__pycache__", true), (".DS_Store", false), ("Thumbs.db", false), (".env", false),
        ("mono_crash.1.json", false), ("Generated Files", true), ("_ReSharper.Caches", true), ("x.VisualState.xml", false),
        (".claude/settings.local.json", false), ("a/b/.claude/settings.local.json", false), ("xclaude/settings.local.json", false),
        (".fake", true), ("a/.fake", true), ("a/.paket/paket.exe", false), (".paket/paket.exe", false), ("b.paket/paket.exe", false),
    ];

    [Fact]
    public void Bucketed_answers_equal_per_rule_answers()
    {
        var content = RepoGitIgnore() + "\n" + IgnoreRulesStore.Defaults
                      + "\n**/.claude/settings.local.json\n**/.claude/settings.local.json\n**/.paket/paket.exe\n";
        var fast = GitIgnore.Parse(content);
        var reference = PerRule.Parse(content);
        foreach (var (rel, isDir) in Paths)
        {
            var expectedMatched = reference.TryMatch(rel, isDir, out var expectedIgnored);
            var matched = fast.TryMatch(rel, isDir, out var ignored);
            Assert.True((expectedMatched, expectedIgnored) == (matched, ignored), $"{rel} (dir={isDir}): expected {(expectedMatched, expectedIgnored)}, got {(matched, ignored)}");
        }
    }

    [Theory]
    [InlineData("node_modules/", "node_modules", true, true)]
    [InlineData("node_modules/", "node_modules", false, false)]      // dir-only rule, a file of that name
    [InlineData("*.log", "src/a.log", false, true)]                   // name pattern at any depth
    [InlineData("*.log", "src/a.log.txt", false, false)]
    [InlineData("*_i.c", "x_i.c", false, true)]                       // suffix
    [InlineData("[Dd]ebug/", "DEBUG", true, true)]                    // two-case class, case-insensitive anyway
    [InlineData("/build/", "build", true, true)]                      // anchored: the root one
    [InlineData("/build/", "src/build", true, false)]                 // anchored: not a nested one
    [InlineData("docs/internal/", "docs/internal", true, true)]       // path pattern
    [InlineData("**/.fake/", "a/b/.fake", true, true)]                // ** spans directories
    [InlineData("**/.fake/", ".fake", true, true)]                    // … and matches at the root
    public void Single_rule(string rule, string rel, bool isDir, bool ignored)
        => Assert.Equal(ignored, GitIgnore.Parse(rule).TryMatch(rel, isDir, out var i) && i);

    [Fact]
    public void A_negation_re_includes_and_keeps_its_directory_walkable()
    {
        var g = GitIgnore.Parse(".vscode/*\n!.vscode/settings.json\n");
        Assert.True(g.TryMatch(".vscode", true, out var dirIgnored) && dirIgnored);
        Assert.True(g.KeepsSubtree(".vscode"));
        Assert.True(g.TryMatch(".vscode/settings.json", false, out var s) && !s);
        Assert.False(g.KeepsSubtree("src"));
    }

    [Fact]
    public void Malformed_and_brace_patterns_are_skipped()
    {
        var g = GitIgnore.Parse("[abc\n{a,b}.cs\n*.log\n");
        Assert.True(g.TryMatch("x.log", false, out var i) && i);
        Assert.False(g.TryMatch("a.cs", false, out _));
    }

    [Fact]
    public void Duplicate_rules_still_match()
    {
        var g = GitIgnore.Parse(string.Concat(Enumerable.Repeat("**/.claude/settings.local.json\n", 32)));
        Assert.True(g.TryMatch("x/.claude/settings.local.json", false, out var i) && i);
        Assert.False(g.TryMatch("x/claude/settings.local.json", false, out _));
    }

    [Fact]
    public void A_path_no_rule_mentions_has_no_verdict()
        => Assert.False(GitIgnore.Parse("*.log\n").TryMatch("Program.cs", false, out _));

    /// <summary>The matching as it was before bucketing: every rule its own regex, tried in turn.</summary>
    private sealed class PerRule
    {
        private readonly List<(bool DirOnly, bool NameOnly, Regex Regex)> _excludes = [], _negations = [];

        public static PerRule Parse(string content)
        {
            var r = new PerRule();
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') { continue; }
                var negate = line[0] == '!';
                if (negate) { line = line.Substring(1); }
                if (line.IndexOfAny(['{', '}', ',']) >= 0) { continue; }
                var innerSlash = line.TrimEnd('/').IndexOf('/') >= 0;
                if (line.EndsWith("/**", StringComparison.Ordinal)) { line = line.Substring(0, line.Length - 3); }
                else if (line.EndsWith("/*", StringComparison.Ordinal)) { line = line.Substring(0, line.Length - 2); }
                var dirOnly = line.EndsWith("/", StringComparison.Ordinal);
                if (dirOnly) { line = line.Substring(0, line.Length - 1); }
                var anchored = line.StartsWith("/", StringComparison.Ordinal);
                if (anchored) { line = line.Substring(1); }
                if (line.Length == 0) { continue; }
                Regex regex;
                try { regex = new Regex("^" + Glob(line) + "$", RegexOptions.IgnoreCase); }
                catch (ArgumentException) { continue; }
                (negate ? r._negations : r._excludes).Add((dirOnly, !anchored && !innerSlash, regex));
            }
            return r;
        }

        public bool TryMatch(string rel, bool isDir, out bool ignored)
        {
            ignored = false;
            var slash = rel.LastIndexOf('/');
            var name = slash < 0 ? rel : rel.Substring(slash + 1);
            var matched = false;
            foreach (var p in _excludes)
            {
                if (p.DirOnly && !isDir) { continue; }
                if (p.Regex.IsMatch(p.NameOnly ? name : rel)) { matched = true; ignored = true; break; }
            }
            foreach (var p in _negations)
            {
                if (p.DirOnly && !isDir) { continue; }
                if (p.Regex.IsMatch(p.NameOnly ? name : rel)) { matched = true; ignored = false; break; }
            }
            return matched;
        }

        private static string Glob(string glob)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < glob.Length; i++)
            {
                var ch = glob[i];
                if (ch == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; }
                    sb.Append("(?:.*/)?");
                    continue;
                }
                if (ch == '[')
                {
                    var j = i + 1;
                    if (j < glob.Length && (glob[j] == '!' || glob[j] == '^')) { j++; }
                    if (j < glob.Length && glob[j] == ']') { j++; }
                    var close = glob.IndexOf(']', j);
                    if (close < 0) { sb.Append("\\["); continue; }
                    var body = glob.Substring(i + 1, close - i - 1);
                    if (body.Length > 0 && body[0] == '!') { body = "^" + body.Substring(1); }
                    sb.Append("(?!/)[").Append(body).Append(']');
                    i = close;
                    continue;
                }
                sb.Append(ch switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    '.' or '+' or '(' or ')' or '|' or '^' or '$' or '{' or '}' or ']' or '\\' => "\\" + ch,
                    _ => ch.ToString(),
                });
            }
            return sb.ToString();
        }
    }
}
