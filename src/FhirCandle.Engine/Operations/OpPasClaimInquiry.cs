// <copyright file="OpPasClaimInquiry.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Serialization;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>
/// DaVinci PAS <c>$inquire</c>: answers an inquiry for a previously-submitted Pre-Authorization,
/// mirroring each submitted Claim back as a certified-in-total ClaimResponse in a collection Bundle.
/// </summary>
public sealed class OpPasClaimInquiry : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$inquire";

    /// <inheritdoc/>
    public string OperationVersion => "1.2.0";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquiry" },
    };

    /// <inheritdoc/>
    public bool IsNamedQuery => false;

    /// <inheritdoc/>
    public bool AffectsState => false;

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
            bodyResource, "PAS Claim Inquiry", "PASClaimInquiryBundle", out BundleJsonNode? requestBundle, out opResponse))
        {
            return false;
        }

        string now = DateTimeOffset.UtcNow.ToString("o");

        var responseEntries = new JsonArray();

        foreach (BundleComponentJsonNode entry in requestBundle.Entry)
        {
            if (entry.Resource is not { ResourceType: "Claim" } claimResource)
            {
                continue;
            }

            JsonObject claim = claimResource.MutableNode;
            string claimResponseId = Guid.NewGuid().ToString();

            var claimResponse = new JsonObject
            {
                ["resourceType"] = "ClaimResponse",
                ["id"] = claimResponseId,
                ["meta"] = new JsonObject { ["lastUpdated"] = now },
                ["identifier"] = claim["identifier"]?.DeepClone(),
                ["status"] = claim["status"]?.DeepClone(),
                ["type"] = claim["type"]?.DeepClone(),
                ["use"] = claim["use"]?.DeepClone(),
                ["patient"] = claim["patient"]?.DeepClone(),
                ["created"] = claim["created"]?.DeepClone(),
                ["insurer"] = claim["insurer"]?.DeepClone()
                    ?? new JsonObject { ["display"] = "Claim provided no insurer!" },
                ["requestor"] = claim["provider"]?.DeepClone(),
                ["request"] = new JsonObject { ["reference"] = $"Claim/{claimResource.Id}" },
                ["insurance"] = MapArray(claim["insurance"], insurance => new JsonObject
                {
                    ["sequence"] = insurance["sequence"]?.DeepClone(),
                    ["focal"] = insurance["focal"]?.DeepClone(),
                    ["coverage"] = insurance["coverage"]?.DeepClone(),
                    ["extension"] = insurance["extension"]?.DeepClone(),
                    ["id"] = insurance["id"]?.DeepClone(),
                }),
                ["outcome"] = "complete",
                ["item"] = MapArray(claim["item"], item => new JsonObject
                {
                    ["itemSequence"] = item["sequence"]?.DeepClone(),
                    ["extension"] = item["extension"]?.DeepClone(),
                    ["id"] = item["id"]?.DeepClone(),
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
                        ["extension"] = new JsonArray(new JsonObject
                        {
                            ["url"] = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction",
                            ["extension"] = new JsonArray(
                                new JsonObject
                                {
                                    ["url"] = "number",
                                    ["valueString"] = $"AUTH{item["sequence"]?.GetValue<int>():0000}",
                                },
                                new JsonObject
                                {
                                    ["url"] = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode",
                                    ["valueCodeableConcept"] = new JsonObject
                                    {
                                        ["coding"] = new JsonArray(new JsonObject
                                        {
                                            ["system"] = "https://codesystem.x12.org/005010/306",
                                            ["code"] = "A1",
                                            ["display"] = "Certified in total",
                                        }),
                                    },
                                }),
                        }),
                    }),
                    ["detail"] = MapArray(item["detail"], detail => new JsonObject
                    {
                        ["detailSequence"] = detail["sequence"]?.DeepClone(),
                        ["extension"] = detail["extension"]?.DeepClone(),
                        ["id"] = detail["id"]?.DeepClone(),
                        ["subDetail"] = MapArray(detail["subDetail"], subDetail => new JsonObject
                        {
                            ["subDetailSequence"] = subDetail["sequence"]?.DeepClone(),
                            ["extension"] = subDetail["extension"]?.DeepClone(),
                            ["id"] = subDetail["id"]?.DeepClone(),
                        }),
                    }),
                }),
            };

            responseEntries.Add(new JsonObject
            {
                ["fullUrl"] = $"urn:uuid:{claimResponseId}",
                ["resource"] = claimResponse,
            });
        }

        var responseBundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["meta"] = new JsonObject { ["lastUpdated"] = now },
            ["type"] = "collection",
            ["timestamp"] = now,
            ["entry"] = responseEntries,
        };

        opResponse = new FhirResponseContext
        {
            StatusCode = HttpStatusCode.OK,
            Resource = JsonSourceNodeFactory.Parse((JsonNode)responseBundle),
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "See response bundle for details."),
        };
        return true;
    }

    /// <summary>Projects each object element of <paramref name="source"/> (when it is an array)
    /// through <paramref name="map"/>; null/absent input maps to null (the property is omitted).</summary>
    private static JsonArray? MapArray(JsonNode? source, Func<JsonObject, JsonObject> map) =>
        source is JsonArray array
            ? new JsonArray(array.OfType<JsonObject>().Select(JsonNode (o) => map(o)).ToArray())
            : null;

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        // operation has canonical definition in package
        return null;
    }
}
