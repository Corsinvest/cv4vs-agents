/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>Every listed file's changes in the one window Visual Studio 18 has for it: a file per section, how it was before on
/// the left and the live document on the right. It is a way to look at all of it together; Keep and Undo stay in the panel.
/// <para>The window is reached through <see cref="MultiDiffShape"/>, by reflection, because it is not part of the SDK this
/// extension is built against. Where the service is not there (Visual Studio 2022) or is not the shape that was decompiled, the
/// button that opens it is never shown, and nothing else changes.</para></summary>
internal static class MultiDiffReviewer
{
    private static bool _probed;
    private static MultiDiffShape _shape;

    /// <summary>Whether this Visual Studio has the window. Settled by the first look, which has to be on the UI thread: asked
    /// from another before that, it says no, and is asked again.</summary>
    public static bool IsAvailable => Resolve() is not null;

    private static MultiDiffShape Resolve()
    {
        if (_probed) { return _shape; }
        if (!ThreadHelper.CheckAccess()) { return null; }

        _probed = true;
        try
        {
            var components = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            var definition = new ContractBasedImportDefinition(
                MultiDiffShape.ServiceContract, null, null, ImportCardinality.ZeroOrMore, false, false, CreationPolicy.Any);
            var service = components?.DefaultExportProvider.GetExports(definition).FirstOrDefault()?.Value;
            _shape = MultiDiffShape.TryResolve(service, out var why);
            OutputWindowLogger.Global.Info(_shape is not null
                ? "[changes] the all-changes window is available"
                : $"[changes] the all-changes window is not used: {why}");
        }
        catch (Exception ex)
        {
            _shape = null;
            OutputWindowLogger.Global.LogException("MultiDiffReviewer.Resolve", ex);
        }
        return _shape;
    }

    /// <summary>Open the window with every listed file that can be shown in text. False when it could not be opened, or when no
    /// listed file can be shown there.</summary>
    public static async Task<bool> ShowAsync(ChangeSet set)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var shape = Resolve();
        if (shape is null) { return false; }

        try
        {
            var items = await CollectAsync(set.Snapshot());
            if (items.Count == 0)
            {
                OutputWindowLogger.Global.Info("[changes] no listed file can be shown in the all-changes window");
                return false;
            }

            var components = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            var buffers = components.GetService<ITextBufferFactoryService>();
            var extensions = components.GetService<IFileExtensionRegistryService>();
            var contentTypes = components.GetService<IContentTypeRegistryService>();

            var session = shape.CreateSession($"Changes by Claude ({items.Count})");
            foreach (var item in items)
            {
                var extension = Path.GetExtension(item.Path).TrimStart('.');
                var contentType = extensions.GetContentTypeForExtension(extension) ?? contentTypes.UnknownContentType;
                var buffer = buffers.CreateTextBuffer(item.Text, contentType);
                shape.AddDiff(session, shape.NewEntry(item.Path, buffer, item.Path));
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OutputWindowLogger.Global.LogException("MultiDiffReviewer.ShowAsync", ex);
            return false;
        }
    }

    private readonly struct Item
    {
        public Item(string path, string text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }
        public string Text { get; }
    }

    /// <summary>The files the window can take, each with its baseline as text in the file's own line endings: the two sides
    /// then differ in lines and not in how they end. Reads the disk, asynchronously.</summary>
    private static async Task<List<Item>> CollectAsync(IReadOnlyList<FileChange> rows)
    {
        var log = OutputWindowLogger.Global;
        var items = new List<Item>();
        foreach (var row in rows)
        {
            // The same rule as the review in the editor: where text and bytes agree. Deleted files have no side to show.
            var (why, baseline) = await BaselineReview.PrepareAsync(row).ConfigureAwait(false);
            if (why is not null)
            {
                log.Info($"[changes] {row.Path} is left out of the all-changes window: {why}");
                continue;
            }
            string current;
            try { current = await AsyncFile.ReadAllTextAsync(row.Path).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"[changes] {row.Path} is left out of the all-changes window, it could not be read: {ex.Message}");
                continue;
            }
            items.Add(new Item(row.Path, BaselineText.WithLineEnding(baseline.Text, BaselineText.DominantLineEnding(current))));
        }
        return items;
    }
}
