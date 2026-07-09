// <copyright file="OpSubscriptionHook.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>
/// System-level <c>$subscription-hook</c> operation: receives a Subscription notification Bundle
/// posted by another server this store is subscribed against.
/// <para>
/// <b>Placeholder</b>: the old file parsed the notification's <c>SubscriptionStatus</c>/
/// <c>Parameters</c> first entry and registered it against live subscription tracking
/// (<c>ParseNotificationBundle</c>, <c>RegisterReceivedNotification</c>) that does not exist yet in the
/// Ignixa-model engine - subscription execution is Task 14. This validates only the basic
/// notification-bundle shape (a Bundle with at least one entry) and always acknowledges receipt; it
/// does not parse the status content or register the notification anywhere yet.
/// </para>
/// </summary>
public sealed class OpSubscriptionHook : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$subscription-hook";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://argo.run/fhir/OperationDefinition/subscription-hook" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://argo.run/fhir/OperationDefinition/subscription-hook" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://argo.run/fhir/OperationDefinition/subscription-hook" },
    };

    /// <inheritdoc/>
    public bool IsNamedQuery => false;

    /// <inheritdoc/>
    public bool AffectsState => true;

    /// <inheritdoc/>
    public bool AllowGet => false;

    /// <inheritdoc/>
    public bool AllowPost => true;

    /// <inheritdoc/>
    public bool AllowSystemLevel => true;

    /// <inheritdoc/>
    public bool AllowResourceLevel => false;

    /// <inheritdoc/>
    public bool AllowInstanceLevel => false;

    /// <inheritdoc/>
    public bool AcceptsNonFhir => false;

    /// <inheritdoc/>
    public bool ReturnsNonFhir => false;

    /// <inheritdoc/>
    public string RequiresPackage => string.Empty;

    /// <inheritdoc/>
    public HashSet<string> SupportedResources => [];

    /// <inheritdoc/>
    public bool DoOperation(
        FhirRequestContext ctx,
        Storage.VersionedFhirStore store,
        Storage.IVersionedResourceStore? resourceStore,
        ResourceJsonNode? focusResource,
        ResourceJsonNode? bodyResource,
        out FhirResponseContext opResponse)
    {
        BundleJsonNode? bundle = bodyResource switch
        {
            BundleJsonNode typed => typed,
            not null when bodyResource.ResourceType == "Bundle" => new BundleJsonNode(bodyResource.MutableNode, bodyResource.FhirVersion),
            _ => null,
        };

        if (bundle is null || bundle.Entry.Count == 0)
        {
            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, "Posted content is not a valid Subscription notification bundle", OperationOutcomeJsonNode.IssueType.Structure),
            };
            return false;
        }

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Subscription Notification Received"),
        };
        return true;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("resource", "in", 1, "1", "Bundle", "The notification bundle."),
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "OperationOutcome", "Acknowledgement of receipt."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
