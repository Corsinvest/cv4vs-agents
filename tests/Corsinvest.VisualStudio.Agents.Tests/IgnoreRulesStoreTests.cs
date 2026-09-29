/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Host;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class IgnoreRulesStoreTests
{
    [Fact]
    public void Vscode_default_hides_contents_not_the_folder()
    {
        // `.vscode/` would prune the folder before a project's `!.vscode/settings.json` is read.
        var defaults = IgnoreRulesStore.Defaults.Replace("\r\n", "\n");
        Assert.Contains("\n.vscode/*\n", defaults);
        Assert.DoesNotContain("\n.vscode/\n", defaults);
    }
}
