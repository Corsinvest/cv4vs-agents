/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Options;

public enum LogLevel
{
    None = 0,
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}

[ComVisible(true)]
public class AgentsDebugPage : AgentsOptionsPage
{

    [DisplayName("Log level")]
    [Description("Verbosity of the Output window log. None = silent, Trace = include bridge traffic.")]
    public LogLevel LogLevel { get; set; } = LogLevel.None;

    [DisplayName("Enable performance logging")]
    [Description("Enable performance span logging in the Output window (C#) and browser console (JS). Requires restart of Visual Studio.")]
    public bool EnablePerfLog { get; set; } = false;
}
