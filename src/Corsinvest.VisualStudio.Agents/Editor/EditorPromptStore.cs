/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary><para>Loads/saves the context menus' prompts as a plain JSON file
/// (<see cref="AppPaths.PromptsFile"/>), one list per <see cref="PromptScope"/>, like
/// <c>ProfileStore</c> and for the same reason: the menus are queried by VS long before any Options
/// page exists. Tolerant on read — a missing or corrupt file falls back to <see cref="Defaults"/>
/// rather than an empty menu.</para>
/// <para>
/// An object of lists rather than one array with a scope on each row, because it tells "never
/// configured" from "emptied on purpose": a key that is absent takes that menu's defaults (a menu
/// added in a later version shows up with its prompts), a key holding <c>[]</c> stays empty.
/// </para></summary>
internal static class EditorPromptStore
{
    private static Dictionary<PromptScope, List<EditorPrompt>> _cache;

    private static readonly PromptScope[] Scopes = (PromptScope[])Enum.GetValues(typeof(PromptScope));

    /// <summary>What ships with the extension, and what a missing or unreadable list falls back
    /// to. Order is the menu order: the one you reach for most comes first. A new list each call:
    /// the Options grid edits the items in place.</summary>
    public static List<EditorPrompt> Defaults(PromptScope scope) => scope switch
    {
        PromptScope.Editor =>
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
        ],
        // Sent on click, as the single "Explain" entry these menus had always was.
        PromptScope.ErrorList =>
        [
            new EditorPrompt
            {
                Title = "Explain",
                Prompt = "What is causing these errors, and how do I fix them?",
                SendImmediately = true,
            },
            new EditorPrompt { Title = "Fix", Prompt = "Fix these errors.", SendImmediately = true },
        ],
        // Asked flat: the active pane is as likely to be Debug or the program's own output as a
        // failed build, so naming a failure would invent one where there is none — and where there
        // is one, explaining it covers the cause without being told to.
        PromptScope.Output =>
        [
            new EditorPrompt { Title = "Explain", Prompt = "Explain this output.", SendImmediately = true },
        ],
        _ => [],
    };

    /// <summary>The prompts a menu shows. Cached: VS re-queries the menus on every keystroke, and
    /// re-reading the file each time would put disk IO on that path. Blank titles are dropped —
    /// a nameless row in the JSON would render as an empty menu item.</summary>
    public static IReadOnlyList<EditorPrompt> Items(PromptScope scope)
    {
        _cache ??= Load().ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(p => !string.IsNullOrWhiteSpace(p.Title)).ToList());
        return _cache[scope];
    }

    /// <summary>Exactly what is on disk, for the Options page to edit. With no prompts.json yet,
    /// the editor's list is carried over from editor-prompts.json; nothing is written until the
    /// page is applied.</summary>
    public static Dictionary<PromptScope, List<EditorPrompt>> Load()
    {
        try
        {
            if (File.Exists(AppPaths.PromptsFile)) { return Parse(File.ReadAllText(AppPaths.PromptsFile)); }
            if (File.Exists(AppPaths.EditorPromptsFile)) { return FromLegacy(File.ReadAllText(AppPaths.EditorPromptsFile)); }
        }
        catch (IOException)
        {
            OutputWindowLogger.Global.Warn("[editor] failed to read the prompts file (IO) — using the defaults");
        }
        return AllDefaults();
    }

    /// <summary>Writes prompts.json only: editor-prompts.json is left as it is, for a downgrade.</summary>
    public static void Save(Dictionary<PromptScope, List<EditorPrompt>> prompts)
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        File.WriteAllText(AppPaths.PromptsFile, Serialize(prompts));
        // The menus read Items, which is cached for the reason given there.
        _cache = null;
    }

    /// <summary>prompts.json's content. A key whose list cannot be read takes its defaults on its
    /// own: one bad hand edit should not wipe the other menus.</summary>
    internal static Dictionary<PromptScope, List<EditorPrompt>> Parse(string json)
    {
        JObject root;
        try { root = string.IsNullOrWhiteSpace(json) ? null : JToken.Parse(json) as JObject; }
        catch (JsonException) { root = null; }
        if (root == null)
        {
            OutputWindowLogger.Global.Warn("[editor] prompts.json is corrupt — using the defaults");
            return AllDefaults();
        }

        var result = new Dictionary<PromptScope, List<EditorPrompt>>();
        foreach (var scope in Scopes)
        {
            var token = root[scope.ToString()];
            if (token == null) { result[scope] = Defaults(scope); continue; }
            try
            {
                result[scope] = token.ToObject<List<EditorPrompt>>() ?? Defaults(scope);
            }
            catch (JsonException)
            {
                OutputWindowLogger.Global.Warn($"[editor] prompts.json: the {scope} list is corrupt — using its defaults");
                result[scope] = Defaults(scope);
            }
        }
        return result;
    }

    /// <summary>editor-prompts.json's content: the editor's list, from before the other menus had
    /// prompts of their own.</summary>
    internal static Dictionary<PromptScope, List<EditorPrompt>> FromLegacy(string json)
    {
        var result = AllDefaults();
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                result[PromptScope.Editor] = JsonConvert.DeserializeObject<List<EditorPrompt>>(json) ?? Defaults(PromptScope.Editor);
            }
        }
        catch (JsonException)
        {
            OutputWindowLogger.Global.Warn("[editor] editor-prompts.json is corrupt — using the defaults");
        }
        return result;
    }

    /// <summary>A scope missing from <paramref name="prompts"/> is left out rather than written as
    /// <c>[]</c>: absent reads back as that menu's defaults, empty as emptied on purpose.</summary>
    internal static string Serialize(Dictionary<PromptScope, List<EditorPrompt>> prompts)
    {
        var root = new JObject();
        foreach (var scope in Scopes)
        {
            if (prompts != null && prompts.TryGetValue(scope, out var list) && list != null)
            {
                root[scope.ToString()] = JToken.FromObject(list);
            }
        }
        return JsonExtensions.ToIndentedString(root);
    }

    private static Dictionary<PromptScope, List<EditorPrompt>> AllDefaults()
        => Scopes.ToDictionary(s => s, Defaults);
}
