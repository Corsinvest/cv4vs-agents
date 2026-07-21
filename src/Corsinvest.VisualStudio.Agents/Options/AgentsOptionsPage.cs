/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>
/// Shared base for all extension Options pages. Centralises the OnApply
/// hook that raises <see cref="AgentsOptions.RaiseApplied"/> on Apply,
/// so the four pages don't each repeat the identical override.
/// </summary>
[ComVisible(true)]
public abstract class AgentsOptionsPage : DialogPage
{
    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        if (e.ApplyBehavior == ApplyKind.Apply)
        {
            AgentsOptions.RaiseApplied();
        }
    }
}
