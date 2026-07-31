// <copyright file="OpPasClaimSubmit.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using Ignixa.Models;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>
/// DaVinci PAS <c>$submit</c>: accepts a Pre-Authorization Claim Request as a collection Bundle
/// containing the PASClaimRequest and referenced resources, stores it, and returns a response Bundle
/// with a queued ClaimResponse plus the request's Organization/Patient/Coverage resources.
/// </summary>
public sealed class OpPasClaimSubmit : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$submit";

    /// <inheritdoc/>
    public string OperationVersion => "1.2.0";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-submit" },
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
    public bool AllowSystemLevel => false;

    /// <inheritdoc/>
    public bool AllowResourceLevel => true;

    /// <inheritdoc/>
    public bool AllowInstanceLevel => false;

    /// <inheritdoc/>
    public bool AcceptsNonFhir => true;

    /// <inheritdoc/>
    public bool ReturnsNonFhir => false;

    /// <inheritdoc/>
    public string RequiresPackage => "hl7.fhir.us.davinci-pas";

    /// <inheritdoc/>
    public HashSet<string> SupportedResources => new()
    {
        "Claim"
    };

    /// <inheritdoc/>
    public bool DoOperation(
        FhirRequestContext ctx,
        Storage.VersionedFhirStore store,
        Storage.IVersionedResourceStore? resourceStore,
        ResourceJsonNode? focusResource,
        ResourceJsonNode? bodyResource,
        out FhirResponseContext opResponse)
    {
        if (!PasOperationCommon.TryGetClaimCollection(
            bodyResource, "PAS Claim Submit", "PASRequestBundle", out Bundle? requestBundle, out opResponse))
        {
            return false;
        }

        var responseOutcome = new OperationOutcome { Id = Guid.NewGuid().ToString() };

        // ensure that the first entry is a claim
        ResourceJsonNode? firstResource = requestBundle.Entry.Count > 0 ? requestBundle.Entry[0].Resource : null;

        if (firstResource is null || firstResource.ResourceType != "Claim")
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.BadRequest, OperationOutcomeIssue.IssueTypeCommon.BusinessRule,
                "First entry in bundle is not a Claim.");
            return false;
        }

        JsonObject claim = firstResource.MutableNode;
        string claimId = firstResource.Id;

        if (claim["identifier"] is not JsonArray { Count: > 0 })
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.BadRequest, OperationOutcomeIssue.IssueTypeCommon.Required,
                $"Claim {claimId} is missing mandatory `identifier` element.");
            return false;
        }

        if (claim["provider"] is null)
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.BadRequest, OperationOutcomeIssue.IssueTypeCommon.Required,
                $"Claim {claimId} is missing mandatory `provider` element.");
            return false;
        }

        if (claim["insurance"] is not JsonArray { Count: > 0 })
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.BadRequest, OperationOutcomeIssue.IssueTypeCommon.Required,
                $"Claim {claimId} is missing mandatory `insurance` element.");
            return false;
        }

        if (claim["item"] is not JsonArray { Count: > 0 } claimItems)
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.BadRequest, OperationOutcomeIssue.IssueTypeCommon.Required,
                $"Claim {claimId} is missing mandatory `item` element.");
            return false;
        }

        // store the request bundle (a clone - the original stays available for building the response)
        if (!StoreClone(store, ctx, "Bundle", requestBundle.MutableNode))
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.InternalServerError, OperationOutcomeIssue.IssueTypeCommon.Exception,
                "Failed to store claim request bundle.");
            return false;
        }

        string now = DateTimeOffset.UtcNow.ToString("o");

        // build a claim response
        var claimResponse = new JsonObject
        {
            ["resourceType"] = "ClaimResponse",
            ["id"] = Guid.NewGuid().ToString(),
            ["meta"] = new JsonObject { ["lastUpdated"] = now },
            ["identifier"] = new JsonArray(new JsonObject
            {
                ["system"] = "http://hl7.org/fhir/us/davinci-pas/ClaimResponse",
                ["value"] = Guid.NewGuid().ToString(),
            }),
            ["status"] = "active",
            ["type"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/claim-type",
                    ["code"] = "professional",
                }),
            },
            ["use"] = "preauthorization",
            ["patient"] = claim["patient"]?.DeepClone(),
            ["created"] = now,
            ["insurer"] = claim["insurer"]?.DeepClone()
                ?? new JsonObject { ["display"] = "Claim provided no insurer!" },
            ["request"] = new JsonObject { ["reference"] = $"Claim/{claimId}" },
            ["outcome"] = "queued",
            ["disposition"] = "Claim accepted.",
            ["item"] = new JsonArray(claimItems.OfType<JsonObject>().Select(JsonNode (item) => new JsonObject
            {
                ["itemSequence"] = item["sequence"]?.DeepClone(),
                ["noteNumber"] = new JsonArray(1),
                ["adjudication"] = new JsonArray(new JsonObject
                {
                    ["category"] = new JsonObject
                    {
                        ["coding"] = new JsonArray(new JsonObject
                        {
                            ["system"] = "http://terminology.hl7.org/CodeSystem/adjudication",
                            ["code"] = "submitted",
                        }),
                    },
                }),
            }).ToArray()),
        };

        // store the claim response locally
        if (!StoreClone(store, ctx, "ClaimResponse", claimResponse))
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.InternalServerError, OperationOutcomeIssue.IssueTypeCommon.Exception,
                "Failed to store claim response.");
            return false;
        }

        // build the claim response bundle: the ClaimResponse first, then the request bundle's
        // Organization/Patient/Coverage entries copied through
        var responseEntries = new JsonArray(new JsonObject
        {
            ["fullUrl"] = $"ClaimResponse/{claimResponse["id"]!.GetValue<string>()}",
            ["resource"] = claimResponse,
        });

        foreach (BundleEntry entry in requestBundle.Entry)
        {
            if (entry.Resource?.ResourceType is not ("Organization" or "Patient" or "Coverage"))
            {
                continue;
            }

            responseEntries.Add(new JsonObject
            {
                ["fullUrl"] = entry.FullUrl,
                ["resource"] = entry.Resource.MutableNode.DeepClone(),
            });
        }

        var responseBundleObj = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["meta"] = new JsonObject { ["lastUpdated"] = now },
            ["identifier"] = requestBundle.MutableNode["identifier"]?.DeepClone(),
            ["type"] = "collection",
            ["timestamp"] = now,
            ["entry"] = responseEntries,
        };

        // store the claim response bundle
        if (!StoreClone(store, ctx, "Bundle", responseBundleObj))
        {
            opResponse = FailWith(responseOutcome, HttpStatusCode.InternalServerError, OperationOutcomeIssue.IssueTypeCommon.Exception,
                "Failed to store claim response bundle.");
            return false;
        }

        responseOutcome.Issue.Add(new OperationOutcomeIssue
        {
            SeverityCode = OperationOutcomeIssue.IssueSeverityCode.Information,
            IssueTypeCode = OperationOutcomeIssue.IssueTypeCommon.Informational,
            Diagnostics = "Claim request has been accepted and a claim response bundle stored.",
        });

        opResponse = new FhirResponseContext
        {
            StatusCode = HttpStatusCode.OK,
            Resource = JsonSourceNodeFactory.Parse(responseBundleObj),
            Outcome = responseOutcome,
        };
        return true;
    }

    /// <summary>Stores a clone of <paramref name="resource"/> via a POST create, so the caller's
    /// instance stays detached from the store.</summary>
    private static bool StoreClone(
        Storage.VersionedFhirStore store, FhirRequestContext ctx, string resourceType, JsonObject resource) =>
        store.InstanceCreate(
            new FhirRequestContext(store, "POST", resourceType, JsonSourceNodeFactory.Parse((JsonNode)resource.DeepClone()))
            {
                Authorization = ctx.Authorization,
            },
            out _);

    private static FhirResponseContext FailWith(
        OperationOutcome outcome, HttpStatusCode statusCode, OperationOutcomeIssue.IssueTypeCommon issueType, string diagnostics)
    {
        outcome.Issue.Add(new OperationOutcomeIssue
        {
            SeverityCode = OperationOutcomeIssue.IssueSeverityCode.Error,
            IssueTypeCode = issueType,
            Diagnostics = diagnostics,
        });

        return new FhirResponseContext
        {
            StatusCode = statusCode,
            Outcome = outcome,
        };
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        // operation has canonical definition in package
        return null;
    }
}
