using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Ignixa.Abstractions;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Storage;

/// <summary>
/// A service for tracking ValueSet membership (for the <c>:in</c>/<c>:not-in</c> search modifiers) and
/// performing <c>$validate-code</c>-style checks against stored ValueSets.
/// </summary>
/// <remarks>
/// Ignixa-model port of the old (Firely-based) <c>StoreTerminologyService</c>. That type implemented
/// Firely's <c>ITerminologyService</c> (registered with the FHIRPath <c>%terminologies</c> resolver);
/// nothing in the new engine consumes that interface, so this port keeps only the three members that
/// are actually used: <see cref="VsContains"/>, <see cref="StoreProcessValueSet"/>, and
/// <see cref="ValueSetValidateCode"/>. The six Firely stub members that always threw
/// <c>NotImplementedException</c> (Subsumes, CodeSystemValidateCode, Lookup, Translate, Closure, Expand)
/// are dropped entirely.
/// </remarks>
public sealed class StoreTerminologyService
{
    private sealed record ValueSetContents
    {
        public HashSet<string> Codes { get; init; } = [];

        public HashSet<string> SystemAndCodes { get; init; } = [];
    }

    private readonly IFhirSchemaProvider _schema;

    private readonly ConcurrentDictionary<string, ValueSetContents> _valueSetContents = new();

    /// <summary>Initializes a new instance of the <see cref="StoreTerminologyService"/> class.</summary>
    /// <param name="schema">The FHIR schema provider, used to convert Parameters/ValueSet resources
    /// passed to <see cref="ValueSetValidateCode"/> into <see cref="IElement"/> for traversal.</param>
    public StoreTerminologyService(IFhirSchemaProvider schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
    }

    /// <summary>Tests whether a code is a member of a previously-processed ValueSet.</summary>
    /// <param name="vsUrl"> The canonical URL of the ValueSet.</param>
    /// <param name="system">The code system (optional - if omitted, matches on code alone).</param>
    /// <param name="code">  The code.</param>
    /// <returns>True if the ValueSet is known and contains the code; otherwise false.</returns>
    public bool VsContains(string? vsUrl, string? system, string? code)
    {
        if (vsUrl is null || code is null || !_valueSetContents.TryGetValue(vsUrl, out ValueSetContents? contents))
        {
            return false;
        }

        return string.IsNullOrEmpty(system)
            ? contents.Codes.Contains(code)
            : contents.SystemAndCodes.Contains($"{system}|{code}");
    }

    /// <summary>Flattens a ValueSet's <c>expansion.contains</c> (including nested <c>contains</c>) or
    /// <c>compose.include[].concept[]</c> into the membership sets used by <see cref="VsContains"/>, or
    /// removes a previously-processed ValueSet.</summary>
    /// <param name="valueSet">The ValueSet resource, as an <see cref="IElement"/>.</param>
    /// <param name="remove">  True to remove the ValueSet's contents instead of (re-)processing them.</param>
    public void StoreProcessValueSet(IElement valueSet, bool remove = false)
    {
        string? url = valueSet.FirstChild("url")?.Value as string;

        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (remove)
        {
            _valueSetContents.TryRemove(url, out _);
            return;
        }

        HashSet<string> codes = [];
        HashSet<string> systemAndCodes = [];

        IReadOnlyList<IElement> expansionContains = valueSet.FirstChild("expansion")?.Children("contains") ?? [];

        if (expansionContains.Count > 0)
        {
            AddContains(expansionContains, codes, systemAndCodes);
        }
        else
        {
            foreach (IElement include in valueSet.FirstChild("compose")?.Children("include") ?? [])
            {
                string system = include.FirstChild("system")?.Value as string ?? string.Empty;

                foreach (IElement concept in include.Children("concept"))
                {
                    if (concept.FirstChild("code")?.Value is not string code)
                    {
                        continue;
                    }

                    codes.Add(code);
                    systemAndCodes.Add($"{system}|{code}");
                }
            }
        }

        _valueSetContents[url] = new ValueSetContents { Codes = codes, SystemAndCodes = systemAndCodes };

        static void AddContains(IReadOnlyList<IElement> contains, HashSet<string> codes, HashSet<string> systemAndCodes)
        {
            foreach (IElement c in contains)
            {
                if (c.FirstChild("code")?.Value is not string code)
                {
                    continue;
                }

                string system = c.FirstChild("system")?.Value as string ?? string.Empty;

                codes.Add(code);
                systemAndCodes.Add($"{system}|{code}");

                IReadOnlyList<IElement> nested = c.Children("contains");
                if (nested.Count > 0)
                {
                    AddContains(nested, codes, systemAndCodes);
                }
            }
        }
    }

    /// <summary>Ports the old <c>ValueSet/$validate-code</c> operation: reads <c>url</c>/<c>code</c>/
    /// <c>system</c>/<c>coding</c>/<c>codeableConcept</c>/<c>valueSet</c> from an input Parameters
    /// resource and returns an output Parameters resource with a boolean <c>result</c> and, on failure,
    /// a <c>message</c>.</summary>
    /// <param name="parameters">The input Parameters resource.</param>
    /// <returns>The output Parameters resource.</returns>
    public ResourceJsonNode ValueSetValidateCode(ResourceJsonNode parameters)
    {
        IElement root = parameters.ToElement(_schema);

        string vsUrl = FindParamPrimitiveValue(root, "url") ?? string.Empty;

        if (string.IsNullOrEmpty(vsUrl))
        {
            IElement? embeddedValueSet = FindParam(root, "valueSet")?.FirstChild("resource");
            string? embeddedUrl = embeddedValueSet?.FirstChild("url")?.Value as string;

            if (embeddedValueSet is null || string.IsNullOrEmpty(embeddedUrl))
            {
                return BuildResult(false, "No value set specified");
            }

            vsUrl = embeddedUrl;
            StoreProcessValueSet(embeddedValueSet);
        }

        string system = FindParamPrimitiveValue(root, "system") ?? string.Empty;
        string code = FindParamPrimitiveValue(root, "code") ?? string.Empty;

        if (string.IsNullOrEmpty(code))
        {
            IElement? coding = FindParamValueElement(root, "coding");
            IElement? concept = FindParamValueElement(root, "codeableConcept");

            if (coding is not null)
            {
                system = coding.FirstChild("system")?.Value as string ?? string.Empty;
                code = coding.FirstChild("code")?.Value as string ?? string.Empty;
            }
            else if (concept is not null)
            {
                IElement? firstCoding = concept.Children("coding").FirstOrDefault();
                system = firstCoding?.FirstChild("system")?.Value as string ?? string.Empty;
                code = firstCoding?.FirstChild("code")?.Value as string ?? string.Empty;
            }
        }

        if (string.IsNullOrEmpty(system) && string.IsNullOrEmpty(code))
        {
            return BuildResult(false, "Could not determine system and code for testing!");
        }

        return VsContains(vsUrl, system, code)
            ? BuildResult(true, null)
            : BuildResult(false, "Code not found in value set");
    }

    private static IElement? FindParam(IElement parametersRoot, string name) =>
        parametersRoot.Children("parameter")
            .FirstOrDefault(p => string.Equals(p.FirstChild("name")?.Value as string, name, StringComparison.Ordinal));

    private static string? FindParamPrimitiveValue(IElement parametersRoot, string name) =>
        FindParam(parametersRoot, name)?.FirstChild("value")?.Value as string;

    private static IElement? FindParamValueElement(IElement parametersRoot, string name) =>
        FindParam(parametersRoot, name)?.FirstChild("value");

    private static ResourceJsonNode BuildResult(bool result, string? message)
    {
        var output = new ParametersJsonNode();

        var resultParam = new ParameterJsonNode { Name = "result" };
        resultParam.SetValue("valueBoolean", JsonValue.Create(result));
        output.Parameter.Add(resultParam);

        if (!string.IsNullOrEmpty(message))
        {
            var messageParam = new ParameterJsonNode { Name = "message" };
            messageParam.SetValue("valueString", JsonValue.Create(message));
            output.Parameter.Add(messageParam);
        }

        return output;
    }
}
