/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.Drawing.Design;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>What the primary half of the launcher/toolbar "New" split button
/// spawns. The dropdown half always lets the user pick the other kind.</summary>
public enum NewSessionKind
{
    [Description("Chat")]
    Chat,
    [Description("CLI")]
    Cli,
}

/// <summary>File picker limited to executables — the CLI path must be a real .exe (see the
/// ClaudeExecutablePath comment). The filter only guides the dialog; a hand-typed path is still
/// validated by the resolver.</summary>
internal sealed class ExeFileNameEditor : System.Windows.Forms.Design.FileNameEditor
{
    protected override void InitializeDialog(System.Windows.Forms.OpenFileDialog openFileDialog)
    {
        base.InitializeDialog(openFileDialog);
        openFileDialog.Filter = "Executable (*.exe)|*.exe";
        openFileDialog.Title = "Select claude.exe";
    }
}

[ComVisible(true)]
public class AgentsGeneralPage : AgentsOptionsPage
{

    [DisplayName("Restore panes on solution open")]
    [Description("Reopen the panes (with their sessions) that were open for a solution when it is reopened.")]
    public bool RestorePanesOnSolutionOpen { get; set; } = false;

    [DisplayName("Default new session")]
    [Description("Which kind of session the \"New\" button creates by default (the dropdown still lets you pick the other).")]
    public NewSessionKind DefaultNewSession { get; set; } = NewSessionKind.Chat;

    // Must be the real claude.exe: both panes launch it as a PE binary (ConPTY CreateProcess, and
    // ProcessStartInfo with UseShellExecute=false + redirected stdio), so a .cmd/.bat/.ps1 shim
    // can't be launched — hence the .exe-only picker.
    [DisplayName("Claude executable path")]
    [Description("Full path to claude.exe, to override auto-detection (PATH, native installer, npm). Leave empty to auto-detect. Must be the real claude.exe — .cmd/.bat/.ps1 shims cannot be launched.")]
    [Editor(typeof(ExeFileNameEditor), typeof(UITypeEditor))]
    public string ClaudeExecutablePath { get; set; } = "";
}
