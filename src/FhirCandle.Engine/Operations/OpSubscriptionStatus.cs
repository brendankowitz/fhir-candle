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
/// Resource/instance-level Subscription <c>$status</c> operation.
/// <para>
/// <b>Placeholder</b>: the old file's status generation (<c>VersionedFhirStore._subscriptions</c>,
/// <c>StatusForSubscription</c>) depends on live subscription/notification tracking that does not
/// exist yet in the Ignixa-model engine - subscription execution is Task 14. This parses the
/// <c>id</c>/<c>status</c> filters from the request (matching the old file's input shape) but returns
/// a well-formed, empty searchset Bundle rather than applying them against real subscription state.
/// </para>
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
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "searchset",
            ["entry"] = new JsonArray(),
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
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "Bundle", "A searchset Bundle. This placeholder always returns an empty searchset - see Task 14 for real subscription-status population."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
