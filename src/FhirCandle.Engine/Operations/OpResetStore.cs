// <copyright file="OpResetStore.cs" company="Microsoft Corporation">
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

/// <summary>System-level <c>$reset-store</c> operation: deletes all non-protected resources (and,
/// unless <c>keep-conformance</c> is set, conformance resources too). Semantics match the previous
/// port introduced in commit f4675d0.</summary>
public sealed class OpResetStore : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$reset-store";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://ginoc.io/fhir/OperationDefinition/reset-store" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://ginoc.io/fhir/OperationDefinition/reset-store" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://ginoc.io/fhir/OperationDefinition/reset-store" },
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
        System.Collections.Specialized.NameValueCollection queryParams = System.Web.HttpUtility.ParseQueryString(ctx.UrlQuery);
        string[] stringValues = queryParams.GetValues("keep-conformance") ?? [];
        bool keepConformance = stringValues.Any(value => bool.TryParse(value, out bool result) && result);

        if (bodyResource is not null && bodyResource.ResourceType == "Parameters")
        {
            ParametersJsonNode parameters = bodyResource is ParametersJsonNode typed
                ? typed
                : new ParametersJsonNode(bodyResource.MutableNode, bodyResource.FhirVersion);

            ParameterJsonNode? keepConformanceParam = parameters.FindParameter("keep-conformance");
            JsonNode? value = keepConformanceParam?.GetValue("valueBoolean");
            if (value is not null)
            {
                keepConformance = value.GetValue<bool>();
            }
        }

        store.ResetStore(keepConformance);

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Outcome = SerializationUtils.BuildOutcomeForRequest(
                HttpStatusCode.OK,
                keepConformance
                    ? "All non-protected and non-conformance resources have been removed."
                    : "All non-protected resources have been removed."),
        };

        return true;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("keep-conformance", "in", 0, "1", "boolean", "True to keep conformance resources, false to delete them. Default is false (delete)."),
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "OperationOutcome", "An OperationOutcome resource with the results of the request."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
