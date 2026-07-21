/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Profiles;

/// <summary>An environment profile: a named set of env vars injected into the
/// claude.exe of a pane, so a pane can drive a third-party provider (z.ai/GLM,
/// other Anthropic-compatible hosts) instead of native Claude.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;

    // Arbitrary env vars — no special fields. The user sets ANTHROPIC_BASE_URL,
    // ANTHROPIC_AUTH_TOKEN, model overrides, etc. The token is just a value here.
    public Dictionary<string, string> Env { get; set; } = [];

    public Profile Clone() => new()
    {
        Name = Name,
        Description = Description,
        Enabled = Enabled,
        Env = new Dictionary<string, string>(Env),
    };
}
