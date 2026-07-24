/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

// Custom WPF controls (Core/Controls) resolve their default ControlTemplate from Themes/Generic.xaml
// in this assembly. Required for CvExpander's chevron template to load.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

[assembly: AssemblyTitle("Corsinvest.VisualStudio.Agents")]
[assembly: AssemblyDescription("Claude Code AI assistant for Visual Studio")]
[assembly: AssemblyCompany("Corsinvest Srl")]
[assembly: AssemblyProduct("cv4vs Agents")]
// First-published year, not the current one. BuildInfo.Copyright reads it back for the About
// dialog and the WebView welcome screen.
[assembly: AssemblyCopyright("© Corsinvest Srl 2026")]
[assembly: ComVisible(false)]

// No version attributes here. This is a legacy csproj, which does not generate an AssemblyInfo, so
// they used to be written by hand and kept in step with Directory.Build.props by a build check.
// GenerateVersionAssemblyInfo (Directory.Build.targets) emits them into obj\ instead, from Version
// — one place to edit, nothing left to drift.
