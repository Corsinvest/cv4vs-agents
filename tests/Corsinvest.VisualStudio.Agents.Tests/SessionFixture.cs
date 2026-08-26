/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>A throwaway config-dir holding one session .jsonl, laid out where the CLI would put it.
/// <para>The readers take the real filesystem — ClaudePaths and the working directory both arrive
/// through the constructor, so a temp folder is all the isolation needed. No IFileSystem seam:
/// these files are small, and an abstraction here would be tested instead of the code that ships.
/// </para></summary>
internal sealed class SessionFixture : IDisposable
{
    public string ConfigDir { get; }
    public string WorkingDirectory { get; }
    public ClaudePaths Paths { get; }
    public string SessionId { get; }

    private SessionFixture(string configDir, string workingDirectory, string sessionId)
    {
        ConfigDir = configDir;
        WorkingDirectory = workingDirectory;
        SessionId = sessionId;
        Paths = new ClaudePaths(configDir);
    }

    public SessionManager Manager() => new(Paths, WorkingDirectory, OutputWindowLogger.Global);

    /// <summary>Writes the lines as the CLI does: one compact JSON object per line, UTF-8, LF.</summary>
    public static SessionFixture Compact(IEnumerable<JObject> lines)
        => Write(lines, Formatting.None);

    /// <summary>The same content through a pretty-printing writer. Still valid JSONL only if each
    /// record stays on one line, so indentation goes INSIDE the object — which is the shape the
    /// string-matching scan has to survive: '"type": "user"' with a space after the colon.</summary>
    public static SessionFixture Pretty(IEnumerable<JObject> lines)
        => Write(lines, Formatting.None, spaceAfterColon: true);

    private static SessionFixture Write(IEnumerable<JObject> lines, Formatting formatting,
                                        bool spaceAfterColon = false)
    {
        var configDir = Path.Combine(Path.GetTempPath(),
            "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(configDir, "work");
        Directory.CreateDirectory(workingDirectory);

        var sessionId = Guid.NewGuid().ToString("D");
        var folder = Path.Combine(configDir, "projects",
            ClaudePaths.ProjectFolderName(workingDirectory));
        Directory.CreateDirectory(folder);

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var json = line.ToString(formatting);
            // A writer that puts a space after every colon produces the same JSON and is what the
            // scan must tolerate; done by re-serializing rather than by hand so the result stays
            // parseable.
            if (spaceAfterColon) { json = SpaceAfterColons(json); }
            sb.Append(json).Append('\n');
        }
        File.WriteAllText(Path.Combine(folder, sessionId + ".jsonl"), sb.ToString(), new UTF8Encoding(false));
        return new SessionFixture(configDir, workingDirectory, sessionId);
    }

    /// <summary>Adds a space after each colon that separates a key from its value, leaving colons
    /// inside string values alone (a Windows path, a timestamp).</summary>
    private static string SpaceAfterColons(string json)
    {
        var sb = new StringBuilder(json.Length + 32);
        bool inString = false, escaped = false;
        foreach (var c in json)
        {
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escaped = true; continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }
            sb.Append(c);
            if (c == ':' && !inString) { sb.Append(' '); }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(ConfigDir)) { Directory.Delete(ConfigDir, true); } }
        catch { /* a temp folder the OS will reclaim; failing the test over it helps nobody */ }
    }
}

/// <summary>The transcript records these tests care about, in the CLI's own shape.</summary>
internal static class Jsonl
{
    public static JObject UserPrompt(string uuid, string text, string parent = null) => new()
    {
        ["type"] = "user",
        ["uuid"] = uuid,
        ["parentUuid"] = parent,
        ["timestamp"] = "2026-08-26T10:00:00.000Z",
        ["message"] = new JObject { ["role"] = "user", ["content"] = text },
    };

    public static JObject Assistant(string uuid, string text, string parent = null) => new()
    {
        ["type"] = "assistant",
        ["uuid"] = uuid,
        ["parentUuid"] = parent,
        ["timestamp"] = "2026-08-26T10:00:01.000Z",
        ["message"] = new JObject
        {
            ["role"] = "assistant",
            ["model"] = "claude-opus-5",
            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
        },
    };

    /// <summary>A rewindable point: a snapshot naming at least one tracked backup.</summary>
    public static JObject FileHistorySnapshot(string messageId, params string[] files)
    {
        var backups = new JObject();
        foreach (var f in files)
        {
            backups[f] = new JObject { ["backupFileName"] = "bk-" + Math.Abs(f.GetHashCode()) };
        }
        return new JObject
        {
            ["type"] = "file-history-snapshot",
            ["messageId"] = messageId,
            ["snapshot"] = new JObject { ["trackedFileBackups"] = backups },
        };
    }

    /// <summary>A snapshot with nothing tracked — present in real transcripts, and NOT rewindable.</summary>
    public static JObject EmptySnapshot(string messageId) => new()
    {
        ["type"] = "file-history-snapshot",
        ["messageId"] = messageId,
        ["snapshot"] = new JObject { ["trackedFileBackups"] = new JObject() },
    };
}
