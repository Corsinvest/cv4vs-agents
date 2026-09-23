/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Linq;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>Reads remoteControlAtStartup the way the CLI already resolved it. initialize computes
/// <c>auto_enable = user-or-policy value ?? rollout default</c> (a project/local false winning) and
/// <c>auto_on_by_default = auto_enable &amp;&amp; value was unset</c> — so an explicit, allowed true is
/// exactly auto_enable and not by-default. The rollout default is left out on purpose: it is a
/// server-side flag that changed twice in three weeks.</summary>
internal static class RemoteControlStartup
{
    public const string SettingKey = "remoteControlAtStartup";

    public const string AvailableField = "remote_control_available";
    public const string AutoEnableField = "remote_control_auto_enable";
    public const string AutoOnByDefaultField = "remote_control_auto_on_by_default";
    public const string LockReasonField = "remote_control_policy_lock_reason";

    public static bool ShouldStart(JObject initializeReply) =>
        initializeReply != null
        && Available(initializeReply) != false
        && initializeReply.ValBool(AutoEnableField) == true
        && initializeReply.ValBool(AutoOnByDefaultField) == false;

    public static bool? Available(JObject initializeReply) => initializeReply.ValBool(AvailableField);

    /// <summary>The user's own choice for this profile (the userSettings layer), not the resolved
    /// value: the switch shows what it wrote, even where a project turns it off.</summary>
    public static bool? UserValue(JObject getSettingsReply) =>
        (getSettingsReply?["sources"] as JArray)?
            .OfType<JObject>()
            .FirstOrDefault(s => s.Val("source") == "userSettings")?["settings"]
            .ValBool(SettingKey);

    /// <summary>Absent when no org policy locks Remote Control.</summary>
    public static string LockReason(JObject getSettingsReply) =>
        getSettingsReply.Val(LockReasonField);
}
