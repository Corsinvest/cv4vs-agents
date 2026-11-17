/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The "all changes" window of Visual Studio 18 (<c>IMultiDiffService</c> and the types around it), reached by
/// reflection: the SDK this extension is built against (17.0) has none of them, and they are marked as not finalised, so a
/// compile-time reference would tie the extension to one build of the editor and could not load on 17.x at all.
/// <para>Every member used is looked up by name once, and one that is missing means the window is not used: the caller
/// hides the button. The types are taken from the assembly the service itself comes from, since a type found by name alone
/// was not found on the Visual Studio this was tried on, while the service was.</para>
/// <para>Editor-free on purpose: a text buffer is whatever object the caller hands in, so the lookups can be tested
/// against types of the same names.</para></summary>
internal sealed class MultiDiffShape
{
    /// <summary>The contract the window's service is exported under.</summary>
    public const string ServiceContract = "Microsoft.VisualStudio.Text.Differencing.IMultiDiffService";

    private const string Namespace = "Microsoft.VisualStudio.Text.Differencing.";

    private readonly object _service;
    private readonly MethodInfo _createToolWindowSession;
    private readonly MethodInfo _addDiff;
    private readonly Type _inputType;
    private readonly Type _entryType;
    private readonly Type _infoType;
    private readonly PropertyInfo _inputTitle;
    private readonly PropertyInfo _entryLeft;
    private readonly PropertyInfo _entryRight;
    private readonly PropertyInfo _entryTitle;
    private readonly PropertyInfo _infoUri;
    private readonly PropertyInfo _infoBuffer;
    private readonly PropertyInfo _infoReadOnly;

    private MultiDiffShape(object service, MethodInfo create, MethodInfo addDiff, Type input, Type entry, Type info,
                           PropertyInfo inputTitle, PropertyInfo entryLeft, PropertyInfo entryRight, PropertyInfo entryTitle,
                           PropertyInfo infoUri, PropertyInfo infoBuffer, PropertyInfo infoReadOnly)
    {
        _service = service;
        _createToolWindowSession = create;
        _addDiff = addDiff;
        _inputType = input;
        _entryType = entry;
        _infoType = info;
        _inputTitle = inputTitle;
        _entryLeft = entryLeft;
        _entryRight = entryRight;
        _entryTitle = entryTitle;
        _infoUri = infoUri;
        _infoBuffer = infoBuffer;
        _infoReadOnly = infoReadOnly;
    }

    /// <summary>Look the service's types up. Null, with <paramref name="why"/>, when anything that is needed is not there.</summary>
    public static MultiDiffShape TryResolve(object service, out string why)
    {
        why = null;
        if (service is null)
        {
            why = "there is no service";
            return null;
        }

        var contract = service.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == ServiceContract);
        if (contract is null)
        {
            why = $"the service does not implement {ServiceContract}";
            return null;
        }

        var assembly = contract.Assembly;
        var input = assembly.GetType(Namespace + "MultiDiffInput");
        var entry = assembly.GetType(Namespace + "DiffEntry");
        var info = assembly.GetType(Namespace + "DiffableTextInfo");
        var session = assembly.GetType(Namespace + "IMultiDiffSession");
        if (input is null || entry is null || info is null || session is null)
        {
            why = "one of MultiDiffInput, DiffEntry, DiffableTextInfo and IMultiDiffSession is missing";
            return null;
        }

        var create = contract.GetMethod("CreateToolWindowSession", new[] { input });
        var addDiff = session.GetMethod("AddDiff", new[] { entry });
        var inputTitle = input.GetProperty("DisplayTitle");
        var entryLeft = entry.GetProperty("Left");
        var entryRight = entry.GetProperty("Right");
        var entryTitle = entry.GetProperty("DisplayTitle");
        var infoUri = info.GetProperty("TextUri");
        var infoBuffer = info.GetProperty("TextBuffer");
        var infoReadOnly = info.GetProperty("ReadOnly");

        var missing = new[]
        {
            (create, "CreateToolWindowSession"), (addDiff, "AddDiff"),
        }.Where(m => m.Item1 is null).Select(m => m.Item2)
         .Concat(new[]
         {
             (inputTitle, "MultiDiffInput.DisplayTitle"), (entryLeft, "DiffEntry.Left"), (entryRight, "DiffEntry.Right"),
             (entryTitle, "DiffEntry.DisplayTitle"), (infoUri, "DiffableTextInfo.TextUri"),
             (infoBuffer, "DiffableTextInfo.TextBuffer"), (infoReadOnly, "DiffableTextInfo.ReadOnly"),
         }.Where(p => p.Item1 is null).Select(p => p.Item2))
         .ToList();
        if (missing.Count > 0)
        {
            why = "not found: " + string.Join(", ", missing);
            return null;
        }

        return new MultiDiffShape(service, create, addDiff, input, entry, info,
                                  inputTitle, entryLeft, entryRight, entryTitle, infoUri, infoBuffer, infoReadOnly);
    }

    /// <summary>The type the editor wants a text buffer to be, for whoever has to make one.</summary>
    public Type BufferType => _infoBuffer.PropertyType;

    /// <summary>Open the window, empty, and give back its session.</summary>
    public object CreateSession(string title)
    {
        var input = Activator.CreateInstance(_inputType);
        _inputTitle.SetValue(input, title);
        return Invoke(_createToolWindowSession, _service, input);
    }

    /// <summary>One file's two sides: the baseline in a buffer of the caller's making, read-only, and the file itself by its
    /// address, which the window opens through its own resolver, so that its side is the live document and can be edited.</summary>
    public object NewEntry(string title, object baselineBuffer, string path)
    {
        var left = Activator.CreateInstance(_infoType);
        _infoBuffer.SetValue(left, baselineBuffer);
        _infoReadOnly.SetValue(left, true);

        var right = Activator.CreateInstance(_infoType);
        _infoUri.SetValue(right, new Uri(path));
        _infoReadOnly.SetValue(right, false);

        var entry = Activator.CreateInstance(_entryType);
        _entryLeft.SetValue(entry, left);
        _entryRight.SetValue(entry, right);
        _entryTitle.SetValue(entry, title);
        return entry;
    }

    public void AddDiff(object session, object entry) => Invoke(_addDiff, session, entry);

    // A reflected call wraps what the callee threw: the caller wants that, as it was thrown. The log of an integration nobody can
    // step through is the stack of the callee, and a new exception made here would show this method instead.
    private static object Invoke(MethodInfo method, object target, object argument)
    {
        try { return method.Invoke(target, new[] { argument }); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
