/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>Writes the CLI's own settings.json — its contract, not ours. The control protocol can't:
/// update_settings refuses remoteControlAtStartup ("keys not allowed") and apply_flag_settings
/// answers success while persisting nothing, which is why the VS Code extension writes the file by
/// hand too. Everything it wasn't asked to change is kept, and a file it can't read is never
/// overwritten.</summary>
internal static class CliSettingsStore
{
    private static readonly object Gate = new();

    /// <summary>Raised after a key was written, with the file it was written to: panes of the same
    /// profile compare that path with their own.</summary>
    public static event Action<string, string, JToken> KeyChanged;

    public static void RaiseKeyChanged(string path, string key, JToken value) => KeyChanged?.Invoke(path, key, value);

    public static bool Update(string path, Func<JObject, bool> mutate)
    {
        lock (Gate)
        {
            JObject root;
            if (File.Exists(path))
            {
                try { root = JToken.Parse(File.ReadAllText(path)) as JObject; }
                catch (JsonReaderException ex) { throw new InvalidDataException($"{path} is not valid JSON: {ex.Message}", ex); }
                if (root == null) { throw new InvalidDataException($"{path} is not a JSON object."); }
            }
            else
            {
                root = [];
            }
            if (!mutate(root)) { return false; }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var text = root.ToIndentedString() + Environment.NewLine;
            // A symlinked settings.json (dotfiles repo) is written through the link: File.Replace
            // would swap the link itself for a plain file and silently cut it from its target.
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                File.WriteAllText(path, text);
                return true;
            }
            // Otherwise through a temp file: the CLI reads this file on every start, and must never
            // catch it half-written.
            var tmp = path + ".cv4vs.tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) { File.Replace(tmp, path, null); }
            else { File.Move(tmp, path); }
            return true;
        }
    }

    public static void SetKey(string path, string key, JToken value) =>
        Update(path, root =>
        {
            root[key] = value;
            return true;
        });
}
