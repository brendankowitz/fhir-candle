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
/// Instance-level Subscription <c>$events</c> operation.
/// <para>
/// <b>Placeholder</b>: the old file answered from a live in-memory subscription/notification ledger
/// (<c>VersionedFhirStore._subscriptions</c>, <c>BundleForSubscriptionEvents</c>) that does not exist
/// yet in the Ignixa-model engine - subscription execution is Task 14. The instance-operation dispatch
/// already guarantees the named Subscription exists (via <paramref name="focusResource"/> in
/// <see cref="Storage.VersionedFhirStore.InstanceOperation"/>), so this returns a well-formed, empty
/// history Bundle - the shape a real implementation would extend with actual notification-bundle
/// entries once Task 14 lands.
/// </para>
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
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "history",
            ["entry"] = new JsonArray(),
        };

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Resource = JsonSourceNodeFactory.Parse((JsonNode)bundle),
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
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "Bundle", "A history Bundle. This placeholder always returns an empty history Bundle - see Task 14 for real event population."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
