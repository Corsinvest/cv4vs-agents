/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The review UI of GitHub Copilot in Visual Studio 18 (the hunk buttons on a change, the panel at the bottom of the
/// editor), reached by reflection through the parts Copilot exports: <c>IAiChangesMarker</c> and <c>IBaselineStateRegistry</c>,
/// with <c>IFileBaselineState</c> and the types around it. The editor draws nothing of this itself, so there is nothing to call
/// but Copilot, and Copilot's assemblies are not something this extension can reference: it is not there on every Visual Studio,
/// and what it exposes is not a documented API. A member that is missing means the review is not offered, and nothing is drawn.
/// <para>Every member used is looked up by name once, on the interface and on the interfaces it inherits. The contract is the
/// interface's full name, which is also how the parts are exported, and the types are taken from the members that use them (the
/// interaction id from the marker's own parameter, the state from the registry's), so no assembly name is written down.</para>
/// <para>Editor-free on purpose: a text view or snapshot is whatever object the caller hands in, so the lookups can be tried
/// against types of the same names (see <c>CopilotReviewShapeTests</c>).</para></summary>
internal sealed class CopilotReviewShape
{
    public const string MarkerContract = "Microsoft.VisualStudio.Copilot.EditingStateTracking.IAiChangesMarker";
    public const string RegistryContract = "Microsoft.VisualStudio.Copilot.EditingStateTracking.IBaselineStateRegistry";

    private readonly object _marker;
    private readonly object _registry;
    private readonly MethodInfo _mark;
    private readonly MethodInfo _markCompleted;
    private readonly MethodInfo _tryGet;
    private readonly MethodInfo _getOrCreate;
    private readonly MethodInfo _release;
    private readonly MethodInfo _getSnapshot;
    private readonly MethodInfo _updateDiffState;
    private readonly MethodInfo _requestDismissal;
    private readonly PropertyInfo _baselineFilePath;
    private readonly PropertyInfo _changes;
    private readonly PropertyInfo _sessionOfId;
    private readonly PropertyInfo _clientOfSession;
    private readonly Type _idType;
    private readonly Type _diffStateType;
    private readonly Type _stateType;
    private readonly EventInfo _diffChanged;
    private readonly EventInfo _baselineChanged;
    private readonly EventInfo _dismissRequested;

    private CopilotReviewShape(object marker, object registry, MethodInfo mark, MethodInfo markCompleted, MethodInfo tryGet,
                               MethodInfo getOrCreate, MethodInfo release, MethodInfo getSnapshot, MethodInfo updateDiffState,
                               MethodInfo requestDismissal, PropertyInfo baselineFilePath, PropertyInfo changes,
                               PropertyInfo sessionOfId, PropertyInfo clientOfSession, Type idType, Type diffStateType,
                               Type stateType, EventInfo diffChanged, EventInfo baselineChanged, EventInfo dismissRequested)
    {
        _marker = marker;
        _registry = registry;
        _mark = mark;
        _markCompleted = markCompleted;
        _tryGet = tryGet;
        _getOrCreate = getOrCreate;
        _release = release;
        _getSnapshot = getSnapshot;
        _updateDiffState = updateDiffState;
        _requestDismissal = requestDismissal;
        _baselineFilePath = baselineFilePath;
        _changes = changes;
        _sessionOfId = sessionOfId;
        _clientOfSession = clientOfSession;
        _idType = idType;
        _diffStateType = diffStateType;
        _stateType = stateType;
        _diffChanged = diffChanged;
        _baselineChanged = baselineChanged;
        _dismissRequested = dismissRequested;
    }

    /// <summary>Which parts were found, for the log: the types they are and the assemblies those come from.</summary>
    public string Description
    {
        get
        {
            var marker = _marker.GetType();
            var registry = _registry.GetType();
            return $"{marker.FullName} ({marker.Assembly.GetName().Version}), {registry.FullName} ({registry.Assembly.GetName().Version})";
        }
    }

    /// <summary>Look the parts up. Null, with <paramref name="why"/>, when anything that is needed is not there.</summary>
    public static CopilotReviewShape TryResolve(object marker, object registry, out string why)
    {
        why = null;
        if (marker is null || registry is null)
        {
            why = marker is null ? "there is no marker part" : "there is no registry part";
            return null;
        }

        var markerType = marker.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == MarkerContract);
        var registryType = registry.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == RegistryContract);
        if (markerType is null || registryType is null)
        {
            why = markerType is null ? $"the marker does not implement {MarkerContract}" : $"the registry does not implement {RegistryContract}";
            return null;
        }

        var mark = FindMethod(markerType, "MarkForAiChangesAsync", 3);
        var markCompleted = FindMethod(markerType, "MarkAiChangesCompleted", 2);
        var tryGet = FindMethod(registryType, "TryGetActiveBaselineState", 2);
        var getOrCreate = FindMethod(registryType, "GetOrCreateBaselineStateAsync", 3);
        var release = FindMethod(registryType, "ReleaseBaselineStateAsync", 2);

        var missing = new List<string>();
        void Need(object found, string name)
        {
            if (found is null) { missing.Add(name); }
        }
        Need(mark, "MarkForAiChangesAsync");
        Need(markCompleted, "MarkAiChangesCompleted");
        Need(tryGet, "TryGetActiveBaselineState");
        Need(getOrCreate, "GetOrCreateBaselineStateAsync");
        Need(release, "ReleaseBaselineStateAsync");
        if (missing.Count > 0)
        {
            why = "not found: " + string.Join(", ", missing);
            return null;
        }

        // The types come from the members that use them.
        var idType = mark.GetParameters()[1].ParameterType;
        var stateType = tryGet.GetParameters()[1].ParameterType.GetElementType();
        if (stateType is null)
        {
            why = "TryGetActiveBaselineState has no out parameter for the state";
            return null;
        }

        var getSnapshot = FindMethod(stateType, "GetBaselineSnapshot", 0);
        var updateDiffState = FindMethod(stateType, "UpdateDiffState", 1);
        var requestDismissal = FindMethod(stateType, "RequestDismissal", 0);
        var baselineFilePath = FindProperty(stateType, "BaselineFilePath");
        var diffStateProperty = FindProperty(stateType, "DiffState");
        var diffChanged = FindEvent(stateType, "BaselineDiffChanged");
        var baselineChanged = FindEvent(stateType, "BaselineChanged");
        var dismissRequested = FindEvent(stateType, "DismissRequested");
        var sessionOfId = FindProperty(idType, "SessionId");
        var clientOfSession = sessionOfId is null ? null : FindProperty(sessionOfId.PropertyType, "ClientId");
        var diffStateType = diffStateProperty?.PropertyType;
        var changes = diffStateType is null ? null : FindProperty(diffStateType, "Changes");

        Need(getSnapshot, "IFileBaselineState.GetBaselineSnapshot");
        Need(updateDiffState, "IFileBaselineState.UpdateDiffState");
        Need(requestDismissal, "IFileBaselineState.RequestDismissal");
        Need(baselineFilePath, "IFileBaselineState.BaselineFilePath");
        Need(diffStateProperty, "IFileBaselineState.DiffState");
        Need(diffChanged, "IFileBaselineState.BaselineDiffChanged");
        Need(baselineChanged, "IFileBaselineState.BaselineChanged");
        Need(dismissRequested, "IFileBaselineState.DismissRequested");
        Need(sessionOfId, "CopilotInteractionId.SessionId");
        Need(clientOfSession, "CopilotSessionId.ClientId");
        Need(changes, "BaselineDiffState.Changes");
        if (missing.Count > 0)
        {
            why = "not found: " + string.Join(", ", missing);
            return null;
        }

        // What is built from them: the id from three one-argument constructors, the diff state from three counts.
        var sessionType = sessionOfId.PropertyType;
        var clientType = clientOfSession.PropertyType;
        if (clientType.GetConstructor(new[] { typeof(string) }) is null
            || sessionType.GetConstructor(new[] { clientType }) is null
            || idType.GetConstructor(new[] { sessionType }) is null)
        {
            why = "the interaction id cannot be built from a client name (a constructor is missing)";
            return null;
        }
        if (diffStateType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) }) is null)
        {
            why = "BaselineDiffState cannot be built from three counts";
            return null;
        }

        return new CopilotReviewShape(marker, registry, mark, markCompleted, tryGet, getOrCreate, release, getSnapshot,
                                      updateDiffState, requestDismissal, baselineFilePath, changes, sessionOfId, clientOfSession,
                                      idType, diffStateType, stateType, diffChanged, baselineChanged, dismissRequested);
    }

    // --- what Copilot is asked ---------------------------------------------------------------------------

    /// <summary>An id for a conversation of our own: a client name, with a session made of it. Copilot only records it (its
    /// telemetry and its bookkeeping of who started a review).</summary>
    public object NewInteractionId(string clientName)
    {
        var client = Activator.CreateInstance(_clientOfSession.PropertyType, clientName);
        var session = Activator.CreateInstance(_sessionOfId.PropertyType, client);
        return Activator.CreateInstance(_idType, session);
    }

    /// <summary>A diff state of the counts given: what Copilot's own view model compares to decide whether there is a
    /// difference to draw.</summary>
    public object NewDiffState(int additions, int deletions, int changes)
        => Activator.CreateInstance(_diffStateType, additions, deletions, changes);

    /// <summary>The state Copilot holds for the file, or null when it holds none. States are Copilot's, keyed by path, and
    /// shared with whoever else asks.</summary>
    public object TryGetState(string path)
    {
        var args = new object[] { path, null };
        return (bool)Invoke(_tryGet, _registry, args) ? args[1] : null;
    }

    /// <summary>The file's state, made if there is none: it copies the open document's text as the baseline.</summary>
    public async Task<object> GetOrCreateAsync(string path, object interactionId)
    {
        var task = (Task)Invoke(_getOrCreate, _registry, new[] { path, interactionId, CancellationToken.None });
        await task.ConfigureAwait(true);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    /// <summary>Let go of the file's state, without the view model hearing of it: only for a state that no view is on.</summary>
    public async Task ReleaseAsync(string path)
        => await ((Task)Invoke(_release, _registry, new object[] { path, CancellationToken.None })).ConfigureAwait(true);

    /// <summary>Put the view into review: it is wired to the state, and the hunk buttons and the panel are drawn.</summary>
    public async Task MarkAsync(object view, object interactionId)
        => await ((Task)Invoke(_mark, _marker, new[] { view, interactionId, CancellationToken.None })).ConfigureAwait(true);

    public void MarkCompleted(object view) => Invoke(_markCompleted, _marker, new[] { view, CancellationToken.None });

    // --- what a state is asked ---------------------------------------------------------------------------

    /// <summary>The baseline as Copilot has it now, an <c>ITextSnapshot</c> of its own buffer.</summary>
    public object BaselineSnapshot(object state) => Invoke(_getSnapshot, state, Array.Empty<object>());

    /// <summary>The temp file the code window shows as the left side.</summary>
    public string BaselineFilePath(object state) => (string)_baselineFilePath.GetValue(state);

    public void UpdateDiffState(object state, object diffState) => Invoke(_updateDiffState, state, new[] { diffState });

    /// <summary>End the review the way Copilot's own Keep all and Undo all end theirs: the diff goes, the state is released,
    /// the temp file is deleted. Nothing is done to the document. Only effective while a view is on the state.</summary>
    public void RequestDismissal(object state) => Invoke(_requestDismissal, state, Array.Empty<object>());

    // --- what a state tells ------------------------------------------------------------------------------

    /// <summary>The number of hunks the difference has, as the event carries it. Raised on whichever thread worked it out.</summary>
    public IDisposable OnDiffChanged(object state, Action<int> changes)
        => Hook(_diffChanged, state, args => changes((int)_changes.GetValue(args)));

    /// <summary>A hunk was kept: the baseline is not the text it was, and what the row keeps has to follow it.</summary>
    public IDisposable OnBaselineChanged(object state, Action changed) => Hook(_baselineChanged, state, _ => changed());

    /// <summary>The state was asked to end: by Keep all, by Undo all, or by <see cref="RequestDismissal"/>. Not by the
    /// automatic clean-up of a state with no difference left, which raises nothing.</summary>
    public IDisposable OnDismissRequested(object state, Action dismissed) => Hook(_dismissRequested, state, _ => dismissed());

    // --- plumbing ----------------------------------------------------------------------------------------

    private static MethodInfo FindMethod(Type type, string name, int parameters)
        => Chain(type).SelectMany(t => t.GetMethods()).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters);

    private static PropertyInfo FindProperty(Type type, string name)
        => Chain(type).Select(t => t.GetProperty(name)).FirstOrDefault(p => p is not null);

    private static EventInfo FindEvent(Type type, string name)
        => Chain(type).Select(t => t.GetEvent(name)).FirstOrDefault(e => e is not null);

    // An interface does not list the members of the ones it inherits.
    private static IEnumerable<Type> Chain(Type type) => new[] { type }.Concat(type.GetInterfaces());

    // A reflected call wraps what the callee threw: the caller wants that, as it was thrown. The log of an integration nobody can
    // step through is the stack of the callee, and a new exception made here would show this method instead.
    private static object Invoke(MethodInfo method, object target, object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    // The argument type of an event belongs to Copilot, so no handler can be written against it: a generic relay whose type is
    // the one the event's delegate takes.
    private static IDisposable Hook(EventInfo ev, object target, Action<object> on)
    {
        var argumentType = ev.EventHandlerType.GetMethod("Invoke").GetParameters()[1].ParameterType;
        var relayType = typeof(Relay<>).MakeGenericType(argumentType);
        var relay = Activator.CreateInstance(relayType, on);
        var handler = Delegate.CreateDelegate(ev.EventHandlerType, relay, relayType.GetMethod(nameof(Relay<object>.Handle)));
        ev.AddEventHandler(target, handler);
        return new Unhook(ev, target, handler);
    }

    private sealed class Relay<T>
    {
        private readonly Action<object> _on;

        public Relay(Action<object> on) => _on = on;

        public void Handle(object sender, T args) => _on(args);
    }

    private sealed class Unhook : IDisposable
    {
        private readonly EventInfo _event;
        private readonly object _target;
        private readonly Delegate _handler;
        private int _done;

        public Unhook(EventInfo ev, object target, Delegate handler)
        {
            _event = ev;
            _target = target;
            _handler = handler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) { return; }
            _event.RemoveEventHandler(_target, _handler);
        }
    }
}
