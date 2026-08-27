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
    public const int ReleasesCommandId = 0x0208;
    public const int StatisticsCommandId = 0x0209;
    public const int UsageCommandId = 0x020A;
    public const int ContextUsageCommandId = 0x020B;
    public const int ExplainCommandId = 0x020C;

    // Seeds the dynamic open-panes range, which grows with the number of open panes — its own
    // 0x0300 block, clear of the fixed ids above.
    public const int ActiveSessionCommandId = 0x0300;

    // Seeds the editor context menu's prompt range, which grows with the configured prompts —
    // 0x0400, clear of the panes range that grows under it.
    public const int EditorPromptCommandId = 0x0400;
}
