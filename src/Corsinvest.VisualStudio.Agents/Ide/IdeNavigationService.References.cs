/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

// Find-references via the per-language IFindUsagesService. That service streams its results
// into an IFindUsagesContext instead of returning them, so we reuse Roslyn's own concrete
// BufferedFindUsagesContext (it implements context + progress tracker and buffers the hits)
// and read its private results back.
internal sealed partial class IdeNavigationService
{
    private bool _refsProbed;
    private bool _refsAvailable;
    private Type _findUsagesServiceType;             // internal IFindUsagesService
    private MethodInfo _findReferencesAsync;         // on the service
    private MethodInfo _findImplementationsAsync;    // sibling method (same shape) → goToImplementation
    private Type _bufferedContextType;               // internal BufferedFindUsagesContext (we reuse it)
    private Type _optionsProviderType;               // internal OptionsProvider<ClassificationOptions> (proxied)
    private object _classificationOptionsDefault;    // ClassificationOptions.Default boxed

    /// <summary>Resolve the find-references types/members. The one parameter we must still
    /// supply to FindReferencesAsync is an OptionsProvider&lt;ClassificationOptions&gt; — we
    /// fulfil it with a tiny RealProxy returning ClassificationOptions.Default. All internal,
    /// all feature-detected.</summary>
    /// <summary>Lay the four values we have into the parameter list the method actually declares,
    /// matching on type rather than counting positions, and leaving anything else at its default.
    /// The list has gained and lost parameters between VS versions; matching by type survives that,
    /// where a fixed array of five silently stopped working the day one of them moved.</summary>
    private static object[] BuildFindUsagesArgs(
        MethodInfo method, object context, object document, int offset, object optionsProvider, CancellationToken ct)
    {
        var parameters = method.GetParameters();
        var args = new object[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var t = parameters[i].ParameterType;
            args[i] =
                t == typeof(int) ? offset
                : t == typeof(CancellationToken) ? ct
                : t.IsInstanceOfType(context) ? context
                : t.IsInstanceOfType(document) ? document
                : t.IsInstanceOfType(optionsProvider) ? optionsProvider
                : parameters[i].HasDefaultValue ? parameters[i].DefaultValue
                : t.IsValueType ? Activator.CreateInstance(t)
                : null;
        }
        return args;
    }

    /// <summary>One of IFindUsagesService's two entry points, by name.
    ///
    /// Deliberately not pinned to an arity. It used to require exactly five parameters, and when
    /// one of the two grew a sixth the tool reported "not available in this Visual Studio" — which
    /// reads as an edition limit and sent everyone looking in the wrong place, while its sibling
    /// went on working from the same interface. If the shape ever changes for real, the invoke
    /// below fails with a message naming the mismatch, which is a better account than a probe that
    /// quietly answers no. Overloads are ordered so the widest wins: extra parameters are the ones
    /// that get added.</summary>
    private MethodInfo FindUsagesMethod(string name)
        => _findUsagesServiceType.GetMethods()
            .Where(m => m.Name == name)
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault();

    private bool EnsureRefsProbed()
    {
        if (_refsProbed) { return _refsAvailable; }
        _refsProbed = true;
        if (!EnsureProbed()) { return false; } // shares workspace + GetService<T> handles
        try
        {
            string step = "IFindUsagesService";
            _findUsagesServiceType = VsReflection.FindType("Microsoft.CodeAnalysis.FindUsages.IFindUsagesService");
            if (_findUsagesServiceType == null) { return ProbeFailed(step); }

            step = "FindReferencesAsync";
            _findReferencesAsync = FindUsagesMethod("FindReferencesAsync");
            if (_findReferencesAsync == null) { return ProbeFailed(step); }

            // Sibling method, same shape — powers goToImplementation. Optional: if a future VS
            // drops it we just disable implementations, references still work.
            _findImplementationsAsync = FindUsagesMethod("FindImplementationsAsync");

            step = "BufferedFindUsagesContext";
            _bufferedContextType = VsReflection.FindType("Microsoft.CodeAnalysis.FindUsages.BufferedFindUsagesContext");
            if (_bufferedContextType == null) { return ProbeFailed(step); }

            step = "OptionsProvider<ClassificationOptions>";
            _optionsProviderType = VsReflection.FindType("Microsoft.CodeAnalysis.OptionsProvider`1");
            var classOptionsType = VsReflection.FindType("Microsoft.CodeAnalysis.Classification.ClassificationOptions");
            if (_optionsProviderType == null || classOptionsType == null) { return ProbeFailed(step); }
            _optionsProviderType = _optionsProviderType.MakeGenericType(classOptionsType);

            step = "ClassificationOptions.Default";
            _classificationOptionsDefault = classOptionsType
                .GetField("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (_classificationOptionsDefault == null) { return ProbeFailed(step); }

            _refsAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.EnsureRefsProbed", ex);
            return false;
        }
    }

    /// <summary>Find all references to the symbol named <paramref name="symbolName"/> on
    /// 1-based <paramref name="line"/> of <paramref name="filePath"/>. Multi-language; never
    /// throws (degrades to Supported=false). The file must be in the open solution.</summary>
    public Task<NavResult> GetReferencesAsync(string filePath, int line, string symbolName, CancellationToken ct)
        => RunFindUsagesAsync(() => _findReferencesAsync, "references", filePath, line, symbolName, ct);

    /// <summary>Find all implementations of the symbol on that line — concrete classes/members that
    /// implement an interface, or override a virtual/abstract member (Go To Implementation).
    /// Different from references (callers) and definition (declaration). Multi-language; never
    /// throws. Uses the sibling FindImplementationsAsync of the same IFindUsagesService.</summary>
    public Task<NavResult> GetImplementationsAsync(string filePath, int line, string symbolName, CancellationToken ct)
        => RunFindUsagesAsync(() => _findImplementationsAsync, "implementations", filePath, line, symbolName, ct);

    /// <summary>Shared driver for the two IFindUsagesService entry points (FindReferencesAsync /
    /// FindImplementationsAsync): both stream SourceReferenceItems into the buffered context, which
    /// we then read back. <paramref name="kind"/> only flavours the messages.
    /// <para>The method arrives as a function, not as a MethodInfo, because the field it comes from
    /// is filled by the probe below. Passed directly it was read at the call site — before the probe
    /// had run — so the very first find-references of a session saw null and answered "the internal
    /// API has moved", then worked ever after. A one-off failure that read like a version problem
    /// and cleared itself on retry, which is the worst way for it to look.</para></summary>
    private async Task<NavResult> RunFindUsagesAsync(
        Func<MethodInfo> getFindMethod, string kind, string filePath, int line, string symbolName, CancellationToken ct)
    {
        // Probe first: it is what fills the field getFindMethod reads.
        var probed = EnsureRefsProbed();
        var findMethod = getFindMethod();
        if (!probed || findMethod == null)
        {
            // Not an edition limit, whatever it reads like: this is Roslyn's own service, and it
            // ships with every edition. Saying where it broke beats implying the IDE cannot do it.
            return new NavResult
            {
                Supported = false,
                Reason = $"Find-{kind} could not be wired up to this Visual Studio's language service — " +
                         "the internal API it goes through has moved. Grep finds the same text meanwhile.",
            };
        }

        try
        {
            var document = ResolveDocument(filePath);
            if (document == null) { return new NavResult { Supported = false, Reason = "No language document for this file (language not supported)." }; }

            var offset = await ResolveOffsetAsync(document, line, symbolName, ct).ConfigureAwait(false);
            if (offset < 0) { return new NavResult { Supported = true, Found = false, Reason = "Symbol not found on that line." }; }

            var service = GetLanguageService(document, _findUsagesServiceType);
            if (service == null) { return new NavResult { Supported = false, Reason = $"This language has no find-{kind} service." }; }

            // Reuse Roslyn's own buffering context (implements IFindUsagesContext + progress tracker).
            var context = VsReflection.CreateInstance(_bufferedContextType, nonPublic: true);
            var optionsProvider = OptionsProviderProxy.Create(_optionsProviderType, _classificationOptionsDefault);

            // Built from the signature rather than assumed: the parameter list has changed shape
            // across VS versions, and the four values below are the only ones we have to give —
            // anything else the method wants gets its own default rather than shifting the rest
            // out of position.
            var args = BuildFindUsagesArgs(findMethod, context, document, offset, optionsProvider, ct);
            var task = (Task)findMethod.Invoke(service, args);
            await task.ConfigureAwait(false);

            var locations = ReadBufferedReferences(context);
            return new NavResult
            {
                Supported = true,
                Found = locations.Length > 0,
                Locations = locations,
                Reason = locations.Length > 0 ? null : $"No {kind} found.",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException($"IdeNavigationService.RunFindUsagesAsync({kind})", ex);
            return new NavResult { Supported = false, Reason = $"Find-{kind} failed (internal API changed?)." };
        }
    }

    /// <summary>Read the buffered SourceReferenceItem list out of BufferedFindUsagesContext's
    /// private state (_state.References) and map each SourceSpan to file/line/col.</summary>
    private static NavLocation[] ReadBufferedReferences(object context)
    {
        var state = VsReflection.GetField(context, "_state", BindingFlags.NonPublic | BindingFlags.Instance);
        if (state == null) { return []; }
        var builder = VsReflection.GetField(state, "References"); // ImmutableArray<SourceReferenceItem>.Builder
        if (builder is not IEnumerable items) { return []; }

        var result = new System.Collections.Generic.List<NavLocation>();
        foreach (var item in items.Cast<object>())
        {
            // SourceReferenceItem.SourceSpan : DocumentSpan(Document, TextSpan)
            var span = VsReflection.GetPropOrNull(item, "SourceSpan");
            if (span == null) { continue; }
            var doc = VsReflection.GetProp(span, "Document");
            var filePath = (string)VsReflection.GetPropOrNull(doc, "FilePath");
            if (string.IsNullOrEmpty(filePath)) { continue; }
            var textSpan = VsReflection.GetProp(span, "SourceSpan");
            var start = VsReflection.GetProp<int>(textSpan, "Start");
            var (lineNo, colNo, preview) = FileOffsetToLineCol(filePath, start);
            result.Add(new NavLocation { FilePath = filePath, Line = lineNo, Column = colNo, Preview = preview });
        }
        // Roslyn collects references in parallel, so the order is nondeterministic;
        // sort for a stable result the model can compare across calls.
        return [.. result
            .OrderBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Line).ThenBy(l => l.Column)];
    }

    /// <summary>RealProxy that satisfies the single-method internal interface
    /// OptionsProvider&lt;ClassificationOptions&gt; required by FindReferencesAsync: its
    /// GetOptionsAsync(languageServices, ct) returns ValueTask&lt;ClassificationOptions&gt;
    /// wrapping ClassificationOptions.Default. RealProxy (not DispatchProxy) so we stay on
    /// the .NET Framework base — no extra package — and can target the internal interface
    /// type at runtime via GetTransparentProxy(Type).</summary>
    private sealed class OptionsProviderProxy : System.Runtime.Remoting.Proxies.RealProxy
    {
        private readonly object _defaultOptions;
        private readonly Type _classOptionsType;

        private OptionsProviderProxy(Type optionsProviderInterface, object defaultOptions)
            : base(optionsProviderInterface)
        {
            _defaultOptions = defaultOptions;
            _classOptionsType = defaultOptions.GetType();
        }

        public static object Create(Type optionsProviderInterface, object defaultOptions)
            => new OptionsProviderProxy(optionsProviderInterface, defaultOptions).GetTransparentProxy();

        public override System.Runtime.Remoting.Messaging.IMessage Invoke(
            System.Runtime.Remoting.Messaging.IMessage msg)
        {
            var call = (System.Runtime.Remoting.Messaging.IMethodCallMessage)msg;
            object ret;
            if (call.MethodName == "GetOptionsAsync")
            {
                // new ValueTask<ClassificationOptions>(Default) via the ValueTask<T>(T) ctor.
                var valueTaskType = typeof(ValueTask<>).MakeGenericType(_classOptionsType);
                ret = VsReflection.CreateInstance(valueTaskType, _defaultOptions);
            }
            else
            {
                var rt = ((MethodInfo)call.MethodBase).ReturnType;
                ret = rt.IsValueType && rt != typeof(void) ? VsReflection.CreateInstance(rt) : null;
            }
            return new System.Runtime.Remoting.Messaging.ReturnMessage(ret, null, 0, call.LogicalCallContext, call);
        }
    }
}
