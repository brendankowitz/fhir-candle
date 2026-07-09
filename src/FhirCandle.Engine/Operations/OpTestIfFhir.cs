// <copyright file="OpTestIfFhir.cs" company="Microsoft Corporation">
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

/// <summary>System-level <c>$test-if-fhir</c> operation: reports whether the posted content is a
/// structurally-parseable FHIR resource. Ported from the old file's <c>OpTestIfFhir</c> class (the old
/// file was named <c>OpIsFhir.cs</c> but its class and operation name were always <c>$test-if-fhir</c>).</summary>
public sealed class OpTestIfFhir : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$test-if-fhir";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://ginoc.io/fhir/OperationDefinition/test-if-fhir" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://ginoc.io/fhir/OperationDefinition/test-if-fhir" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://ginoc.io/fhir/OperationDefinition/test-if-fhir" },
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
    public bool AcceptsNonFhir => true;

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
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, "Body is empty", OperationOutcomeJsonNode.IssueType.Structure),
            };
            return false;
        }

        if (bodyResource is null)
        {
            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, "Content is not parseable as FHIR", OperationOutcomeJsonNode.IssueType.Structure),
            };
            return false;
        }

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Content is a structurally-parseable FHIR resource"),
        };
        return true;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "OperationOutcome", "An OperationOutcome with information about the submitted data."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
