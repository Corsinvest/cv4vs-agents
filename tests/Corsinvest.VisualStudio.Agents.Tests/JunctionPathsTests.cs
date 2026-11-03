/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The solution is opened through a junction (<c>C:\Users\me\Dev</c> to <c>D:\</c>), so the pane's working directory and the
/// IDE's documents have the link's spelling, while Claude Code 2.1.314 reports the files it edits by the real path. Each file must
/// be listed once, under the spelling the IDE knows it by.</summary>
public sealed class JunctionPathsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _junction;
    private readonly string _linkWork;
    private readonly string _realWork;
    private readonly ClaudePaths _paths;
    private readonly string _sessionId = Guid.NewGuid().ToString("D");

    public JunctionPathsTests()
    {
        var real = Path.Combine(_base, "real");
        Directory.CreateDirectory(Path.Combine(real, "work", "sub"));
        _junction = Junction.Create(Path.Combine(_base, "link"), real);
        _linkWork = Path.Combine(_base, "link", "work");
        // What git and the CLI would say: the folder as the file system names it.
        _realWork = PaneRoot.Of(_linkWork).Real;
        _paths = new ClaudePaths(Path.Combine(_base, "claude"));
    }

    public void Dispose()
    {
        // The junction first: see Junction.
        _junction.Dispose();
        try { Directory.Delete(_base, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    // Where the extension looks for the transcript: the folder named after the working directory it passed to the CLI.
    private string TranscriptPath => Path.Combine(_paths.ProjectsFolder, ClaudePaths.ProjectFolderName(_linkWork), _sessionId + ".jsonl");

    private void WriteTranscript(params JObject[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TranscriptPath));
        File.WriteAllText(TranscriptPath, string.Concat(lines.Select(l => l.ToString(Formatting.None) + "\n")), new UTF8Encoding(false));
    }

    private void WriteBackup(string name, string content)
    {
        var folder = Path.Combine(_paths.FileHistoryFolder, _sessionId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), content, new UTF8Encoding(false));
    }

    private SessionChanges Create()
        => new(_paths, _linkWork, new SessionChangesOptions
        {
            FileCheckpoints = () => true,
            PollMilliseconds = new[] { 0, 20, 40 },
            Baselines = new ChangeBaselines(Path.Combine(_base, "baselines"), OutputWindowLogger.Global),
            Kept = new KeptLedger(Path.Combine(_base, "kept"), OutputWindowLogger.Global),
        }, OutputWindowLogger.Global);

    private HookEdit Hook(string filePath, string cwd, string toolUseId = "toolu_1")
        => HookEdit.TryParse(new JObject
        {
            ["session_id"] = _sessionId,
            ["transcript_path"] = TranscriptPath,
            ["cwd"] = cwd,
            ["tool_name"] = "Edit",
            ["tool_input"] = new JObject { ["file_path"] = filePath },
        }, toolUseId);

    [Fact]
    public async Task A_file_the_cli_reports_by_its_real_path_is_listed_under_the_link_with_its_baseline()
    {
        WriteTranscript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1, realParentDir: _realWork));
        WriteBackup("h@v1", "one\n");
        File.WriteAllText(Path.Combine(_linkWork, "a.txt"), "one\ntwo\n");
        using var changes = Create();

        await changes.OnPostEditAsync(Hook(Path.Combine(_realWork, "a.txt"), _realWork));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(Path.Combine(_linkWork, "a.txt"), row.Path, ignoreCase: true);
        Assert.True(row.Baseline.IsKnown);
        Assert.Equal(1, row.Added);
    }

    [Fact]
    public async Task A_second_edit_of_the_same_file_by_either_name_is_one_row()
    {
        WriteTranscript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1, realParentDir: _realWork));
        WriteBackup("h@v1", "one\n");
        File.WriteAllText(Path.Combine(_linkWork, "a.txt"), "one\ntwo\n");
        using var changes = Create();

        await changes.OnPostEditAsync(Hook(Path.Combine(_realWork, "a.txt"), _realWork, "toolu_1"));
        File.WriteAllText(Path.Combine(_linkWork, "a.txt"), "one\ntwo\nthree\n");
        await changes.OnPostEditAsync(Hook(Path.Combine(_linkWork, "a.txt"), _linkWork, "toolu_2"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(2, row.Added);
    }

    [Fact]
    public async Task A_relative_path_after_a_cd_in_the_real_folder_is_listed_under_the_link()
    {
        WriteTranscript(Jsonl.UserPrompt("u1", "go"));
        File.WriteAllText(Path.Combine(_linkWork, "sub", "b.txt"), "x\n");
        using var changes = Create();

        await changes.OnPostEditAsync(Hook("b.txt", Path.Combine(_realWork, "sub")));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(Path.Combine(_linkWork, "sub", "b.txt"), row.Path, ignoreCase: true);
    }

    [Fact]
    public async Task A_path_in_the_working_directorys_own_spelling_is_left_as_it_is()
    {
        // What Claude Code 2.1.312 and earlier reported: the folder it was started in.
        WriteTranscript(Jsonl.UserPrompt("u1", "go"));
        File.WriteAllText(Path.Combine(_linkWork, "c.txt"), "x\n");
        using var changes = Create();

        await changes.OnPostEditAsync(Hook(Path.Combine(_linkWork, "c.txt"), _linkWork));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(Path.Combine(_linkWork, "c.txt"), row.Path, ignoreCase: true);
    }

    [Fact]
    public void A_resumed_list_names_a_file_the_cli_recorded_by_its_real_path_under_the_link()
    {
        WriteTranscript(Jsonl.FileHistoryDelta(Path.Combine(_realWork, "d.txt"), "d@v1", 1));
        WriteBackup("d@v1", "before\n");
        File.WriteAllText(Path.Combine(_linkWork, "d.txt"), "after\n");
        using var changes = Create();

        changes.BeginSession(_sessionId, resume: true);

        Assert.True(SpinWait.SpinUntil(() => changes.Set.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(Path.Combine(_linkWork, "d.txt"), changes.Set.Snapshot()[0].Path, ignoreCase: true);
    }
}
