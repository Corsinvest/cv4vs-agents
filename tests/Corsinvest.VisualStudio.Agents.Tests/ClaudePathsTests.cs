/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Where the CLI's files live.
/// <para><see cref="ClaudePaths.ProjectFolderName"/> mirrors a rule that belongs to the CLI, not to
/// us: it resolves the cwd and replaces every non-alphanumeric character with a dash. Drift here
/// does not throw — it points at a folder that does not exist, and the session list comes back
/// empty as though the project had never been used.</para></summary>
public class ClaudePathsTests
{
    [Theory]
    // Every non-alphanumeric becomes a dash, case PRESERVED — including the dot in a username,
    // which is the case that makes this look wrong until you check the CLI does the same.
    [InlineData(@"C:\Users\jane.doe", "C--Users-jane-doe")]
    [InlineData(@"C:\proj\demo", "C--proj-demo")]
    [InlineData(@"K:\source\repos\OpenSource\cv4vs-agents", "K--source-repos-OpenSource-cv4vs-agents")]
    // Existing dashes and digits survive; spaces do not.
    [InlineData(@"C:\my-proj2\sub dir", "C--my-proj2-sub-dir")]
    public void ProjectFolderName_mirrors_the_CLI_folder_naming(string workingDirectory, string expected)
        => Assert.Equal(expected, ClaudePaths.ProjectFolderName(workingDirectory));

    [Fact]
    public void ProjectFolderName_resolves_a_relative_path_first()
    {
        // The CLI names the folder after the ABSOLUTE cwd, so a relative input must be resolved
        // before the replace — otherwise the same project gets two folders.
        var expected = ClaudePaths.ProjectFolderName(Directory.GetCurrentDirectory());

        Assert.Equal(expected, ClaudePaths.ProjectFolderName("."));
    }

    [Fact]
    public void The_folders_hang_off_the_config_dir_it_was_given()
    {
        var paths = new ClaudePaths(@"C:\cfg\claude");

        Assert.Equal(@"C:\cfg\claude", paths.ClaudeFolder);
        Assert.Equal(@"C:\cfg\claude\settings.json", paths.SettingsFile);
        Assert.Equal(@"C:\cfg\claude\projects", paths.ProjectsFolder);
        Assert.Equal(@"C:\cfg\claude\ide", paths.IdeFolder);
        Assert.Equal(@"C:\cfg\claude\file-history", paths.FileHistoryFolder);
    }
}
