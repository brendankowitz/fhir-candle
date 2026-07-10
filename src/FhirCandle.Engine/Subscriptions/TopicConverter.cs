// <copyright file="TopicConverter.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Utils;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;
using static FhirCandle.Subscriptions.ConverterUtils;

namespace FhirCandle.Subscriptions;

/// <summary>
/// Parses SubscriptionTopic resources into <see cref="ParsedSubscriptionTopic"/>, merging the three
/// old per-version converters. R4 topics arrive as <c>Basic</c> resources carrying R5 cross-version
/// extensions; R4B/R5 topics are native <c>SubscriptionTopic</c> resources whose JSON is close enough
/// to share one parse path (R4B merely lacks <c>name</c>). Over JSON, the result codes both models
/// use ("test-passes"/"test-fails", interaction codes) are identical wire strings, so everything past
/// field extraction is shared.
/// </summary>
public sealed class TopicConverter
{
    private readonly bool _useR4BasicModel;

    /// <summary>Initializes a new instance of the <see cref="TopicConverter"/> class.</summary>
    /// <param name="fhirVersion">The FHIR version this converter parses topics for.</param>
    public TopicConverter(FhirReleases.FhirSequenceCodes fhirVersion)
    {
        _useR4BasicModel = fhirVersion == FhirReleases.FhirSequenceCodes.R4;
    }

    /// <summary>Attempts to parse a topic resource (R4 <c>Basic</c> wrapper or native
    /// <c>SubscriptionTopic</c>) into common form.</summary>
    public bool TryParse(ResourceJsonNode? topic, out ParsedSubscriptionTopic parsed)
    {
        parsed = null!;

        if (topic is null || string.IsNullOrEmpty(topic.Id))
        {
            return false;
        }

        ParsedSubscriptionTopic? result = _useR4BasicModel
            ? ParseR4Basic(topic.MutableNode, topic.Id)
            : ParseNative(topic.MutableNode, topic.Id);

        if (result is null || string.IsNullOrEmpty(result.Url))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static ParsedSubscriptionTopic? ParseR4Basic(JsonObject json, string id)
    {
        if (GetStringProp(json, "resourceType") != "Basic" ||
            !Objects(json["code"], "coding").Any(c =>
                GetStringProp(c, "system") == "http://hl7.org/fhir/fhir-types" &&
                GetStringProp(c, "code") == "SubscriptionTopic"))
        {
            return null;
        }

        (Dictionary<string, List<JsonNode>> modifierValues, _) = ParseExtensions(json["modifierExtension"] as JsonArray);
        (Dictionary<string, List<JsonNode>> values, Dictionary<string, List<JsonArray>> nested) = ParseExtensions(json["extension"] as JsonArray);

        var parsed = new ParsedSubscriptionTopic
        {
            Id = id,
            Url = GetString(values, "url"),
            Status = GetString(modifierValues, "status"),
            Name = GetString(values, "name"),
            Version = GetString(values, "version"),
            Title = GetString(values, "title"),
            Date = GetString(values, "date"),
            Description = GetString(values, "description"),
        };

        if (nested.TryGetValue("resourceTrigger", out List<JsonArray>? resourceTriggers))
        {
            foreach (JsonArray triggerExtensions in resourceTriggers)
            {
                (Dictionary<string, List<JsonNode>> rt, Dictionary<string, List<JsonArray>> rtNested) = ParseExtensions(triggerExtensions);

                Dictionary<string, List<JsonNode>> queryCriteria =
                    rtNested.TryGetValue("queryCriteria", out List<JsonArray>? qcExtensions)
                        ? ParseExtensions(qcExtensions[0]).Values
                        : [];

                AddResourceTrigger(
                    parsed,
                    resource: GetString(rt, "resource"),
                    interactions: [.. GetStrings(rt, "supportedInteraction")],
                    queryPrevious: GetString(queryCriteria, "previous"),
                    resultForCreate: GetString(queryCriteria, "resultForCreate"),
                    queryCurrent: GetString(queryCriteria, "current"),
                    resultForDelete: GetString(queryCriteria, "resultForDelete"),
                    requireBoth: GetBool(queryCriteria, "requireBoth"),
                    fhirPathCriteria: GetString(rt, "fhirPathCriteria"));
            }
        }

        if (nested.TryGetValue("eventTrigger", out List<JsonArray>? eventTriggers))
        {
            foreach (JsonArray triggerExtensions in eventTriggers)
            {
                (Dictionary<string, List<JsonNode>> et, _) = ParseExtensions(triggerExtensions);

                string resourceType = StripTypePrefix(GetString(et, "resource"));
                if (string.IsNullOrEmpty(resourceType))
                {
                    continue;
                }

                JsonObject? eventConcept = et.TryGetValue("event", out List<JsonNode>? eventValues)
                    ? eventValues.FirstOrDefault() as JsonObject
                    : null;

                AddEventTriggers(parsed, resourceType, eventConcept, GetString(et, "description"));
            }
        }

        if (nested.TryGetValue("canFilterBy", out List<JsonArray>? filters))
        {
            foreach (JsonArray filterExtensions in filters)
            {
                (Dictionary<string, List<JsonNode>> cf, _) = ParseExtensions(filterExtensions);
                AddAllowedFilter(parsed, GetString(cf, "resource"), GetString(cf, "filterParameter"), GetString(cf, "filterDefinition"));
            }
        }

        if (nested.TryGetValue("notificationShape", out List<JsonArray>? shapes))
        {
            foreach (JsonArray shapeExtensions in shapes)
            {
                (Dictionary<string, List<JsonNode>> ns, _) = ParseExtensions(shapeExtensions);
                AddNotificationShape(parsed, GetString(ns, "resource"), GetStrings(ns, "include"), GetStrings(ns, "revInclude"));
            }
        }

        return parsed;
    }

    private static ParsedSubscriptionTopic? ParseNative(JsonObject json, string id)
    {
        if (GetStringProp(json, "resourceType") != "SubscriptionTopic")
        {
            return null;
        }

        var parsed = new ParsedSubscriptionTopic
        {
            Id = id,
            Url = GetStringProp(json, "url"),
            Status = GetStringProp(json, "status"),
            Name = GetStringProp(json, "name"),
            Version = GetStringProp(json, "version"),
            Title = GetStringProp(json, "title"),
            Date = GetStringProp(json, "date"),
            Description = GetStringProp(json, "description"),
        };

        foreach (JsonObject trigger in Objects(json, "resourceTrigger"))
        {
            JsonObject? queryCriteria = trigger["queryCriteria"] as JsonObject;

            AddResourceTrigger(
                parsed,
                resource: GetStringProp(trigger, "resource"),
                interactions: [.. (trigger["supportedInteraction"] as JsonArray ?? []).Select(AsString)],
                queryPrevious: queryCriteria is null ? string.Empty : GetStringProp(queryCriteria, "previous"),
                resultForCreate: queryCriteria is null ? string.Empty : GetStringProp(queryCriteria, "resultForCreate"),
                queryCurrent: queryCriteria is null ? string.Empty : GetStringProp(queryCriteria, "current"),
                resultForDelete: queryCriteria is null ? string.Empty : GetStringProp(queryCriteria, "resultForDelete"),
                requireBoth: queryCriteria?["requireBoth"] is JsonValue rb && rb.TryGetValue(out bool requireBoth) && requireBoth,
                fhirPathCriteria: GetStringProp(trigger, "fhirPathCriteria"));
        }

        foreach (JsonObject trigger in Objects(json, "eventTrigger"))
        {
            string resourceType = StripTypePrefix(GetStringProp(trigger, "resource"));
            if (string.IsNullOrEmpty(resourceType))
            {
                continue;
            }

            AddEventTriggers(parsed, resourceType, trigger["event"] as JsonObject, GetStringProp(trigger, "description"));
        }

        foreach (JsonObject filter in Objects(json, "canFilterBy"))
        {
            AddAllowedFilter(parsed, GetStringProp(filter, "resource"), GetStringProp(filter, "filterParameter"), GetStringProp(filter, "filterDefinition"));
        }

        foreach (JsonObject shape in Objects(json, "notificationShape"))
        {
            AddNotificationShape(
                parsed,
                GetStringProp(shape, "resource"),
                (shape["include"] as JsonArray ?? []).Select(AsString),
                (shape["revInclude"] as JsonArray ?? []).Select(AsString));
        }

        return parsed;
    }

    private static void AddResourceTrigger(
        ParsedSubscriptionTopic parsed,
        string resource,
        HashSet<string> interactions,
        string queryPrevious,
        string resultForCreate,
        string queryCurrent,
        string resultForDelete,
        bool requireBoth,
        string fhirPathCriteria)
    {
        string resourceType = StripTypePrefix(resource);
        if (string.IsNullOrEmpty(resourceType))
        {
            return;
        }

        if (!parsed.ResourceTriggers.TryGetValue(resourceType, out List<ParsedSubscriptionTopic.ResourceTrigger>? triggers))
        {
            triggers = [];
            parsed.ResourceTriggers[resourceType] = triggers;
        }

        triggers.Add(new(
            resourceType,
            interactions.Contains("create"),
            interactions.Contains("update"),
            interactions.Contains("delete"),
            queryPrevious,
            resultForCreate.Equals("test-passes", StringComparison.Ordinal),
            resultForCreate.Equals("test-fails", StringComparison.Ordinal),
            queryCurrent,
            resultForDelete.Equals("test-passes", StringComparison.Ordinal),
            resultForDelete.Equals("test-fails", StringComparison.Ordinal),
            requireBoth,
            fhirPathCriteria));
    }

    private static void AddEventTriggers(ParsedSubscriptionTopic parsed, string resourceType, JsonObject? eventConcept, string description)
    {
        if (!parsed.EventTriggers.TryGetValue(resourceType, out List<ParsedSubscriptionTopic.EventTrigger>? triggers))
        {
            triggers = [];
            parsed.EventTriggers[resourceType] = triggers;
        }

        List<JsonObject> codings = eventConcept is null ? [] : [.. Objects(eventConcept, "coding")];

        if (codings.Count == 0)
        {
            string text = eventConcept is null ? string.Empty : GetStringProp(eventConcept, "text");
            triggers.Add(new(resourceType, string.Empty, string.Empty, string.IsNullOrEmpty(text) ? description : text));
            return;
        }

        foreach (JsonObject coding in codings)
        {
            string display = GetStringProp(coding, "display");
            triggers.Add(new(
                resourceType,
                GetStringProp(coding, "system"),
                GetStringProp(coding, "code"),
                string.IsNullOrEmpty(display) ? description : display));
        }
    }

    private static void AddAllowedFilter(ParsedSubscriptionTopic parsed, string resource, string filterParameter, string filterDefinition)
    {
        string resourceType = StripTypePrefix(resource);
        if (string.IsNullOrEmpty(resourceType))
        {
            return;
        }

        if (!parsed.AllowedFilters.TryGetValue(resourceType, out List<ParsedSubscriptionTopic.AllowedFilter>? filters))
        {
            filters = [];
            parsed.AllowedFilters[resourceType] = filters;
        }

        filters.Add(new(resourceType, filterParameter, filterDefinition));
    }

    private static void AddNotificationShape(ParsedSubscriptionTopic parsed, string resource, IEnumerable<string> includes, IEnumerable<string> revIncludes)
    {
        string resourceType = StripTypePrefix(resource);
        if (string.IsNullOrEmpty(resourceType))
        {
            return;
        }

        if (!parsed.NotificationShapes.TryGetValue(resourceType, out List<ParsedSubscriptionTopic.NotificationShape>? shapes))
        {
            shapes = [];
            parsed.NotificationShapes[resourceType] = shapes;
        }

        shapes.Add(new(
            resourceType,
            includes
                .Where(i => !string.IsNullOrEmpty(i))
                .Select(i => "_include=" + i.Replace("&iterate=", "&_include:iterate=", StringComparison.Ordinal))
                .ToList(),
            revIncludes
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => "_revinclude=" + r.Replace("&iterate=", "&_revinclude:iterate=", StringComparison.Ordinal))
                .ToList()));
    }
}
