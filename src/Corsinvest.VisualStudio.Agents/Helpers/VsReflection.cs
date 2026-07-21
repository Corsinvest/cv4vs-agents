/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Helpers;

/// <summary>
/// Late-binding helpers for VS/Roslyn types that are loaded in-process but not referenced at
/// compile time (referencing them would pin Roslyn/VB-only assemblies — see the "multi-language"
/// rule — or not bind at all with a partial assembly-qualified name). Centralizes the reflection
/// idioms that were duplicated across the Ide/ services. Behavior matches the previous inline code:
/// a missing member throws (same NullReference/exception as before); the typed overloads use a
/// hard <c>(T)</c> cast. Callers that tolerated a missing member with <c>?.</c> keep null-checking
/// the returned <see cref="object"/>.
/// </summary>
internal static class VsReflection
{
    // Resolved types are cached: scanning every loaded assembly (hundreds in VS) is costly, and the
    // set of loaded VS/Roslyn assemblies is stable for the session, so both hits AND misses (null)
    // are permanent — a type absent now won't appear later. Value can be null (cached miss).
    private static readonly ConcurrentDictionary<string, Type> _typeCache = new();

    /// <summary>Resolve a type by full name across all loaded assemblies. Type.GetType with a
    /// partial assembly-qualified name doesn't bind the VS/Roslyn assemblies, but they're already
    /// loaded in-process, so scan them. Cached (incl. misses). Returns null if not found.</summary>
    public static Type FindType(string fullName)
        => _typeCache.GetOrAdd(fullName, ScanForType);

    private static Type ScanForType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) { return t; }
            }
            catch { /* skip assemblies that can't be reflected */ }
        }
        return null;
    }

    /// <summary>obj.GetType().GetProperty(name).GetValue(obj).</summary>
    public static object GetProp(object obj, string name)
        => obj.GetType().GetProperty(name).GetValue(obj);

    /// <summary>Typed property read with a hard (T) cast (matches the old inline casts).</summary>
    public static T GetProp<T>(object obj, string name) => (T)GetProp(obj, name);

    /// <summary>Property read that tolerates a MISSING property, returning null (matches the
    /// old <c>obj.GetType().GetProperty(name)?.GetValue(obj)</c> call-sites, incl. the
    /// <c>GetProp(a) ?? GetProp(b)</c> "try alternate name" fallbacks). Note: a present property
    /// whose value is null also yields null — same as before.</summary>
    public static object GetPropOrNull(object obj, string name)
        => obj?.GetType().GetProperty(name)?.GetValue(obj);

    /// <summary>Indexer read: GetProperty("Item", indexTypes).GetValue(obj, index).</summary>
    public static object GetIndexer(object obj, params object[] index)
    {
        var types = index.Select(a => a.GetType()).ToArray();
        return obj.GetType().GetProperty("Item", types).GetValue(obj, index);
    }

    /// <summary>obj.GetType().GetMethod(name).Invoke(obj, args) — name-only overload resolution.</summary>
    public static object Invoke(object obj, string method, params object[] args)
        => obj.GetType().GetMethod(method).Invoke(obj, args);

    /// <summary>Typed invoke with a hard (T) cast.</summary>
    public static T Invoke<T>(object obj, string method, params object[] args)
        => (T)Invoke(obj, method, args);

    /// <summary>Invoke disambiguated by an explicit parameter-type signature (for overloaded
    /// methods, e.g. ToString(TextSpan) vs ToString()).</summary>
    public static object Invoke(object obj, string method, Type[] sig, object[] args)
        => obj.GetType().GetMethod(method, sig).Invoke(obj, args);

    /// <summary>Invoke an async method, await the returned Task, and return its Result as object.
    /// Pattern: (Task)GetMethod(name).Invoke(...); await; task.GetProperty("Result").GetValue(task).</summary>
    public static async Task<object> InvokeAsync(object obj, string method, params object[] args)
    {
        var task = (Task)obj.GetType().GetMethod(method).Invoke(obj, args);
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result").GetValue(task);
    }

    /// <summary>Field read, including non-public fields (e.g. BufferedFindUsagesContext._state).
    /// Returns null if the field doesn't exist (matches the current <c>?.GetValue</c> call-sites).</summary>
    public static object GetField(object obj, string name,
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance)
        => obj.GetType().GetField(name, flags)?.GetValue(obj);

    /// <summary>Activator.CreateInstance, optionally binding a non-public parameterless ctor.</summary>
    public static object CreateInstance(Type t, bool nonPublic = false)
        => Activator.CreateInstance(t, nonPublic);

    /// <summary>Activator.CreateInstance with constructor arguments.</summary>
    public static object CreateInstance(Type t, params object[] args)
        => Activator.CreateInstance(t, args);
}
