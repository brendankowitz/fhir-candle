// <copyright file="ConverterUtils.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text.Json.Nodes;

namespace FhirCandle.Subscriptions;

/// <summary>
/// JSON-walking utilities shared by <see cref="TopicConverter"/> and <see cref="SubscriptionConverter"/>.
/// Ports the old Firely <c>ConverterUtils.ParseExtensions</c> family from <c>Extension</c> POCOs to
/// <see cref="JsonObject"/> trees, including FHIR JSON's <c>_property</c> sibling convention for
/// extensions on primitive values.
/// </summary>
internal static class ConverterUtils
{
    /// <summary>Base URL of the R5 SubscriptionTopic cross-version extensions used by R4 Basic-wrapped topics.</summary>
    internal const string UrlSt5 = "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.";

    /// <summary>Base URL of the subscriptions-backport extensions used by R4/R4B Subscriptions.</summary>
    internal const string UrlBackport = "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/";

    /// <summary>
    /// Parses an extension array into simple values (any <c>value[x]</c> property) and nested
    /// sub-extension arrays, keyed by the extension URL with the known long prefixes stripped.
    /// Extensions with unknown absolute URLs are skipped, matching the old converter.
    /// </summary>
    internal static (Dictionary<string, List<JsonNode>> Values, Dictionary<string, List<JsonArray>> Nested) ParseExtensions(JsonArray? extensions)
    {
        Dictionary<string, List<JsonNode>> values = [];
        Dictionary<string, List<JsonArray>> nested = [];

        foreach (JsonObject ext in (extensions ?? []).OfType<JsonObject>())
        {
            string url = GetStringProp(ext, "url");
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            string name;
            if (url.StartsWith(UrlSt5, StringComparison.Ordinal))
            {
                name = url[UrlSt5.Length..];
            }
            else if (url.StartsWith(UrlBackport, StringComparison.Ordinal))
            {
                name = url[UrlBackport.Length..];
            }
            else if (url.StartsWith("http", StringComparison.Ordinal))
            {
                continue;
            }
            else
            {
                name = url;
            }

            if (ext["extension"] is JsonArray subExtensions)
            {
                if (!nested.TryGetValue(name, out List<JsonArray>? nestedList))
                {
                    nestedList = [];
                    nested[name] = nestedList;
                }

                nestedList.Add(subExtensions);
            }

            if (!values.TryGetValue(name, out List<JsonNode>? valueList))
            {
                valueList = [];
                values[name] = valueList;
            }

            JsonNode? value = ext.FirstOrDefault(kvp =>
                kvp.Key.StartsWith("value", StringComparison.Ordinal) && kvp.Value is not null).Value;

            if (value is not null)
            {
                valueList.Add(value);
            }
        }

        return (values, nested);
    }

    /// <summary>Gets the extension array attached to a primitive property via its <c>_property</c> sibling.</summary>
    internal static JsonArray? GetUnderscoreExtensions(JsonObject owner, string propertyName) =>
        (owner["_" + propertyName] as JsonObject)?["extension"] as JsonArray;

    internal static string GetString(Dictionary<string, List<JsonNode>> values, string name) =>
        values.TryGetValue(name, out List<JsonNode>? list) ? AsString(list.FirstOrDefault()) : string.Empty;

    internal static IEnumerable<string> GetStrings(Dictionary<string, List<JsonNode>> values, string name) =>
        values.TryGetValue(name, out List<JsonNode>? list) ? list.Select(AsString) : [];

    internal static bool GetBool(Dictionary<string, List<JsonNode>> values, string name) =>
        values.TryGetValue(name, out List<JsonNode>? list) &&
        bool.TryParse(AsString(list.FirstOrDefault()), out bool parsed) &&
        parsed;

    /// <summary>Renders a JSON value node as a plain string: raw string values pass through,
    /// numbers/booleans use their JSON literal, and Reference objects yield their <c>reference</c>.</summary>
    internal static string AsString(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue(out string? s) => s ?? string.Empty,
        JsonValue value => value.ToJsonString(),
        JsonObject obj when obj["reference"] is JsonValue reference && reference.TryGetValue(out string? r) => r ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>Reads a string property directly off a <see cref="JsonObject"/> (empty when absent
    /// or not a string).</summary>
    internal static string GetStringProp(JsonObject obj, string propertyName) =>
        obj[propertyName] is JsonValue value && value.TryGetValue(out string? s) ? s ?? string.Empty : string.Empty;

    /// <summary>Enumerates the <see cref="JsonObject"/> items of an array-valued property.</summary>
    internal static IEnumerable<JsonObject> Objects(JsonNode? owner, string propertyName) =>
        (owner?[propertyName] as JsonArray)?.OfType<JsonObject>() ?? [];

    /// <summary>Reduces a resource URI (e.g. a StructureDefinition canonical) to its type name.</summary>
    internal static string StripTypePrefix(string resource) =>
        resource.Contains('/') ? resource[(resource.LastIndexOf('/') + 1)..] : resource;
}
