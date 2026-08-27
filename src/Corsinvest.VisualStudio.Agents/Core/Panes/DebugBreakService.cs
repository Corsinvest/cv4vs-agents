/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Ide;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>Offers a chat when the debugger stops on something the user didn't plan for. Raises the
/// break InfoBar over the file it happened in; pressing it asks a chat pane about the break. The
/// press is the consent — nothing here starts a turn on its own.
/// <para>An exception is a surprise and worth an offer. A breakpoint is not: the user placed it and
/// knows why they are there, so it is opt-in. Steps never notify — one bar per F10 is noise — and a
/// break landing where the previous one did is dropped, or a breakpoint inside a loop raises the
/// same bar on every iteration.</para></summary>
internal static class DebugBreakService
{
    private static DebugBreakInfoBar _bar;
    private static string _lastKey;

    // Bumped by every resume, so a read that outlives its break can tell and drop its answer.
    private static int _generation;

    /// <summary>The debugger entered break mode on something other than a step. Reads the location
    /// through <see cref="IdeDebugService"/> — the same view the debug tools report — so the bar and
    /// the model can't disagree. A break carrying no exception is a breakpoint.</summary>
    public static async Task NotifyBreakAsync(bool notifyOnBreakpoint)
    {
        var generation = _generation;
        var state = await ReadSettledStateAsync(generation);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Resumed while the location was being read — this answer is about a break already over.
        if (generation != _generation) { return; }
        if (state == null || !string.Equals(state.Mode, "break", StringComparison.Ordinal)) { return; }

        var isException = !string.IsNullOrEmpty(state.ExceptionType);
        if (!isException && !notifyOnBreakpoint) { return; }

        // Same place as the break before it: the user is already looking at that bar.
        var key = $"{state.CurrentFile}|{state.CurrentLine}|{state.ExceptionType}";
        if (string.Equals(key, _lastKey, StringComparison.Ordinal)) { return; }

        CloseBar();
        _lastKey = key;

        _bar = DebugBreakInfoBar.TryShow(
            Describe(state),
            state.CurrentFile,
            onAsk: () => Ask(state),
            onClosed: () => _bar = null);
    }

    /// <summary>The break location, once the debugger agrees with itself about it. Asked the instant
    /// the event fires it answers mid-settle — the opening brace rather than the throwing line, and
    /// no $exception yet, which reads as "no exception" and would drop the very break worth
    /// offering. So it retries briefly, and takes the first answer carrying one.</summary>
    private static async Task<IdeDebugService.DebugState> ReadSettledStateAsync(int generation)
    {
        const int attempts = 5;
        const int delayMs = 120;

        IdeDebugService.DebugState last = null;
        for (var i = 0; i < attempts; i++)
        {
            if (generation != _generation) { return last; }

            last = await IdeDebugService.Instance.GetStateAsync();
            if (last == null || !string.Equals(last.Mode, "break", StringComparison.Ordinal)) { return last; }
            if (!string.IsNullOrEmpty(last.ExceptionType)) { return last; }

            if (i < attempts - 1) { await Task.Delay(delayMs); }
        }
        return last;
    }

    /// <summary>Execution resumed, or the session ended. Clears the de-dup key too: stopping at the
    /// same line after running on is a new event, unlike the same line reported twice without ever
    /// leaving break mode.</summary>
    public static void Clear()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _generation++;
        _lastKey = null;
        CloseBar();
    }

    private static void CloseBar()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_bar == null) { return; }
        _bar.Close();
        _bar = null;
    }

    private static string Describe(IdeDebugService.DebugState state)
        => string.IsNullOrEmpty(state.ExceptionType)
            ? $"Debugger paused{Where(state)}."
            : $"Debugger paused on {Short(state.ExceptionType)}{Where(state)}.";

    private static string Where(IdeDebugService.DebugState state)
    {
        if (string.IsNullOrEmpty(state.CurrentFile)) { return string.Empty; }
        var name = Path.GetFileName(state.CurrentFile);
        return state.CurrentLine > 0 ? $" at {name}:{state.CurrentLine}" : $" in {name}";
    }

    /// <summary>Last segment of a namespace-qualified type — the bar has one line to spend, and
    /// "DivideByZeroException" identifies it as well as the full name does.</summary>
    private static string Short(string type)
    {
        var dot = type.LastIndexOf('.');
        return dot >= 0 && dot < type.Length - 1 ? type.Substring(dot + 1) : type;
    }

    /// <summary>Hands the question to a chat pane the way the editor prompts do: the last activated
    /// one, brought forward first, or the answer arrives in a tab nobody is looking at.</summary>
    private static void Ask(IdeDebugService.DebugState state)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var prompt = Prompt(state);
            var target = PaneRegistry.Instance.OfKind(PaneKind.Chat).LastOrDefault(e => e.SetComposerAction != null);
            if (target == null)
            {
                // First profile = the native "Claude" that ProfileStore prepends.
                var profile = ProfileStore.Load(forEdit: false).FirstOrDefault();
                if (profile != null) { PaneLauncher.OpenNew(PaneKind.Chat, profile, initialPrompt: prompt); }
                return;
            }

            target.ActivateAction?.Invoke();
            target.SetComposerAction(prompt, true);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("DebugBreakService.Ask", ex);
        }
    }

    /// <summary>Says a break is there to look at, and nothing the debug tools would read better.
    /// <para>Naming the type up front anchors the answer on the outermost exception, where the
    /// cause is usually an InnerException two levels down. The second line is the one worth
    /// spending: those same tools step and continue, and nothing tells them the user is standing
    /// in this break watching it.</para></summary>
    private static string Prompt(IdeDebugService.DebugState state)
    {
        // On a breakpoint "why did this happen" answers itself — the user put it there.
        var ask = string.IsNullOrEmpty(state.ExceptionType)
            ? "The debugger is paused. Look at it and tell me what is going on here."
            : "The debugger is paused on an exception. Look at it and tell me why.";

        return $"{ask}\nDo not move the debugger — I am looking at this break.";
    }
}
