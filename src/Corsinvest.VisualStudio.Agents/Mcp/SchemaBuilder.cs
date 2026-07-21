/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;

namespace Corsinvest.VisualStudio.Agents.Mcp;

/// <summary>
/// <para>
/// Reflection-based <c>JSON Schema</c> generator for MCP tool input POCOs,
/// cached per type. Property names are emitted <c>camelCase</c>; <c>[Required]</c>
/// adds to the schema's <c>required</c> array (everything else is optional);
/// primitives map directly, lists become arrays, nested POCOs recurse.
/// </para>
/// <para>
/// Not every JSON-Schema feature is covered (oneOf, patterns, ranges, …);
/// tools needing richer schemas override <see cref="McpTool{TArgs}.InputSchema"/>.
/// </para>
/// </summary>
internal static class SchemaBuilder
{
    private static readonly ConcurrentDictionary<Type, object> _cache = new();

    public static object For<T>() where T : new()
        => _cache.GetOrAdd(typeof(T), BuildObjectSchema);

    private static object BuildObjectSchema(Type t)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) { continue; }
            // JsonProperty wins over camelCase — needed for the snake_case
            // wire names the Claude CLI uses on a few tools (e.g. old_file_path).
            var jsonAttr = prop.GetCustomAttribute<JsonPropertyAttribute>();
            var name = jsonAttr?.PropertyName ?? ToCamelCase(prop.Name);
            properties[name] = BuildPropertySchema(prop);
            if (IsRequired(prop)) { required.Add(name); }
        }

        // Dictionary (not anonymous type) so empty properties/required
        // serialize predictably.
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) { schema["required"] = required; }
        return schema;
    }

    private static object BuildPropertySchema(PropertyInfo prop)
    {
        var meta = new Dictionary<string, object>();

        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        var jsonType = MapJsonType(type);
        if (jsonType != null) { meta["type"] = jsonType; }

        // Array/list element type (string[], List<string>, ...).
        if (jsonType == "array")
        {
            var elem = GetEnumerableElementType(type);
            if (elem != null)
            {
                var elemJson = MapJsonType(elem);
                meta["items"] = elemJson != null
                    ? (object)new Dictionary<string, object> { ["type"] = elemJson }
                    : new Dictionary<string, object> { ["type"] = "object" };
            }
        }

        // Nested complex object: recurse.
        if (jsonType == "object" && type != typeof(object))
        {
            var nested = _cache.GetOrAdd(type, BuildObjectSchema);
            // Inline rather than $ref — not all MCP clients honor $ref.
            if (nested is IDictionary<string, object> dict)
            {
                foreach (var kv in dict) { meta[kv.Key] = kv.Value; }
            }
        }

        var desc = prop.GetCustomAttribute<DescriptionAttribute>()?.Text;
        if (!string.IsNullOrEmpty(desc)) { meta["description"] = desc; }

        return meta;
    }

    private static string MapJsonType(Type t)
    {
        if (t == typeof(string)) { return "string"; }
        if (t == typeof(bool)) { return "boolean"; }
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)) { return "integer"; }
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) { return "number"; }
        if (typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string)) { return "array"; }
        return t.IsClass ? "object" : null;
    }

    private static Type GetEnumerableElementType(Type t)
    {
        if (t.IsArray) { return t.GetElementType(); }
        var generic = t.GetInterfaces()
            .Concat([t])
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return generic?.GetGenericArguments()[0];
    }

    private static bool IsRequired(PropertyInfo prop)
    {
        // Only [Required] marks a property required; value types and
        // reference types are otherwise optional (don't force every flag).
        return prop.GetCustomAttribute<RequiredAttribute>() != null;
    }

    /// <summary>VS-friendly camelCase: <c>FilePath → filePath</c>,
    /// <c>URL → url</c>, <c>HTTPSPort → httpsPort</c> (best-effort).
    /// Mirrors the policy Newtonsoft's CamelCasePropertyNamesContractResolver
    /// uses so wire and schema stay in sync.</summary>
    public static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s) || char.IsLower(s[0])) { return s; }
        var arr = s.ToCharArray();
        for (int i = 0; i < arr.Length; i++)
        {
            if (i > 0 && i + 1 < arr.Length && !char.IsUpper(arr[i + 1])) { break; }
            if (i + 1 == arr.Length || (i > 0 && !char.IsUpper(arr[i + 1]))) { arr[i] = char.ToLowerInvariant(arr[i]); break; }
            arr[i] = char.ToLowerInvariant(arr[i]);
        }
        return new string(arr);
    }
}

/// <summary>Optional human-readable description for a property — surfaces
/// in the JSON Schema as the <c>description</c> field, which the model
/// uses to decide what to put in each argument.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class DescriptionAttribute(string text) : Attribute
{
    public string Text { get; } = text;

}
