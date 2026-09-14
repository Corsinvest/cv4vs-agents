/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Xml.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Monikers.imagemanifest against the code and the assembly that depend on it.
/// <para>Every way this goes wrong is silent: a Guid or ID that isn't the one PackageMonikers names, or a
/// source the assembly doesn't carry, and Visual Studio draws an empty tab icon without a word.</para></summary>
public class ImageManifestTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/VisualStudio/ImageManifestSchema/2014";
    private const string AssemblyName = "Corsinvest.VisualStudio.Agents";

    [Theory]
    [InlineData(PackageMonikers.LogoId)]
    [InlineData(PackageMonikers.ChatGlyphId)]
    public void Each_image_PackageMonikers_names_is_declared(int id)
    {
        var doc = LoadManifest();
        var images = doc.Root.Element(Ns + "Images").Elements(Ns + "Image")
            .Select(i => (Guid.Parse(Resolve(doc, (string)i.Attribute("Guid"))),
                          int.Parse(Resolve(doc, (string)i.Attribute("ID")))));

        Assert.Contains((Guid.Parse(PackageMonikers.ImagesGuidString), id), images);
    }

    [Fact]
    public void Every_source_is_a_resource_the_assembly_carries()
    {
        var doc = LoadManifest();
        var prefix = $"/{AssemblyName};component/";
        var keys = ResourceKeys();

        foreach (var source in doc.Descendants(Ns + "Source"))
        {
            var uri = Resolve(doc, (string)source.Attribute("Uri"));
            Assert.StartsWith(prefix, uri, StringComparison.OrdinalIgnoreCase);
            // WPF files resources under lower-case keys.
            Assert.Contains(uri.Substring(prefix.Length).ToLowerInvariant(), keys);
        }
    }

    // Linked into the output by the test project, from the file the VSIX ships.
    private static XDocument LoadManifest()
        => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Monikers.imagemanifest"));

    // $(Name) references, expanded from the manifest's own Symbols.
    private static string Resolve(XDocument doc, string value)
    {
        foreach (var symbol in doc.Root.Element(Ns + "Symbols").Elements())
        {
            value = value.Replace($"$({(string)symbol.Attribute("Name")})", (string)symbol.Attribute("Value"));
        }
        return value;
    }

    private static HashSet<string> ResourceKeys()
    {
        using var stream = typeof(PackageMonikers).Assembly.GetManifestResourceStream(AssemblyName + ".g.resources");
        using var reader = new ResourceReader(stream);
        return new HashSet<string>(reader.Cast<DictionaryEntry>().Select(e => (string)e.Key));
    }
}
