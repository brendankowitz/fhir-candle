// <copyright file="OpFeatureQuery.cs" company="Microsoft Corporation">
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
/// Structural port of the <c>$feature-query</c> operation (CapabilityStatement feature-query IG).
/// <para>
/// <b>Placeholder answer logic</b>: the old file's answers came from
/// <c>VersionedFhirStore.TryQueryCapabilityFeature</c>, which inspected a typed Firely
/// <c>CapabilityStatement</c> POCO graph. That POCO graph no longer exists (the new engine's
/// CapabilityStatement is a raw JSON literal - see <c>BuildCapabilityStatement</c>), and porting the
/// feature-matching logic against the JSON shape is out of scope for this task (no test requires it).
/// This port keeps the request-parsing shape (the <c>param=feature[@context][(value)]</c> query-string
/// mini-grammar) and always reports back <c>processing-status: not-supported</c> for every requested
/// feature, rather than a real answer, so the operation is discoverable and well-formed without
/// fabricating results.
/// </para>
/// </summary>
public sealed class OpFeatureQuery : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$feature-query";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://www.hl7.org/fhir/uv/capstmt/OperationDefinition/feature-query" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://www.hl7.org/fhir/uv/capstmt/OperationDefinition/feature-query" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://www.hl7.org/fhir/uv/capstmt/OperationDefinition/feature-query" },
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
        string[] paramValues = queryParams.GetValues("param") ?? [];

        var parameterArray = new JsonArray();

        foreach (string? param in paramValues)
        {
            (string feature, string context)? request = ParseFeatureRequestParam(param);
            if (request is null)
            {
                continue;
            }

            var parts = new JsonArray
            {
                new JsonObject { ["name"] = "name", ["valueUri"] = request.Value.feature },
            };

            if (!string.IsNullOrEmpty(request.Value.context))
            {
                parts.Add(new JsonObject { ["name"] = "context", ["valueUri"] = request.Value.context });
            }

            parts.Add(new JsonObject { ["name"] = "processing-status", ["valueCode"] = "not-supported" });

            parameterArray.Add(new JsonObject { ["name"] = "feature", ["part"] = parts });
        }

        var resultParameters = new JsonObject
        {
            ["resourceType"] = "Parameters",
            ["parameter"] = parameterArray,
        };

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Resource = JsonSourceNodeFactory.Parse((JsonNode)resultParameters),
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Feature request query has been processed."),
        };
        return true;
    }

    /// <summary>Parses a single <c>param=feature[@context][(value)]</c> query-string entry.</summary>
    private static (string Feature, string Context)? ParseFeatureRequestParam(string? param)
    {
        if (string.IsNullOrEmpty(param))
        {
            return null;
        }

        string buffer = param;
        if (param.EndsWith(')'))
        {
            int startLoc = param.LastIndexOf('(');
            buffer = buffer[..startLoc];
        }

        int contextSepLoc = buffer.IndexOf('@');
        string feature = contextSepLoc != -1 ? buffer[..contextSepLoc] : buffer;
        string context = contextSepLoc != -1 ? buffer[(contextSepLoc + 1)..] : string.Empty;

        return (feature, context);
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("param", "in", 0, "*", "string", "A string in the format of '{feature}(@{context}):value'."),
            OperationDefinitionBuilder.Param("feature", "in", 0, "*", "string", "A complex parameter include parts for feature, context, and value."),
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "Parameters", "A parameters resource with details about support for the requested feature."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
