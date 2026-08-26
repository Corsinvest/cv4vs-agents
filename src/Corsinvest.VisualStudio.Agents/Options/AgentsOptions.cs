/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>
/// Static facade over the Tools → Options <see cref="DialogPage"/> classes,
/// so consumers read settings without reaching into the package directly.
/// </summary>
public static class AgentsOptions
{
    public static event System.Action Applied;

    internal static void RaiseApplied() => Applied?.Invoke();

    public static AgentsGeneralPage General => Get<AgentsGeneralPage>();

    public static AgentsChatPage Chat => Get<AgentsChatPage>();

    public static AgentsDebugPage Debug => Get<AgentsDebugPage>();

    /// <summary>The live page when the package has one, otherwise a defaults-only stand-in.
    /// <para>Never null: all 23 call sites read a property straight off these, on paths where a
    /// missing setting must degrade to its default rather than throw.</para></summary>
    private static T Get<T>() where T : Microsoft.VisualStudio.Shell.DialogPage, new()
        => AgentsPackage.Instance?.GetDialogPage(typeof(T)) is T live ? live : Defaults<T>.Instance;

    /// <summary>The stand-in, built once per page type.
    /// <para>A <see cref="Microsoft.VisualStudio.Shell.DialogPage"/> constructor reaches through
    /// ThreadHelper.JoinableTaskContext to the global service provider, which exists only when
    /// Visual Studio is the host process. In a unit test that throws FileNotFoundException on
    /// Microsoft.Internal.VisualStudio.Interop — so the construction is guarded, and what is handed
    /// back instead is an instance whose property initialisers never ran: LogLevel.None and
    /// EnablePerfLog false, which is what an unconfigured page would have said anyway.</para></summary>
    private static class Defaults<T> where T : Microsoft.VisualStudio.Shell.DialogPage, new()
    {
        public static readonly T Instance = Build();

        private static T Build()
        {
            try { return new T(); }
            catch { return (T)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(T)); }
        }
    }
}
