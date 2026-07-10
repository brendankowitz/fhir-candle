// <copyright file="PasOperationCommon.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Net;
using FhirCandle.Models;
using FhirCandle.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>Request-shape validation shared by the PAS <c>$submit</c> and <c>$inquire</c> operations,
/// which both require a collection Bundle containing at least one Claim.</summary>
internal static class PasOperationCommon
{
    /// <summary>Validates that <paramref name="bodyResource"/> is a collection Bundle containing at
    /// least one Claim, producing the typed bundle on success or a 422 response on failure.</summary>
    public static bool TryGetClaimCollection(
        ResourceJsonNode? bodyResource,
        string operationLabel,
        string bundleProfileName,
        [NotNullWhen(true)] out BundleJsonNode? bundle,
        out FhirResponseContext failureResponse)
    {
        bundle = bodyResource switch
        {
            BundleJsonNode typed => typed,
            not null when bodyResource.ResourceType == "Bundle" => new BundleJsonNode(bodyResource.MutableNode, bodyResource.FhirVersion),
            _ => null,
        };

        if (bundle is null)
        {
            failureResponse = Unprocessable($"{operationLabel} requires a {bundleProfileName} as input.");
            return false;
        }

        if (bundle.Type != BundleJsonNode.BundleType.Collection)
        {
            failureResponse = Unprocessable($"{operationLabel} {bundleProfileName} SHALL be a `collection`.");
            bundle = null;
            return false;
        }

        if (!bundle.Entry.Any(e => e.Resource?.ResourceType == "Claim"))
        {
            failureResponse = Unprocessable("Submitted bundle does not contain any Claim resources.");
            bundle = null;
            return false;
        }

        failureResponse = new();
        return true;
    }

    private static FhirResponseContext Unprocessable(string diagnostics) => new()
    {
        StatusCode = HttpStatusCode.UnprocessableEntity,
        Outcome = SerializationUtils.BuildOutcomeForRequest(
            HttpStatusCode.UnprocessableEntity, diagnostics, OperationOutcomeJsonNode.IssueType.Structure),
    };
}
