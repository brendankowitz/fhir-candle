// <copyright file="OpSubscriptionStatus.cs" company="Microsoft Corporation">
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
/// Resource/instance-level Subscription <c>$status</c> operation: returns a searchset Bundle of
/// notification status resources for the requested subscriptions (all tracked subscriptions when no
/// <c>id</c>/<c>status</c> filters are given).
/// </summary>
public sealed class OpSubscriptionStatus : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$status";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://hl7.org/fhir/uv/subscriptions-backport/OperationDefinition/backport-subscription-status" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://hl7.org/fhir/uv/subscriptions-backport/OperationDefinition/backport-subscription-status" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://hl7.org/fhir/OperationDefinition/Subscription-status" },
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
    public bool AllowResourceLevel => true;

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
        var subscriptionIds = new List<string>();
        var statusFilters = new List<string>();

        if (!string.IsNullOrEmpty(ctx.Id))
        {
            subscriptionIds.Add(ctx.Id);
        }

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
                    case "id":
                        subscriptionIds.AddRange(value.Split(','));
                        break;

                    case "status":
                        statusFilters.AddRange(value.Split(','));
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
                    case "id" when !string.IsNullOrEmpty(value):
                        subscriptionIds.Add(value);
                        break;

                    case "status" when !string.IsNullOrEmpty(value):
                        statusFilters.Add(value);
                        break;
                }
            }
        }

        var filters = new HashSet<string>(statusFilters);

        IEnumerable<string> candidateIds = subscriptionIds.Count != 0
            ? subscriptionIds.Distinct()
            : store.CurrentSubscriptions.Select(s => s.Id);

        var entries = new JsonArray();

        foreach (string id in candidateIds)
        {
            if (!store.TryGetParsedSubscription(id, out ParsedSubscription? subscription) ||
                (filters.Count != 0 && !filters.Contains(subscription.CurrentStatus)))
            {
                continue;
            }

            ResourceJsonNode? status = store.StatusForSubscription(id, "query-status");
            if (status is not null)
            {
                entries.Add(new JsonObject
                {
                    ["fullUrl"] = $"urn:uuid:{status.Id}",
                    ["resource"] = status.MutableNode.DeepClone(),
                });
            }
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "searchset",
            ["timestamp"] = DateTimeOffset.Now.ToString("o"),
            ["entry"] = entries,
        };

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Resource = JsonSourceNodeFactory.Parse((JsonNode)bundle),
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "See resource for $status data"),
        };
        return true;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("id", "in", 0, "*", "id", "One or more Subscription ids to get status for. In the absence of any, the server returns status for all Subscriptions available to the caller."),
            OperationDefinitionBuilder.Param("status", "in", 0, "*", "code", "A Subscription status to filter by (e.g., \"active\")."),
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "Bundle", "A searchset Bundle of notification status resources for the matching subscriptions."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
