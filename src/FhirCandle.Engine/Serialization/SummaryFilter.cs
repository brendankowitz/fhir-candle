using System.Text.Json.Nodes;
using Ignixa.Abstractions;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Serialization;

public static class SummaryFilter
{
    private const string SubsettedSystem = "http://terminology.hl7.org/CodeSystem/v3-ObservationValue";
    private const string SubsettedCode = "SUBSETTED";

    private static readonly HashSet<string> AlwaysKept = new(StringComparer.Ordinal)
    {
        "resourceType", "id", "meta",
    };

    /// <summary>
    /// Returns a filtered deep clone of <paramref name="resource"/> per the FHIR `_summary`/`_elements`
    /// semantics for <paramref name="summaryFlag"/> ("true", "text", or "data"). The original resource
    /// is left unmodified.
    /// </summary>
    public static ResourceJsonNode Apply(ResourceJsonNode resource, IFhirSchemaProvider schema, string summaryFlag)
    {
        JsonObject clone = resource.MutableNode.DeepClone().AsObject();

        switch (summaryFlag)
        {
            case "true":
                RemoveNonSummaryTopLevelProperties(clone, schema);
                break;
            case "text":
                KeepOnlyTopLevelProperty(clone, "text");
                break;
            case "data":
                RemoveTopLevelProperty(clone, "text");
                break;
            default:
                throw new ArgumentException($"Unsupported _summary flag '{summaryFlag}'", nameof(summaryFlag));
        }

        ResourceJsonNode filtered = JsonSourceNodeFactory.Parse((JsonNode)clone);
        AppendSubsettedTag(filtered);
        return filtered;
    }

    /// <summary>
    /// Returns a filtered deep clone of <paramref name="resource"/> per the FHIR `_elements` semantics:
    /// keeps only top-level properties named in <paramref name="elements"/>, plus (when
    /// <paramref name="includeMandatory"/> is set) any top-level property whose element definition is
    /// mandatory. The original resource is left unmodified.
    /// </summary>
    public static ResourceJsonNode ApplyElements(
        ResourceJsonNode resource, IFhirSchemaProvider schema, IReadOnlyList<string> elements, bool includeMandatory = true)
    {
        JsonObject clone = resource.MutableNode.DeepClone().AsObject();
        RemoveNonElementsTopLevelProperties(clone, schema, elements, includeMandatory);

        ResourceJsonNode filtered = JsonSourceNodeFactory.Parse((JsonNode)clone);
        AppendSubsettedTag(filtered);
        return filtered;
    }

    // Removes only direct (top-level) properties of the resource whose element definition is not
    // in-summary. Kept complex properties retain their full subtree unchanged - the InSummary filter
    // is not re-applied recursively inside them.
    private static void RemoveNonSummaryTopLevelProperties(JsonObject clone, IFhirSchemaProvider schema)
    {
        string resourceType = clone["resourceType"]?.GetValue<string>() ?? string.Empty;
        IType type = schema.GetTypeDefinition(resourceType)
            ?? throw new NotSupportedException($"Unknown resource type '{resourceType}'");

        Dictionary<string, IType> childrenByName = type.Children.ToDictionary(c => c.Info.Name);

        foreach (string key in clone.Select(kv => kv.Key).ToList())
        {
            string baseName = key.StartsWith('_') ? key[1..] : key;
            if (AlwaysKept.Contains(baseName)) continue;
            if (childrenByName.TryGetValue(baseName, out IType? child) && !child.InSummary)
            {
                clone.Remove(key);
            }
        }
    }

    // Removes only direct (top-level) properties of the resource that are neither requested in the
    // `_elements` list nor mandatory. Kept complex properties retain their full subtree unchanged - the
    // filter is not re-applied recursively inside them.
    private static void RemoveNonElementsTopLevelProperties(
        JsonObject clone, IFhirSchemaProvider schema, IReadOnlyList<string> elements, bool includeMandatory)
    {
        string resourceType = clone["resourceType"]?.GetValue<string>() ?? string.Empty;
        IType type = schema.GetTypeDefinition(resourceType)
            ?? throw new NotSupportedException($"Unknown resource type '{resourceType}'");

        Dictionary<string, IType> childrenByName = type.Children.ToDictionary(c => c.Info.Name);
        HashSet<string> requestedElements = new(elements, StringComparer.Ordinal);

        foreach (string key in clone.Select(kv => kv.Key).ToList())
        {
            string baseName = key.StartsWith('_') ? key[1..] : key;
            if (AlwaysKept.Contains(baseName) || requestedElements.Contains(baseName)) continue;
            if (includeMandatory && childrenByName.TryGetValue(baseName, out IType? child) && child.IsRequired) continue;
            clone.Remove(key);
        }
    }

    private static void KeepOnlyTopLevelProperty(JsonObject clone, string propertyName)
    {
        foreach (string key in clone.Select(kv => kv.Key).ToList())
        {
            string baseName = key.StartsWith('_') ? key[1..] : key;
            if (AlwaysKept.Contains(baseName) || baseName == propertyName) continue;
            clone.Remove(key);
        }
    }

    private static void RemoveTopLevelProperty(JsonObject clone, string propertyName)
    {
        clone.Remove(propertyName);
        clone.Remove("_" + propertyName);
    }

    private static void AppendSubsettedTag(ResourceJsonNode resource)
    {
        JsonObject metaObject = resource.Meta.MutableNode;
        if (!metaObject.TryGetPropertyValue("tag", out JsonNode? tagNode) || tagNode is not JsonArray tagArray)
        {
            tagArray = new JsonArray();
            metaObject["tag"] = tagArray;
        }

        tagArray.Add(new JsonObject
        {
            ["system"] = SubsettedSystem,
            ["code"] = SubsettedCode,
        });
    }
}
