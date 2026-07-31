/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Core.Client;

public static class PermissionMode
{
    public const string Default = "default";
    public const string AcceptEdits = "acceptEdits";
    public const string Plan = "plan";
    public const string Auto = "auto";
    public const string BypassPermissions = "bypassPermissions";

    /// <summary>Map the Options enum to the CLI's wire value. <paramref name="allowBypass"/> is
    /// the VS option: with it off, a saved BypassPermissions falls back to Default rather than
    /// starting the session in a mode the selector hides — it could not then be left.</summary>
    public static string FromInitial(Options.InitialPermissionMode mode, bool allowBypass) => mode switch
    {
        Options.InitialPermissionMode.AcceptEdits => AcceptEdits,
        Options.InitialPermissionMode.Plan => Plan,
        Options.InitialPermissionMode.BypassPermissions => allowBypass ? BypassPermissions : Default,
        _ => Default,
    };
}
