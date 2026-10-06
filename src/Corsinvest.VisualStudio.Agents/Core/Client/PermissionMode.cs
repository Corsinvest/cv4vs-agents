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
    // Never offered by the selector: only ever met on a session resumed from the terminal.
    public const string DontAsk = "dontAsk";
    public const string BypassPermissions = "bypassPermissions";

    /// <summary>Map the Options enum to the CLI's wire value, or null when the CLI is left to
    /// pick the mode itself. <paramref name="allowBypass"/> is the VS option: with it off, a saved
    /// BypassPermissions falls back to Manual rather than starting the session in a mode the
    /// selector hides: it could not then be left.</summary>
    public static string FromInitial(Options.InitialPermissionMode mode, bool allowBypass) => mode switch
    {
        Options.InitialPermissionMode.Manual => Default,
        Options.InitialPermissionMode.AcceptEdits => AcceptEdits,
        Options.InitialPermissionMode.Plan => Plan,
        Options.InitialPermissionMode.BypassPermissions => allowBypass ? BypassPermissions : Default,
        _ => null,
    };

    /// <summary>The launch argument for a starting mode, empty for null: without the flag the CLI
    /// starts in the user's `permissions.defaultMode` (settings.json), or in a mode of its own
    /// choosing when there is none.
    /// A closed list, not the value as it comes: it is read back from a .jsonl, and the CLI exits
    /// on a mode it doesn't know. Anything else starts in the cautious one.
    /// <para>`default`, not `manual`, for the cautious one: the CLI took `manual` as its name in
    /// 2.1.200 (2026-07-03) and exits on it before that, while `default` is accepted from 1.0.100
    /// through 2.1.291, though `--help` stopped listing it. Measured on both sides of 2.1.200.</para></summary>
    public static string LaunchArg(string mode) => mode == null
        ? ""
        : " --permission-mode " + mode switch
        {
            AcceptEdits or Plan or Auto or DontAsk or BypassPermissions => mode,
            _ => Default,
        };
}
