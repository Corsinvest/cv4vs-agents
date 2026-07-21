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

    public static AgentsGeneralPage General =>
        Get<AgentsGeneralPage>() ?? new AgentsGeneralPage();

    public static AgentsChatPage Chat =>
        Get<AgentsChatPage>() ?? new AgentsChatPage();

    public static AgentsDebugPage Debug =>
        Get<AgentsDebugPage>() ?? new AgentsDebugPage();

    private static T Get<T>() where T : Microsoft.VisualStudio.Shell.DialogPage =>
        AgentsPackage.Instance?.GetDialogPage(typeof(T)) as T;
}
