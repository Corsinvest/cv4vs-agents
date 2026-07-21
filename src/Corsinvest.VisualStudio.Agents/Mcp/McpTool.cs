/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp;

/// <summary>
/// Strongly-typed base class for MCP tools. The <typeparamref name="TArgs"/>
/// POCO is the single source of truth: schema (via <see cref="SchemaBuilder"/>),
/// deserialization, and <c>[Required]</c> validation all derive from it before
/// <see cref="InvokeAsync(TArgs)"/> receives a typed instance.
/// For tools with no arguments, derive from <see cref="McpTool{NoArgs}"/>.
/// </summary>
internal abstract class McpTool<TArgs> : IMcpTool where TArgs : class, new()
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual object InputSchema => SchemaBuilder.For<TArgs>();
    public virtual bool AlwaysLoad => false;

    private static readonly JsonSerializer _serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        // Match the schema's camelCase wire format. JsonProperty attributes
        // on the POCO still win (used for snake_case CLI quirks).
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    });

    public Task<object> InvokeAsync(JObject arguments)
    {
        TArgs args;
        try
        {
            args = arguments == null
                ? new TArgs()
                : (TArgs)arguments.ToObject(typeof(TArgs), _serializer);
        }
        catch (JsonException jex)
        {
            // Surface bad payloads as -32602 invalid-params.
            throw new McpToolException($"Invalid arguments: {jex.Message}");
        }

        ValidateRequired(args);
        return InvokeAsync(args);
    }

    /// <summary>Tool implementation. Receives validated, typed args.</summary>
    protected abstract Task<object> InvokeAsync(TArgs args);

    /// <summary>Throw <see cref="McpToolException"/> if any
    /// <c>[Required]</c> property is missing/null. Reflection per call,
    /// but the property list is short.</summary>
    private static void ValidateRequired(TArgs args)
    {
        foreach (var prop in typeof(TArgs).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<RequiredAttribute>() == null) { continue; }
            var value = prop.GetValue(args);
            if (value == null || (value is string s && string.IsNullOrEmpty(s)))
            {
                var name = prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName
                           ?? SchemaBuilder.ToCamelCase(prop.Name);
                throw new McpToolException($"Missing required argument: {name}");
            }
        }
    }
}

/// <summary>Marker for tools that take no arguments. Derive
/// <c>McpTool&lt;NoArgs&gt;</c> instead of writing an empty class for
/// each tool.</summary>
internal sealed class NoArgs { }
