/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using Corsinvest.VisualStudio.Agents.Core.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The one writer of the CLI's settings.json. What must never happen: losing the keys it
/// wasn't asked to touch, or overwriting a file it couldn't read.</summary>
public class CliSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cv4vs-cus-" + Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(_dir, "settings.json");

    // Any key: these tests are about the writer, not about what the key means.
    private const string Key = "someSetting";

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); }
    }

    [Fact]
    public void SetKey_keeps_every_other_key()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsFile, "{\"model\":\"opus\",\"permissions\":{\"allow\":[\"Bash\"]}}");

        CliSettingsStore.SetKey(SettingsFile, Key, true);

        var root = JObject.Parse(File.ReadAllText(SettingsFile));
        Assert.Equal("opus", (string)root["model"]);
        Assert.Equal("Bash", (string)root["permissions"]["allow"][0]);
        Assert.True((bool)root[Key]);
    }

    [Fact]
    public void SetKey_creates_file_and_folder_when_missing()
    {
        CliSettingsStore.SetKey(SettingsFile, Key, false);

        var root = JObject.Parse(File.ReadAllText(SettingsFile));
        Assert.False((bool)root[Key]);
        Assert.Single(root.Properties());
    }

    [Fact]
    public void SetKey_flips_an_existing_value()
    {
        CliSettingsStore.SetKey(SettingsFile, Key, true);
        CliSettingsStore.SetKey(SettingsFile, Key, false);

        Assert.False((bool)JObject.Parse(File.ReadAllText(SettingsFile))[Key]);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void Unreadable_file_throws_and_is_left_untouched(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsFile, content);

        Assert.Throws<InvalidDataException>(() => CliSettingsStore.SetKey(SettingsFile, Key, true));
        Assert.Equal(content, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void Update_that_changes_nothing_does_not_write()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsFile, "{\"diffTool\":\"vscode\"}");
        var before = File.GetLastWriteTimeUtc(SettingsFile);

        var wrote = CliSettingsStore.Update(SettingsFile, root => false);

        Assert.False(wrote);
        Assert.Equal(before, File.GetLastWriteTimeUtc(SettingsFile));
    }
}
