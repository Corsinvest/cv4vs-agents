/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Corsinvest.VisualStudio.Agents")]
[assembly: AssemblyDescription("Claude Code AI assistant for Visual Studio")]
[assembly: AssemblyCompany("Corsinvest Srl")]
[assembly: AssemblyProduct("cv4vs Agents")]
[assembly: AssemblyCopyright("Copyright © Corsinvest Srl 2026")]
[assembly: ComVisible(false)]
// Keep these in step with <Identity Version> in source.extension.vsixmanifest: the Marketplace
// rejects an upload whose version was already published, and About/init read the informational one.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
