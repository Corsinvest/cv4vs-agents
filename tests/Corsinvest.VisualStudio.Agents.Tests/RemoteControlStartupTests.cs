/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Only an explicit, allowed choice starts a remote connection — never the rollout default.</summary>
public class RemoteControlStartupTests
{
    private static JObject Init(bool? available, bool? autoEnable, bool? byDefault)
    {
        var o = new JObject();
        if (available != null) { o[RemoteControlStartup.AvailableField] = available; }
        if (autoEnable != null) { o[RemoteControlStartup.AutoEnableField] = autoEnable; }
        if (byDefault != null) { o[RemoteControlStartup.AutoOnByDefaultField] = byDefault; }
        return o;
    }

    [Fact] public void Explicit_true_starts() => Assert.True(RemoteControlStartup.ShouldStart(Init(true, true, false)));
    [Fact] public void Rollout_default_does_not() => Assert.False(RemoteControlStartup.ShouldStart(Init(true, true, true)));
    [Fact] public void Off_does_not() => Assert.False(RemoteControlStartup.ShouldStart(Init(true, false, false)));
    [Fact] public void Unavailable_does_not() => Assert.False(RemoteControlStartup.ShouldStart(Init(false, true, false)));
    [Fact] public void Availability_absent_still_starts() => Assert.True(RemoteControlStartup.ShouldStart(Init(null, true, false)));
    [Fact] public void Fields_absent_do_not() => Assert.False(RemoteControlStartup.ShouldStart(new JObject()));
    [Fact] public void Null_reply_does_not() => Assert.False(RemoteControlStartup.ShouldStart(null));

    private static JObject Settings(JToken userValue, string lockReason = null)
    {
        var user = new JObject();
        if (userValue != null) { user[RemoteControlStartup.SettingKey] = userValue; }
        var o = new JObject
        {
            ["sources"] = new JArray
            {
                new JObject { ["source"] = "projectSettings", ["settings"] = new JObject { [RemoteControlStartup.SettingKey] = false } },
                new JObject { ["source"] = "userSettings", ["settings"] = user },
            },
        };
        if (lockReason != null) { o[RemoteControlStartup.LockReasonField] = lockReason; }
        return o;
    }

    [Fact] public void User_value_comes_from_userSettings_only() => Assert.True(RemoteControlStartup.UserValue(Settings(true)));
    [Fact] public void User_value_unset_is_null() => Assert.Null(RemoteControlStartup.UserValue(Settings(null)));
    [Fact] public void Lock_reason_read_when_present() => Assert.Equal("Org says no", RemoteControlStartup.LockReason(Settings(true, "Org says no")));
    [Fact] public void Lock_reason_absent_is_null() => Assert.Null(RemoteControlStartup.LockReason(Settings(true)));
}
