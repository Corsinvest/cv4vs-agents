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

    /// <summary>Free-form reminder for whoever set the profile up — which account this is, when the
    /// token expires, why it exists. Nothing reads it: profiles are identified by Name everywhere
    /// (the pane caption, the View menu), and a VS dynamic menu item carries no tooltip to put it
    /// in. It earns its place in the Options page alone, which is where such a note gets written
    /// and re-read.</summary>
    public string Notes { get; set; } = "";

    public bool Enabled { get; set; } = true;

    // Arbitrary env vars — no special fields. The user sets ANTHROPIC_BASE_URL,
    // ANTHROPIC_AUTH_TOKEN, model overrides, etc. The token is just a value here.
    public Dictionary<string, string> Env { get; set; } = [];

    public Profile Clone() => new()
    {
        Name = Name,
        Notes = Notes,
        Enabled = Enabled,
        Env = new Dictionary<string, string>(Env),
    };
}
