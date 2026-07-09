using System.Diagnostics.CodeAnalysis;
using Ignixa.Abstractions;

namespace FhirCandle.Compartments;

/// <summary>
/// A parsed CompartmentDefinition resource: which resource types are members of the compartment, and
/// which of their search parameters establish membership.
/// </summary>
public sealed class ParsedCompartment
{
    /// <summary>An included resource type within a compartment, and the search parameter codes that
    /// establish membership.</summary>
    public sealed record class IncludedResource
    {
        /// <summary>Gets the type of the resource.</summary>
        public required string ResourceType { get; init; }

        /// <summary>Gets the search parameter codes associated with the resource.</summary>
        public required string[] SearchParamCodes { get; init; }
    }

    /// <summary>Gets the URL of the compartment definition.</summary>
    public required string Url { get; init; }

    /// <summary>Gets the name of the compartment definition.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the version of the compartment definition.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the type of the compartment (e.g. <c>Patient</c>).</summary>
    public required string CompartmentType { get; init; }

    /// <summary>Gets the included resources within the compartment, keyed by resource type.</summary>
    public required Dictionary<string, IncludedResource> IncludedResources { get; init; }

    /// <summary>Initializes a new instance of the <see cref="ParsedCompartment"/> class from a
    /// CompartmentDefinition resource, used for runtime-registered (POSTed) compartments.</summary>
    /// <param name="cd">The CompartmentDefinition, as an <see cref="IElement"/>.</param>
    /// <exception cref="InvalidOperationException">The resource has no <c>code</c> element.</exception>
    [SetsRequiredMembers]
    public ParsedCompartment(IElement cd)
    {
        Url = cd.FirstChild("url")?.Value as string ?? Guid.NewGuid().ToString();
        Name = cd.FirstChild("name")?.Value as string ?? cd.FirstChild("id")?.Value as string ?? string.Empty;
        Version = cd.FirstChild("version")?.Value as string ?? string.Empty;
        CompartmentType = cd.FirstChild("code")?.Value as string
            ?? throw new InvalidOperationException("Cannot parse compartment definition without a code element!");

        IncludedResources = cd.Children("resource")
            .Select(r => (Code: r.FirstChild("code")?.Value as string, Params: r.Children("param")))
            .Where(r => r.Code is not null && r.Params.Count > 0)
            .Select(r => new IncludedResource
            {
                ResourceType = r.Code!,
                SearchParamCodes = r.Params
                    .Select(p => p.Value as string == "{def}" ? "_id" : p.Value as string ?? string.Empty)
                    .ToArray(),
            })
            .ToDictionary(ir => ir.ResourceType, ir => ir);
    }

    /// <summary>Initializes a new instance of the <see cref="ParsedCompartment"/> class directly from
    /// its constituent parts, used to adapt <c>Ignixa.Search.Definition.CompartmentDefinitionManager</c>'s
    /// pre-built lookups (see <see cref="CoreCompartmentSource"/>) without round-tripping through a
    /// synthesized CompartmentDefinition element.</summary>
    [SetsRequiredMembers]
    internal ParsedCompartment(
        string url,
        string name,
        string version,
        string compartmentType,
        Dictionary<string, IncludedResource> includedResources)
    {
        Url = url;
        Name = name;
        Version = version;
        CompartmentType = compartmentType;
        IncludedResources = includedResources;
    }
}
