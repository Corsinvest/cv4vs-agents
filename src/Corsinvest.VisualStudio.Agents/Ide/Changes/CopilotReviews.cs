/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Primitives;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>Whether GitHub Copilot's review UI can be used on this Visual Studio, found out once. The editor draws no
/// review of its own (the hunk buttons and the panel at the bottom are Copilot's), so without Copilot there is nothing to show
/// a file's changes in, and the review is not offered: one line in the log says so, and Open shows the comparison window.
/// <para>The parts are asked of the editor's MEF container by contract name, not imported: this extension exports nothing, and
/// an import of a type it cannot reference would not compose where Copilot is absent.</para></summary>
internal static class CopilotReviews
{
    private static bool _probed;
    private static CopilotReviewShape _shape;

    /// <summary>The parts of Copilot's review, or null when they are not there. UI thread; the first call looks, the others
    /// answer from what it found.</summary>
    public static CopilotReviewShape Shape(OutputWindowLogger log)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_probed) { return _shape; }
        _probed = true;
        try
        {
            var components = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            var marker = ExportOf(components, CopilotReviewShape.MarkerContract, log, out var markerWhy);
            var registry = ExportOf(components, CopilotReviewShape.RegistryContract, log, out var registryWhy);
            if (marker is null || registry is null)
            {
                log.Info($"[changes] GitHub Copilot's review is not available in this Visual Studio ({markerWhy ?? registryWhy}): "
                         + "files are not reviewed in the editor, and Open shows the comparison window");
                return null;
            }

            _shape = CopilotReviewShape.TryResolve(marker, registry, out var why);
            if (_shape is null)
            {
                log.Info($"[changes] GitHub Copilot's review does not have the shape this extension drives ({why}): "
                         + "files are not reviewed in the editor, and Open shows the comparison window");
            }
            else
            {
                log.Info($"[changes] GitHub Copilot's review is used to show the changes in the editor: {_shape.Description}");
            }
        }
        catch (Exception ex)
        {
            log.LogException("CopilotReviews.Shape", ex);
            _shape = null;
        }
        return _shape;
    }

    // The part the contract names, or null with the reason. One is enough: the parts are shared, and a second would be Copilot's
    // own idea of a second.
    private static object ExportOf(IComponentModel components, string contract, OutputWindowLogger log, out string why)
    {
        why = null;
        if (components is null)
        {
            why = "no component model";
            return null;
        }
        var definition = new ContractBasedImportDefinition(contract, null, null, ImportCardinality.ZeroOrMore, false, false, CreationPolicy.Any);
        List<Export> exports;
        try { exports = components.DefaultExportProvider.GetExports(definition).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Debug(() => $"[changes] asking for {contract} threw {ex.GetType().Name}: {ex.Message}");
            why = $"{Short(contract)} could not be asked for: {ex.GetType().Name}";
            return null;
        }
        if (exports.Count == 0)
        {
            why = $"nothing exports {Short(contract)}";
            return null;
        }
        try { return exports[0].Value; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Debug(() => $"[changes] creating {contract} threw {ex.GetType().Name}: {ex.Message}");
            why = $"{Short(contract)} could not be created: {ex.GetType().Name}";
            return null;
        }
    }

    private static string Short(string contract) => contract.Substring(contract.LastIndexOf('.') + 1);
}
