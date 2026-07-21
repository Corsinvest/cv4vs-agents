/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;

namespace Corsinvest.VisualStudio.Agents;

internal static class PackageGuids
{
    public const string AgentsPackageString = "b1c2d3e4-f5a6-7890-bcde-fa1234567890";
    public static readonly Guid AgentsPackage = new(AgentsPackageString);

    public const string AgentsCommandSetString = "a2b3c4d5-e6f7-8901-cdef-ab1234567890";
    public static readonly Guid AgentsCommandSet = new(AgentsCommandSetString);
}

internal static class PackageIds
{
    // 0x0100 seeds the dynamic profile range, which grows with the profile count — the global
    // entries start at 0x0200 so they can never collide with it.
    public const int ShowToolWindowCommandId = 0x0100;

    public const int SettingsCommandId = 0x0200;
    public const int DataFolderCommandId = 0x0201;
    public const int OutputLogCommandId = 0x0202;
    public const int DocumentationCommandId = 0x0203;
    public const int ReportBugCommandId = 0x0204;
    public const int RequestFeatureCommandId = 0x0205;
    public const int FeedbackCommandId = 0x0206;
    public const int AboutCommandId = 0x0207;
}
