// <copyright file="OpConvert.cs" company="Microsoft Corporation">
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

/// <summary>System-level <c>$convert</c> operation. This server has a single wire format per FHIR
/// version, so "conversion" is a structural parse-and-echo of whatever was posted.</summary>
public sealed class OpConvert : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$convert";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://hl7.org/fhir/OperationDefinition/Resource-convert" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://hl7.org/fhir/OperationDefinition/Resource-convert" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://hl7.org/fhir/OperationDefinition/Resource-convert" },
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
        if (string.IsNullOrEmpty(ctx.SourceContent))
        {
            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, "Body is empty", OperationOutcomeIssue.IssueTypeCommon.Structure),
            };
            return false;
        }

        if (bodyResource is null)
        {
            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, "Content is not parseable as FHIR", OperationOutcomeIssue.IssueTypeCommon.Structure),
            };
            return false;
        }

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Resource = bodyResource,
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Successful conversion"),
        };
        return true;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("resource", "in", 1, "1", "Resource", "The resource that is to be converted"),
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "Resource", "The resource after conversion"));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
