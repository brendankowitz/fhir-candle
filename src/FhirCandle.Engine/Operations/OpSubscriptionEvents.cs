// <copyright file="OpSubscriptionEvents.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Serialization;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>
/// Instance-level Subscription <c>$events</c> operation: builds a notification bundle covering the
/// requested event-number range (or the full range when unspecified) from the tracked subscription's
/// generated events.
/// </summary>
public sealed class OpSubscriptionEvents : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$events";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://hl7.org/fhir/uv/subscriptions-backport/OperationDefinition/backport-subscription-events" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://hl7.org/fhir/uv/subscriptions-backport/OperationDefinition/backport-subscription-events" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://hl7.org/fhir/OperationDefinition/Subscription-events" },
    };

    /// <inheritdoc/>
    public bool IsNamedQuery => false;

    /// <inheritdoc/>
    public bool AffectsState => false;

    /// <inheritdoc/>
    public bool AllowGet => true;

    /// <inheritdoc/>
    public bool AllowPost => true;

    /// <inheritdoc/>
    public bool AllowSystemLevel => false;

    /// <inheritdoc/>
    public bool AllowResourceLevel => false;

    /// <inheritdoc/>
    public bool AllowInstanceLevel => true;

    /// <inheritdoc/>
    public bool AcceptsNonFhir => false;

    /// <inheritdoc/>
    public bool ReturnsNonFhir => false;

    /// <inheritdoc/>
    public string RequiresPackage => string.Empty;

    /// <inheritdoc/>
    public HashSet<string> SupportedResources => ["Subscription"];

    /// <inheritdoc/>
    public bool DoOperation(
        FhirRequestContext ctx,
        Storage.VersionedFhirStore store,
        Storage.IVersionedResourceStore? resourceStore,
        ResourceJsonNode? focusResource,
        ResourceJsonNode? bodyResource,
        out FhirResponseContext opResponse)
    {
        if (string.IsNullOrEmpty(ctx.Id) || !store.TryGetParsedSubscription(ctx.Id, out ParsedSubscription? subscription))
        {
            opResponse = new()
            {
                StatusCode = HttpStatusCode.NotFound,
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Subscription {ctx.Id} was not found."),
            };
            return false;
        }

        string eventsSince = string.Empty;
        string eventsUntil = string.Empty;
        string contentLevel = string.Empty;

        if (!string.IsNullOrEmpty(ctx.UrlQuery))
        {
            System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(ctx.UrlQuery);
            foreach (string? key in query.AllKeys)
            {
                string value = key is null ? string.Empty : query[key] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                switch (key)
                {
                    case "events-since-number":
                    case "eventssincenumber":
                    case "eventsSinceNumber":
                        eventsSince = value;
                        break;

                    case "events-until-number":
                    case "eventsuntilnumber":
                    case "eventsUntilNumber":
                        eventsUntil = value;
                        break;

                    case "content":
                        contentLevel = value;
                        break;
                }
            }
        }

        if (bodyResource?.ResourceType == "Parameters" &&
            bodyResource.MutableNode["parameter"] is JsonArray bodyParameters)
        {
            foreach (JsonObject parameter in bodyParameters.OfType<JsonObject>())
            {
                string name = parameter["name"]?.GetValue<string>() ?? string.Empty;
                string value = parameter.FirstOrDefault(kvp => kvp.Key.StartsWith("value", StringComparison.Ordinal)).Value?.ToString() ?? string.Empty;

                switch (name)
                {
                    case "eventsSinceNumber":
                        eventsSince = value;
                        break;

                    case "eventsUntilNumber":
                        eventsUntil = value;
                        break;

                    case "content":
                        contentLevel = value;
                        break;
                }
            }
        }

        if (!long.TryParse(eventsSince, out long sinceNumber))
        {
            sinceNumber = 0;
        }

        if (!long.TryParse(eventsUntil, out long untilNumber))
        {
            untilNumber = subscription.CurrentEventCount;
        }

        var eventNumbers = new List<long>();
        for (long i = sinceNumber; i <= untilNumber; i++)
        {
            eventNumbers.Add(i);
        }

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Resource = store.BundleForSubscriptionEvents(ctx.Id, eventNumbers, "query-event", contentLevel),
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, $"Events for subscription {ctx.Id}."),
        };
        return true;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("eventsSinceNumber", "in", 0, "1", "integer64", "The starting event number, inclusive of this event (lower bound)."),
            OperationDefinitionBuilder.Param("eventsUntilNumber", "in", 0, "1", "integer64", "The ending event number, inclusive of this event (upper bound)."),
            OperationDefinitionBuilder.Param("content", "in", 0, "1", "code", "Requested content style of returned data (e.g., empty, id-only, full-resource). A hint only; MAY be ignored."),
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "Bundle", "A notification Bundle covering the requested events."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
