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
/// idioms duplicated across the Ide/ services. A missing member throws; the typed overloads use a
/// hard <c>(T)</c> cast. Callers that tolerate a missing member must null-check the returned
/// <see cref="object"/>.
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

    /// <summary>Typed property read with a hard (T) cast.</summary>
    public static T GetProp<T>(object obj, string name) => (T)GetProp(obj, name);

    /// <summary>Property read that tolerates a MISSING property, returning null — used by the
    /// <c>GetProp(a) ?? GetProp(b)</c> "try alternate name" fallbacks. Note: a present property
    /// whose value is null also yields null, so the two cases are indistinguishable.</summary>
    public static object GetPropOrNull(object obj, string name)
        => obj?.GetType().GetProperty(name)?.GetValue(obj);

    /// <summary>Property read off an object whose concrete type declares the name more than once.
    /// <para><see cref="GetPropOrNull"/> throws AmbiguousMatchException there, and VS hands out
    /// plenty of such objects: one implementation wearing several interfaces that share member
    /// names. Pass <paramref name="declaring"/> — the interface the contract actually belongs to —
    /// and the choice never arises; an implementation that later grows another face cannot break
    /// the read. Without it, an ambiguous name falls back to the first match, since the faces of
    /// one object agree on the value.</para>
    /// Returns null for a missing member as well as for a null value, like GetPropOrNull.</summary>
    public static object GetPropSafe(object obj, string name, Type declaring = null)
    {
        if (obj == null) { return null; }
        try { return (declaring ?? obj.GetType()).GetProperty(name)?.GetValue(obj); }
        catch (AmbiguousMatchException)
        {
            return obj.GetType().GetProperties().FirstOrDefault(p => p.Name == name)?.GetValue(obj);
        }
    }

    /// <summary>Invoke an async method declared by <paramref name="declaring"/> rather than by the
    /// object's concrete type, and await it. Same reason as <see cref="GetPropSafe"/>: resolving a
    /// method on an implementation that wears several interfaces is ambiguous, while the interface
    /// that declares it is unambiguous and is the contract being relied on.
    /// <para>Returns null when the method is not there. Unlike <see cref="InvokeAsync"/> it does not
    /// swallow what the call itself throws — a service failing is the caller's to interpret.</para></summary>
    public static async Task<object> InvokeAsyncOn(Type declaring, object obj, string method, params object[] args)
    {
        var mi = declaring?.GetMethod(method);
        if (mi == null) { return null; }
        var task = (Task)mi.Invoke(obj, args);
        await task.ConfigureAwait(true);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

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
    /// Returns null if the field doesn't exist.</summary>
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
