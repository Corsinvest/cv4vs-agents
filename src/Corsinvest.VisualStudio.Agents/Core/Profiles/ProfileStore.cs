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

namespace Corsinvest.VisualStudio.Agents.Core.Profiles;

/// <summary>Loads/saves the profile list as a plain JSON file
/// (<see cref="AppPaths.ProfilesFile"/>), NOT the VS settings store — so the menu and
/// launch path read profiles without materializing the Options page first. Tolerant on
/// read (missing/corrupt file → empty list) so a bad file never blocks the extension.</summary>
internal static class ProfileStore
{
    /// <summary>The profiles. <paramref name="forEdit"/> = true returns EXACTLY what's on disk (no
    /// synthetic native, disabled included) — for the Options page, which edits then persists (the
    /// native must never be written back). = false returns the list for USE: the native "Claude"
    /// prepended and only enabled, non-blank-named profiles kept, in saved order.</summary>
    public static IReadOnlyList<Profile> Load(bool forEdit)
    {
        var onDisk = ReadFromDisk();
        return forEdit
            ? onDisk
            : [.. WithNative(onDisk).Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Name))];
    }

    /// <summary>Raw read of the persisted profiles. Missing/unreadable/corrupt → empty.</summary>
    private static List<Profile> ReadFromDisk()
    {
        try
        {
            return File.Exists(AppPaths.ProfilesFile)
                ? Deserialize(File.ReadAllText(AppPaths.ProfilesFile))
                : [];
        }
        catch (IOException)
        {
            OutputWindowLogger.Warn("[profiles] failed to read profiles.json (IO) — profiles unavailable this session");
            return [];
        }
    }

    /// <summary>Write the profiles to disk (creates the data folder if missing).</summary>
    public static void Save(IReadOnlyList<Profile> profiles)
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        File.WriteAllText(AppPaths.ProfilesFile, Serialize(profiles));
    }

    public static string Serialize(IReadOnlyList<Profile> profiles) =>
        JsonExtensions.ToIndentedString(JToken.FromObject(profiles ?? []));

    public static List<Profile> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return []; }
        try
        {
            return JsonConvert.DeserializeObject<List<Profile>>(json) ?? [];
        }
        catch (JsonException)
        {
            OutputWindowLogger.Warn("[profiles] profiles.json is corrupt — ignoring, profiles will appear empty");
            return [];
        }
    }

    /// <summary>The native "Claude" profile: a normal profile created on the fly (never persisted
    /// to profiles.json), prepended to the list by <see cref="WithNative"/>. Env is the delta over
    /// the inherited process env — at most the system CLAUDE_CONFIG_DIR so the extension reads the
    /// right config-dir; empty otherwise (the CLI then uses ~/.claude). Everything else is inherited
    /// by claude.exe from the parent.</summary>
    private static Profile NativeProfile()
    {
        var p = new Profile { Name = "Claude", Description = "", Enabled = true };
        var sys = Environment.GetEnvironmentVariable(ClaudePaths.ConfigDirEnvVar);
        if (!string.IsNullOrEmpty(sys)) { p.Env[ClaudePaths.ConfigDirEnvVar] = sys; }
        return p;
    }

    /// <summary>The saved profiles with the native "Claude" prepended (unless the user already has
    /// a profile named "Claude" — that one wins). Used by <see cref="Load"/> for the non-edit case;
    /// the native entry is created on the fly and never part of what Save writes.</summary>
    private static IReadOnlyList<Profile> WithNative(IReadOnlyList<Profile> profiles)
    {
        var list = profiles ?? [];
        var hasClaude = list.Any(p => string.Equals(p.Name, "Claude", StringComparison.OrdinalIgnoreCase));
        return hasClaude ? [.. list] : new[] { NativeProfile() }.Concat(list).ToList();
    }
}
