/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Helpers;

internal static class JsonExtensions
{
    /// <summary>Pretty-print a token via WriteTo + JsonTextWriter (not
    /// <c>JToken.ToString(Formatting)</c>): VS loads its own Newtonsoft.Json,
    /// whose overload signature can differ → MissingMethodException.</summary>
    public static string ToIndentedString(this JToken token)
    {
        using var sw = new StringWriter();
        using var jw = new JsonTextWriter(sw) { Formatting = Formatting.Indented };
        token.WriteTo(jw);
        jw.Flush();
        return sw.ToString();
    }

    /// <summary>Safely read a value from a JObject/JToken with a default fallback.</summary>
    public static T Val<T>(this JToken token, string key, T defaultValue = default)
        => token?[key] is JToken t && t.Type != JTokenType.Null ? t.Value<T>() ?? defaultValue : defaultValue;

    /// <summary>Read a string value, returning null if missing (null-safe).</summary>
    public static string Val(this JToken token, string key)
        => token?[key] is JToken t && t.Type != JTokenType.Null ? t.Value<string>() : null;

    /// <summary>Read a nullable bool value, returning null if missing.</summary>
    public static bool? ValBool(this JToken token, string key)
        => token?[key] is JToken t && t.Type != JTokenType.Null ? t.Value<bool>() : null;
}
