/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.VisualStudio.Agents.Chat.Host;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The `@` picker's listing, against real folders: nested .gitignore files, the picker's
/// own rules at the lowest precedence, a file re-included under an excluded folder.</summary>
public class WorkspaceFileListerTests : IDisposable
{
    // Spaces and accents: the path goes to Win32 and the names come back from it.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cv4vs-wfl-" + Guid.NewGuid().ToString("N"), "dir with spaces àè");

    public WorkspaceFileListerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(@"\\?\" + Path.GetDirectoryName(_dir)!, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string rel, string content = "")
    {
        var path = Path.Combine(_dir, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<FileListing> List(string configured, bool useGitIgnore = true, CancellationToken ct = default)
        => WorkspaceFileLister.ListAsync(_dir, configured == null ? null : GitIgnore.Parse(configured), useGitIgnore, ct);

    private static string[] Sorted(FileListing listing) => [.. listing.Paths.OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public async Task Applies_gitignore_nested_gitignore_and_rules()
    {
        Write(".gitignore", "*.log\n.vscode/*\n!.vscode/settings.json\n");
        Write("src/sub/.gitignore", "secret.txt\n");
        Write("keep.cs"); Write("app.log"); Write("src/sub/secret.txt"); Write("src/sub/ok.cs");
        Write("node_modules/x/i.js"); Write("bin/out.dll"); Write("data.tmp");
        Write(".vscode/settings.json"); Write(".vscode/launch.json");

        var listing = await List("node_modules/\nbin/\n*.tmp\n.vscode/*\n");

        Assert.True(listing.Ok, listing.Failure);
        Assert.Equal(
            new[] { ".gitignore", ".vscode/settings.json", "keep.cs", "src/sub/.gitignore", "src/sub/ok.cs" },
            Sorted(listing));
    }

    [Fact]
    public async Task Without_gitignore_keeps_our_rules_only()
    {
        Write(".gitignore", "*.log\n");
        Write("app.log"); Write("data.tmp"); Write("keep.cs");

        var listing = await List("*.tmp\n", useGitIgnore: false);

        Assert.Equal(new[] { ".gitignore", "app.log", "keep.cs" }, Sorted(listing));
    }

    [Fact]
    public async Task Sibling_folders_apply_only_their_own_rules()
    {
        // Listed in parallel: each folder carries its own chain, none sees its sibling's.
        for (var i = 0; i < 20; i++)
        {
            Write($"a{i:00}/.gitignore", "*.tmp\n");
            Write($"a{i:00}/x.tmp"); Write($"b{i:00}/x.tmp");
        }

        var listing = await List(null);

        Assert.All(Enumerable.Range(0, 20), i =>
        {
            Assert.DoesNotContain($"a{i:00}/x.tmp", listing.Paths);
            Assert.Contains($"b{i:00}/x.tmp", listing.Paths);
        });
    }

    [Fact]
    public async Task Ignores_gitignore_files_above_the_workspace()
    {
        // A home folder that is a dotfiles repo, or a `.gitignore` in C:\src\, must not empty the picker.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dir)!, ".gitignore"), "*.txt\n");
        Write("a.txt"); Write("b.cs");

        Assert.Equal(new[] { "a.txt", "b.cs" }, Sorted(await List(null)));
    }

    [Fact]
    public async Task A_path_longer_than_260_characters_is_listed()
    {
        var deep = string.Join("/", Enumerable.Repeat("a-rather-long-folder-name", 12)) + "/deep.cs";
        Directory.CreateDirectory(@"\\?\" + Path.Combine(_dir, Path.GetDirectoryName(deep.Replace('/', '\\'))!));
        File.WriteAllText(@"\\?\" + Path.Combine(_dir, deep.Replace('/', '\\')), "");
        Assert.True(Path.Combine(_dir, deep).Length > 260);

        Assert.Contains(deep, (await List(null)).Paths);
    }

    [Fact]
    public async Task An_unreadable_folder_still_lists_the_rest()
    {
        Write("ok/a.txt"); Write("locked/b.txt");
        var locked = new DirectoryInfo(Path.Combine(_dir, "locked"));
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var listing = await List(null);

            Assert.True(listing.Ok, listing.Failure);
            Assert.Contains("ok/a.txt", listing.Paths);
            Assert.Contains("locked", listing.Warning);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task Junctions_are_not_followed()
    {
        // Two junctions back to the parent: followed, the walk branches at every level and never ends.
        Write("loop/a/f.txt");
        Junction(Path.Combine(_dir, "loop", "a", "back"), Path.Combine(_dir, "loop"));
        Junction(Path.Combine(_dir, "loop", "a", "back2"), Path.Combine(_dir, "loop"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var listing = await List(null, ct: cts.Token);

        Assert.Equal(new[] { "loop/a/f.txt" }, Sorted(listing));
    }

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task A_rule_with_a_slash_stays_anchored_to_its_folder()
    {
        // git: a slash before the end anchors `logs/*` and `.vscode/*` to the .gitignore's folder.
        Write(".gitignore", "logs/*\n.vscode/*\n");
        Write("logs/a.cs"); Write("src/logs/b.cs"); Write(".vscode/x.json"); Write("deep/.vscode/c.json");

        var listing = await List(null);

        Assert.Equal(new[] { ".gitignore", "deep/.vscode/c.json", "src/logs/b.cs" }, Sorted(listing));
    }

    [Fact]
    public async Task Returns_paths_in_path_order()
    {
        for (var i = 0; i < 600; i++) { Write($"d{i % 30:00}/f{i:000}.cs"); }

        var listing = await List(null);

        Assert.Equal(listing.Paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), listing.Paths.ToArray());
    }

    [Fact]
    public async Task Empty_folder_is_an_empty_success()
    {
        var listing = await List(null);
        Assert.True(listing.Ok, listing.Failure);
        Assert.Empty(listing.Paths);
    }

    [Fact]
    public async Task A_missing_workspace_fails_with_reason()
    {
        var listing = await WorkspaceFileLister.ListAsync(Path.Combine(_dir, "nope"), null, true, CancellationToken.None);
        Assert.False(listing.Ok);
        Assert.False(string.IsNullOrWhiteSpace(listing.Failure));
    }

    [Fact]
    public async Task Cancelled_throws()
    {
        for (var i = 0; i < 3000; i++) { Write($"d{i % 30}/f{i}.cs"); }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => List(null, ct: cts.Token));
    }
}
