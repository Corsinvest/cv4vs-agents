/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// The Test Explorer, reached through its own services rather than around them: the tests it has
/// discovered, the runs it performs, and the failures it recorded — the IDE's build and its active
/// configuration, not a second opinion from an external runner.
/// <para><b>All late-bound, and not by preference.</b> The types live in
/// <c>Microsoft.VisualStudio.TestWindow.Interfaces</c>, which is unreferenceable here: it wants
/// <c>Microsoft.VisualStudio.GraphModel 18.0</c> while the VS SDK package pins 17.0, and MSBuild
/// settles that by dropping the reference — silently, since MSB3277 is a message in this project.
/// Most of what is needed is <c>internal</c> anyway. At runtime the assembly is in VS's AppDomain
/// with the version it wants, so everything here goes through <see cref="VsReflection"/>.</para>
/// <para>One probe decides availability once, and every entry point returns
/// <c>supported=false</c> rather than throwing when a future VS reshapes these names.</para>
/// </summary>
internal static class IdeTestService
{
    private const string Ns = "Microsoft.VisualStudio.TestWindow.Extensibility.";
    private const string MsgNs = "Microsoft.VisualStudio.TestWindow.Messages.";

    // A null query is never handed to the service: it dereferences it inside ToSearchQuery, and the
    // NullReferenceException that comes back names nothing the caller can act on.
    private const string QueryFailed =
        "Could not build the test query — this version of Visual Studio shapes TestQuery differently.";

    private static readonly object _probeGate = new();
    private static bool _probed;
    private static bool _available;
    private static string _unavailableReason;

    // The one VsTestService instance, held through its two faces: IVsTestService runs and lists,
    // IVsTestServiceInternal reads the per-test failures. Same object, so it is resolved once.
    private static object _service;
    private static Type _testQueryType;

    /// <summary>Whether the Test Explorer's services answered. False also carries the reason, which
    /// the tools report instead of a bare "not supported".</summary>
    public static bool IsAvailable(out string reason)
    {
        lock (_probeGate)
        {
            if (!_probed) { Probe(); }
            reason = _unavailableReason;
            return _available;
        }
    }

    private static void Probe()
    {
        _probed = true;
        // `step` names the last thing tried, so a future VS that reshapes this says WHERE it broke.
        var step = "IComponentModel";
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var components = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            if (components == null) { Fail(step); return; }

            step = "IVsTestService type";
            var serviceType = VsReflection.FindType(Ns + "IVsTestService");
            if (serviceType == null) { Fail(step); return; }

            step = "IVsTestService instance";
            _service = typeof(IComponentModel).GetMethod(nameof(IComponentModel.GetService))
                                              ?.MakeGenericMethod(serviceType)
                                              .Invoke(components, null);
            if (_service == null) { Fail(step); return; }

            step = "TestQuery type";
            _testQueryType = VsReflection.FindType(Ns + "TestQuery");
            if (_testQueryType == null) { Fail(step); return; }

            // Checked here rather than on the first call: the empty query is how every unfiltered
            // call starts, so a rename of AllTests should read as "unavailable" at startup instead
            // of as a failure halfway through a run.
            step = "TestQuery.AllTests";
            if (BuildQuery(null) == null) { Fail(step); return; }

            _available = true;
        }
        catch (Exception ex)
        {
            _unavailableReason = $"{step}: {ex.GetType().Name}: {ex.Message}";
            OutputWindowLogger.Global.Warn($"[test] Test Explorer services unavailable — {_unavailableReason}");
        }
    }

    private static void Fail(string step)
    {
        _unavailableReason = $"{step} not found";
        OutputWindowLogger.Global.Warn($"[test] Test Explorer services unavailable — {_unavailableReason}");
    }

    /// <summary>A TestQuery for everything, or for the tests whose fully-qualified name contains one
    /// of <paramref name="filters"/>.
    /// <para>Not <c>Activator.CreateInstance</c>: TestQuery's parameterless constructor is PRIVATE,
    /// so that throws "no parameterless constructor defined". The empty query is reached through the
    /// static <c>AllTests</c> instead — which is what the Test Explorer itself uses — and a filtered
    /// one through the public <c>(TestPropertyType, IEnumerable&lt;string&gt;, FilterMatchKind)</c>
    /// constructor.</para></summary>
    private static object BuildQuery(IEnumerable<string> filters)
    {
        var list = filters?.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray() ?? [];
        if (list.Length == 0)
        {
            // get_AllTests, not GetProperty("AllTests"): in the IL it is a bare static getter with
            // no property entry alongside it, so asking for the property yields null.
            return _testQueryType.GetMethod("get_AllTests", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                ?.Invoke(null, null);
        }

        // Match on the fully-qualified name, the one identifier a caller can reasonably know: a
        // filter is a substring of it, so "FactorialTests" takes a class and a full name takes one
        // test. Values go in together — the query ORs them, which is what several filters mean.
        var propertyType = VsReflection.FindType(Ns + "TestPropertyType");
        var matchKind = VsReflection.FindType(Ns + "FilterMatchKind");
        // NonPublic as well: that constructor is `internal`, so GetConstructors() alone finds
        // nothing and the query silently comes back null — which the service then dereferences.
        var ctor = _testQueryType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 .FirstOrDefault(c => c.GetParameters().Length == 3);
        if (ctor == null)
        {
            OutputWindowLogger.Global.Warn($"[test] {QueryFailed} — no 3-argument TestQuery constructor.");
            return null;
        }
        return ctor.Invoke([
            Enum.Parse(propertyType, "FullyQualifiedName"),
            list,
            Enum.Parse(matchKind, "Contains"),
        ]);
    }

    /// <summary>The tests the Test Explorer has discovered — its tree, not a fresh discovery run.
    /// Sorted by project, then class, then name, because the window's own order is not stable and a
    /// caller diffing two listings should not see phantom moves.</summary>
    public static async Task<IReadOnlyList<TestInfo>> ListTestsAsync(
        IEnumerable<string> filters = null, CancellationToken ct = default)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        if (!IsAvailable(out _)) { return null; }

        var query = BuildQuery(filters);
        if (query == null) { return null; }

        var nodes = await CallAsync("IVsTestService", "GetTestsAsync", query, ct);
        return ((IEnumerable)nodes ?? Array.Empty<object>())
            .Cast<object>()
            // A node is a group (project, namespace, class) or a test; only the leaves are tests.
            .Where(n => Read(n, "IsTest") as bool? == true)
            .Select(ToTestInfo)
            .OrderBy(t => t.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.FullyQualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolve one of the Test Explorer's interfaces by name, from either namespace it
    /// might live in.</summary>
    private static Type Face(string interfaceName)
        => VsReflection.FindType(Ns + interfaceName) ?? VsReflection.FindType(MsgNs + interfaceName);

    /// <summary>Call one of the service's methods through the interface that declares it, awaiting
    /// the Task it returns, and turn a failure into a null the callers already read as
    /// <c>supported=false</c>.</summary>
    private static async Task<object> CallAsync(string interfaceName, string method, params object[] args)
    {
        try { return await VsReflection.InvokeAsyncOn(Face(interfaceName), _service, method, args); }
        catch (Exception ex)
        {
            // The Test Explorer throws from inside its own Task when it is not ready — a
            // NullReferenceException out of GetTestsAsync before any discovery has run. Nothing
            // here can fix that, but letting it surface as a raw MCP error tells the caller
            // "something broke" where the truth is "ask again once tests exist".
            var real = (ex as TargetInvocationException)?.InnerException ?? ex;
            OutputWindowLogger.Global.Warn($"[test] {interfaceName}.{method} failed: {real.GetType().Name}: {real.Message}");
            return null;
        }
    }

    private static object Read(object obj, string name, Type declaring = null)
        => VsReflection.GetPropSafe(obj, name, declaring);

    private static TestInfo ToTestInfo(object node) => new()
    {
        Id = Read(node, "Id") as int? ?? 0,
        DisplayName = Read(node, "DisplayName") as string ?? "",
        FullyQualifiedName = Read(node, "FullyQualifiedName") as string ?? "",
        Project = Read(node, "ProjectName") as string ?? "",
        Namespace = Read(node, "NamespaceName") as string ?? "",
        ClassName = Read(node, "ClassName") as string ?? "",
        Source = Read(node, "Source") as string ?? "",
        TargetFramework = Read(node, "TargetFramework") as string ?? "",
    };

    /// <summary>Run the matching tests and wait for the run to end — the Task completes when the
    /// Test Explorer is done, not when it starts, so the caller decides how long to wait through
    /// <paramref name="ct"/>. Cancelling the token stops the run: there is no separate cancel call.
    /// <para><paramref name="debug"/> runs them under the IDE's debugger, which is the one thing no
    /// external runner can offer — the debug_* tools take over wherever it breaks.</para></summary>
    public static async Task<TestRunOutcome> RunTestsAsync(
        IEnumerable<string> filters = null, bool debug = false, CancellationToken ct = default)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        if (!IsAvailable(out var reason)) { return new TestRunOutcome { Supported = false, Message = reason }; }

        var query = BuildQuery(filters);
        if (query == null)
        {
            return new TestRunOutcome { Supported = false, Message = QueryFailed };
        }

        var method = debug ? "DebugTestsAsync" : "RunTestsAsync";
        var started = await CallAsync("IVsTestService", method, query, ct);
        // The service answers bool: false is "the run did not start" (nothing matched the filter, a
        // build failed, a run already going) — not "tests failed", which the results tell.
        return new TestRunOutcome { Supported = true, Started = started as bool? ?? false };
    }

    /// <summary>The last run's outcome per test: what failed, why, and where. The counts alone
    /// ("2 failed") are not actionable — this is the part that lets the caller go to the code.
    /// <para>Failures first, then the rest: on a red run that is the whole point of asking, and a
    /// caller reading only the first entries still gets what matters.</para></summary>
    public static async Task<IReadOnlyList<TestOutcome>> GetResultsAsync(
        IEnumerable<string> filters = null, bool failedOnly = false, CancellationToken ct = default)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        if (!IsAvailable(out _)) { return null; }

        var query = BuildQuery(filters);
        if (query == null) { return null; }

        // The details hang off IVsTestServiceInternal, which the same object implements — its own
        // interface because it carries the per-test failures the public one does not expose.
        var nodes = await CallAsync("IVsTestService", "GetTestsAsync", query, ct);
        var results = new List<TestOutcome>();

        foreach (var node in ((IEnumerable)nodes ?? Array.Empty<object>()).Cast<object>())
        {
            if (Read(node, "IsTest") as bool? != true) { continue; }
            ct.ThrowIfCancellationRequested();

            var name = Read(node, "FullyQualifiedName") as string ?? "";
            object details;
            try
            {
                // viewId null = the current view. Passing the token as the third argument keeps a
                // long listing cancellable per test rather than only between tests.
                details = await CallAsync("IVsTestServiceInternal", "GetTestResultDetailsAsync", node, null, ct);
            }
            catch (Exception ex)
            {
                // One unreadable test must not lose the other results — the run is what it is, and
                // silence here would read as "this test passed".
                OutputWindowLogger.Global.Warn($"[test] no result details for {name}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // The outcome comes off the NODE, which also implements ITestNodeRunDetails — the enum
            // the test platform itself uses. Reading it from ErrorMessage instead would call a
            // skipped test failed: the reason a test was skipped is carried in that same field.
            var outcome = Read(node, "Outcome", Face("ITestNodeRunDetails"))?.ToString();

            foreach (var r in ((IEnumerable)details ?? Array.Empty<object>()).Cast<object>())
            {
                var failed = string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase);
                if (failedOnly && !failed) { continue; }
                results.Add(new TestOutcome
                {
                    FullyQualifiedName = name,
                    DisplayName = Read(r, "DisplayName") as string ?? name,
                    Project = Read(node, "ProjectName") as string ?? "",
                    // The platform's own word — Passed / Failed / Skipped / NotFound / None —
                    // rather than a boolean that would have to guess what "not passed" meant.
                    Outcome = outcome ?? "Unknown",
                    Failed = failed,
                    ErrorMessage = Read(r, "ErrorMessage") as string,
                    ErrorStackTrace = Read(r, "ErrorStackTrace") as string,
                    DurationMs = Read(r, "DurationInMs") as long? ?? 0,
                });
            }
        }

        return [.. results.OrderByDescending(r => r.Failed)
                          .ThenBy(r => r.FullyQualifiedName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One test's outcome from the last run. The stack trace is the CLI's own text, which
    /// carries file and line — the caller reads them from there rather than being handed a parse.</summary>
    public sealed class TestOutcome
    {
        public string FullyQualifiedName { get; set; }
        public string DisplayName { get; set; }
        public string Project { get; set; }
        /// <summary>The test platform's own outcome: Passed, Failed, Skipped, NotFound, None.</summary>
        public string Outcome { get; set; }
        public bool Failed { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorStackTrace { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>How a run ended. `Started` false means the Test Explorer declined to run at all —
    /// read `Message`, then the results, to tell that apart from a run whose tests failed.</summary>
    public sealed class TestRunOutcome
    {
        public bool Supported { get; set; }
        public bool Started { get; set; }
        public string Message { get; set; }
    }

    /// <summary>One discovered test, flattened out of the Test Explorer's tree.</summary>
    public sealed class TestInfo
    {
        public int Id { get; set; }
        public string DisplayName { get; set; }
        public string FullyQualifiedName { get; set; }
        public string Project { get; set; }
        public string Namespace { get; set; }
        public string ClassName { get; set; }
        /// <summary>The container it was found in — the built assembly.</summary>
        public string Source { get; set; }
        public string TargetFramework { get; set; }
    }
}
