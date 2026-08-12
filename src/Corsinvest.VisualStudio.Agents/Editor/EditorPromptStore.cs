/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary>Loads/saves the editor context menu's prompts as a plain JSON file
/// (<see cref="AppPaths.EditorPromptsFile"/>), like <c>ProfileStore</c> and for the same reason:
/// the menu is queried by VS long before any Options page exists. Tolerant on read — a missing or
/// corrupt file falls back to <see cref="Defaults"/> rather than an empty menu.</summary>
internal static class EditorPromptStore
{
    private static IReadOnlyList<EditorPrompt> _cache;

    /// <summary>What ships with the extension, and what a missing or unreadable file falls back
    /// to. Order is the menu order: the one you reach for most comes first.</summary>
    public static IReadOnlyList<EditorPrompt> Defaults =>
    [
        new EditorPrompt { Title = "Explain", Prompt = "Explain what this code does." },
        new EditorPrompt
        {
            Title = "Review",
            Prompt = "Review this code and point out what you would change, and why.",
        },
        new EditorPrompt
        {
            Title = "Find bugs",
            Prompt = "Look for bugs in this code. Say so plainly if you find none.",
        },
        new EditorPrompt
        {
            Title = "Write tests",
            Prompt = "Write tests for this code, following the ones already in this project.",
        },
        new EditorPrompt
        {
            Title = "Simplify",
            Prompt = "Simplify this code without changing what it does.",
            RequiresSelection = true,
        },
    ];

    /// <summary>The prompts the menu shows. Cached: VS re-queries the menu on every keystroke, and
    /// re-reading the file each time would put disk IO on that path. Blank titles are dropped —
    /// a nameless row in the JSON would render as an empty menu item.</summary>
    public static IReadOnlyList<EditorPrompt> Items =>
        _cache ??= [.. Load().Where(p => !string.IsNullOrWhiteSpace(p.Title))];

    /// <summary>Exactly what is on disk, for the Options page to edit. Falls back to the defaults
    /// when there is no file yet, so the page opens on something rather than nothing.</summary>
    public static IReadOnlyList<EditorPrompt> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.EditorPromptsFile)) { return Defaults; }
            var json = File.ReadAllText(AppPaths.EditorPromptsFile);
            return string.IsNullOrWhiteSpace(json)
                ? Defaults
                : JsonConvert.DeserializeObject<List<EditorPrompt>>(json) ?? Defaults;
        }
        catch (IOException)
        {
            OutputWindowLogger.Global.Warn("[editor] failed to read editor-prompts.json (IO) — using the defaults");
            return Defaults;
        }
        catch (JsonException)
        {
            OutputWindowLogger.Global.Warn("[editor] editor-prompts.json is corrupt — using the defaults");
            return Defaults;
        }
    }

    public static void Save(IReadOnlyList<EditorPrompt> prompts)
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        File.WriteAllText(
            AppPaths.EditorPromptsFile,
            JsonExtensions.ToIndentedString(JToken.FromObject(prompts ?? [])));
        // The menu reads Items, which is cached for the reason given there.
        _cache = null;
    }
}
