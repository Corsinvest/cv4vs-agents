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
using System.Linq;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>A throwaway config-dir holding one session .jsonl, laid out where the CLI would put it.
/// <para>The readers take the real filesystem: ClaudePaths and the working directory both arrive
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

    /// <summary>The transcript the fixture wrote.</summary>
    public string TranscriptPath => Path.Combine(
        Paths.ProjectsFolder, ClaudePaths.ProjectFolderName(WorkingDirectory), SessionId + ".jsonl");

    /// <summary>Add records to the transcript, as the CLI does while a turn runs.</summary>
    public void Append(params JObject[] lines)
        => File.AppendAllText(
            TranscriptPath,
            string.Concat(lines.Select(l => l.ToString(Formatting.None) + "\n")),
            new UTF8Encoding(false));

    /// <summary>Put a copy where the CLI keeps them: <c>file-history/&lt;session&gt;/&lt;name&gt;</c>.</summary>
    public string WriteBackup(string name, string content)
    {
        var folder = Path.Combine(Paths.FileHistoryFolder, SessionId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A file in the fixture's working directory, written with the given text.</summary>
    public string WriteWork(string relativePath, string content)
    {
        var path = Path.Combine(WorkingDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Writes the lines as the CLI does: one compact JSON object per line, UTF-8, LF.</summary>
    public static SessionFixture Compact(IEnumerable<JObject> lines)
        => Write(lines, Formatting.None);

    /// <summary>The same content through a pretty-printing writer. Still valid JSONL only if each
    /// record stays on one line, so indentation goes INSIDE the object, which is the shape the
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

    /// <summary>A user turn whose content is a BLOCK ARRAY, which is the shape the host sends: the
    /// editor-context tag goes in a block of its own ahead of the prompt. Pass the blocks' texts in
    /// send order.</summary>
    public static JObject UserPromptBlocks(string uuid, string parent, params string[] texts) => new()
    {
        ["type"] = "user",
        ["uuid"] = uuid,
        ["parentUuid"] = parent,
        ["timestamp"] = "2026-08-26T10:00:00.000Z",
        ["message"] = new JObject
        {
            ["role"] = "user",
            ["content"] = new JArray(texts.Select(t => new JObject { ["type"] = "text", ["text"] = t })),
        },
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

    /// <summary>A snapshot with nothing tracked: present in real transcripts, and NOT rewindable.</summary>
    public static JObject EmptySnapshot(string messageId) => new()
    {
        ["type"] = "file-history-snapshot",
        ["messageId"] = messageId,
        ["snapshot"] = new JObject { ["trackedFileBackups"] = new JObject() },
    };

    /// <summary>The record the CLI writes the first time it tracks a file: the copy it took is the
    /// file as it was before that edit. A null backup name means the file did not exist.</summary>
    public static JObject FileHistoryDelta(string trackingPath, string backupFileName, int version,
                                           string realParentDir = null, string snapshotMessageId = "u1") => new()
    {
        ["type"] = "file-history-delta",
        ["messageId"] = Guid.NewGuid().ToString(),
        ["snapshotMessageId"] = snapshotMessageId,
        ["trackingPath"] = trackingPath,
        ["backup"] = Backup(backupFileName, version, realParentDir),
        ["timestamp"] = "2026-10-26T10:00:00.000Z",
    };

    /// <summary>A snapshot listing the files tracked so far, each with the copy taken for it.</summary>
    public static JObject TrackedSnapshot(string messageId, params JProperty[] files) => new()
    {
        ["type"] = "file-history-snapshot",
        ["messageId"] = messageId,
        ["snapshot"] = new JObject { ["trackedFileBackups"] = new JObject(files) },
        ["isSnapshotUpdate"] = false,
    };

    /// <summary>One entry of a snapshot's tracked files.</summary>
    public static JProperty Tracked(string trackingPath, string backupFileName, int version, string realParentDir = null)
        => new(trackingPath, Backup(backupFileName, version, realParentDir));

    private static JObject Backup(string backupFileName, int version, string realParentDir) => new()
    {
        ["backupFileName"] = backupFileName is null ? JValue.CreateNull() : (JToken)backupFileName,
        ["version"] = version,
        ["backupTime"] = "2026-10-26T10:00:00.000Z",
        ["realParentDir"] = realParentDir is null ? JValue.CreateNull() : (JToken)realParentDir,
    };

    /// <summary>A prompt sent while a turn was running, as the CLI records it once injected: an
    /// attachment with its own line uuid, carrying the uuid it was sent with as source_uuid. The
    /// prompt is a block array with the editor-context block ahead, the host's shape. Keys in the
    /// CLI's order: the nested attachment, with its own "type", comes BEFORE the line's type: a
    /// scan taking the first "type" it meets reads the wrong one.</summary>
    public static JObject QueuedPrompt(string uuid, string sourceUuid, string text, string parent = null,
                                       string commandMode = "prompt", JObject origin = null) => new()
                                       {
                                           ["parentUuid"] = parent,
                                           ["isSidechain"] = false,
                                           ["attachment"] = new JObject
                                           {
                                               ["type"] = "queued_command",
                                               ["prompt"] = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = "<ide_opened_file>Foo.cs</ide_opened_file>" },
                new JObject { ["type"] = "text", ["text"] = text },
            },
                                               ["source_uuid"] = sourceUuid,
                                               ["commandMode"] = commandMode,
                                               ["origin"] = origin ?? new JObject { ["kind"] = "human" },
                                           },
                                           ["type"] = "attachment",
                                           ["uuid"] = uuid,
                                           ["timestamp"] = "2026-08-26T10:00:02.000Z",
                                       };
}
