/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The JSON Schema the CLI reads to learn how to call an MCP tool.
/// <para>Wrong here means the model sends an argument under a name the tool never reads, or omits
/// one it needs — and neither shows up as an error on our side: the tool simply behaves as though
/// the caller had passed nothing.</para></summary>
public class SchemaBuilderTests
{
    private sealed class SampleArgs
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public bool? IncludeDetails { get; set; }
        public string[] Tags { get; set; }

        // The CLI uses snake_case on a few tools, so an explicit wire name has to win over the
        // camelCase policy.
        [JsonProperty("old_file_path")]
        public string OldFilePath { get; set; }

        // Read-only: nothing can be passed for it, so it has no business in the schema.
        public string Computed => "x";
    }

    private static Dictionary<string, object> Schema()
        => (Dictionary<string, object>)SchemaBuilder.For<SampleArgs>();

    private static Dictionary<string, object> Properties()
        => (Dictionary<string, object>)Schema()["properties"];

    [Fact]
    public void For_describes_an_object_with_its_writable_properties()
    {
        var schema = Schema();

        Assert.Equal("object", schema["type"]);
        Assert.Equal(
            new[] { "filePath", "line", "includeDetails", "tags", "old_file_path" },
            Properties().Keys);
    }

    [Fact]
    public void For_leaves_out_a_property_nothing_can_be_passed_for()
        => Assert.DoesNotContain("computed", Properties().Keys);

    [Fact]
    public void For_lets_an_explicit_JsonProperty_name_win_over_camelCase()
    {
        // Would be "oldFilePath" by policy; the attribute names the wire form the CLI sends.
        Assert.Contains("old_file_path", Properties().Keys);
        Assert.DoesNotContain("oldFilePath", Properties().Keys);
    }

    [Theory]
    [InlineData("filePath", "string")]
    [InlineData("line", "integer")]
    [InlineData("includeDetails", "boolean")]
    [InlineData("tags", "array")]
    public void For_maps_a_property_to_its_JSON_type(string name, string expected)
    {
        var meta = (Dictionary<string, object>)Properties()[name];

        Assert.Equal(expected, meta["type"]);
    }

    [Fact]
    public void For_says_what_an_array_holds()
    {
        var tags = (Dictionary<string, object>)Properties()["tags"];
        var items = (Dictionary<string, object>)tags["items"];

        Assert.Equal("string", items["type"]);
    }

    [Fact]
    public void For_returns_the_same_schema_object_on_a_second_call()
    {
        // Cached per type: the schema is rebuilt for every tool listing otherwise.
        Assert.Same(SchemaBuilder.For<SampleArgs>(), SchemaBuilder.For<SampleArgs>());
    }

    [Theory]
    [InlineData("FilePath", "filePath")]
    [InlineData("Line", "line")]
    [InlineData("URL", "url")]
    [InlineData("HTTPSPort", "httpsPort")]
    [InlineData("ID", "id")]
    // Already camel, or nothing to case.
    [InlineData("filePath", "filePath")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void ToCamelCase_lowers_the_leading_run_of_capitals(string input, string expected)
        => Assert.Equal(expected, SchemaBuilder.ToCamelCase(input));

    [Theory]
    // The schema and the wire have to agree, and the wire is Newtonsoft's resolver. Drift between
    // the two is invisible: the model sends what the schema promised, under a name nothing binds.
    [InlineData("FilePath")]
    [InlineData("Line")]
    [InlineData("URL")]
    [InlineData("HTTPSPort")]
    [InlineData("ID")]
    [InlineData("OldFilePath")]
    [InlineData("IncludeDetails")]
    [InlineData("A")]
    public void ToCamelCase_agrees_with_the_resolver_that_names_the_wire(string name)
    {
        var resolver = new CamelCasePropertyNamesContractResolver();

        Assert.Equal(resolver.GetResolvedPropertyName(name), SchemaBuilder.ToCamelCase(name));
    }
}
