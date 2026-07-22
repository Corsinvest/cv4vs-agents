/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Reflection;

namespace Corsinvest.VisualStudio.Agents;

/// <summary>
/// What this build is, read once from the assembly. The version comes from
/// AssemblyInformationalVersion, which Directory.Build.targets generates from
/// <c>Version</c> — the only attribute that carries a preview suffix, since
/// AssemblyVersion and the VSIX Identity Version take digits only.
/// </summary>
internal static class BuildInfo
{
    /// <summary>Full version, suffix included: <c>1.0.0</c> or <c>1.0.0-rc1</c>.</summary>
    public static string Version { get; }

    /// <summary>The suffix on its own (<c>rc1</c>), empty on a stable build.</summary>
    public static string PreRelease { get; }

    /// <summary>True when this build carries a preview suffix.</summary>
    public static bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    static BuildInfo()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Roslyn can append source-revision metadata (1.0.0-rc1+sha); it is not part of the
        // version and would leave the suffix unrecognisable.
        Version = (informational ?? "0.0.0").Split('+')[0];

        // Split on the first hyphen only, so a dotted suffix (rc.1) survives intact.
        var parts = Version.Split(['-'], 2);
        PreRelease = parts.Length > 1 ? parts[1] : string.Empty;
    }
}
