// <copyright file="OpSubscriptionHook.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Serialization;
using Ignixa.Models;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>
/// System-level <c>$subscription-hook</c> operation: receives a Subscription notification Bundle
/// posted by another server this store is subscribed against, parses its first-entry status resource,
/// stores the bundle, and registers the received notification.
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
        Bundle? bundle = bodyResource switch
        {
            Bundle typed => typed,
            not null when bodyResource.ResourceType == "Bundle" => new Bundle(bodyResource.MutableNode, bodyResource.FhirVersion),
            _ => null,
        };

        if (bundle is null || bundle.Entry.Count == 0)
        {
            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, "Posted content is not a valid Subscription notification bundle", OperationOutcomeIssue.IssueTypeCommon.Structure),
            };
            return false;
        }

        if (string.IsNullOrEmpty(bundle.Id))
        {
            bundle.Id = Guid.NewGuid().ToString();
        }

        FhirCandle.Models.ParsedSubscriptionStatus? status = store.ParseNotificationBundle(bundle);

        if (status is null)
        {
            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, "Posted content is not a valid Subscription notification bundle", OperationOutcomeIssue.IssueTypeCommon.Structure),
            };
            return false;
        }

        // persist the notification bundle so it can be inspected later; a duplicate id surfaces the
        // store's conflict response and must not register a second received notification
        ResourceJsonNode? stored = null;
        HttpStatusCode createStatus = HttpStatusCode.InternalServerError;
        OperationOutcome? createOutcome = null;

        if (store.GetStore("Bundle") is { } bundleStore)
        {
            stored = bundleStore.InstanceCreate(ctx, bundle, allowExistingId: true, out createStatus, out createOutcome);
        }

        if (stored is null)
        {
            opResponse = new()
            {
                StatusCode = createStatus,
                Outcome = createOutcome ?? SerializationUtils.BuildOutcomeForRequest(createStatus, "Failed to store notification bundle"),
            };
            return false;
        }

        store.RegisterReceivedNotification(stored.Id, status);

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
