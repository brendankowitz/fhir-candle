// <copyright file="OperationDefinitionBuilder.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text.Json.Nodes;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>Builds the common OperationDefinition JSON envelope shared by every <see cref="IFhirOperation"/>
/// port. Ignixa has no typed OperationDefinition wrapper, so this composes a <see cref="JsonObject"/>
/// literal directly (the same pattern <c>VersionedFhirStore.BuildCapabilityStatement</c> uses).</summary>
internal static class OperationDefinitionBuilder
{
    /// <summary>Builds the OperationDefinition envelope for <paramref name="op"/>, or null if it has no
    /// canonical registered for <paramref name="fhirVersion"/>.</summary>
    public static ResourceJsonNode? Build(IFhirOperation op, FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion, JsonArray parameters)
    {
        if (!op.CanonicalByFhirVersion.TryGetValue(fhirVersion, out string? url))
        {
            return null;
        }

        var def = new JsonObject
        {
            ["resourceType"] = "OperationDefinition",
            ["id"] = op.OperationName.Substring(1) + "-" + op.OperationVersion.Replace('.', '-'),
            ["name"] = op.OperationName,
            ["url"] = url,
            ["status"] = "draft",
            ["kind"] = op.IsNamedQuery ? "query" : "operation",
            ["affectsState"] = op.AffectsState,
            ["code"] = op.OperationName.Substring(1),
            ["system"] = op.AllowSystemLevel,
            ["type"] = op.AllowResourceLevel,
            ["instance"] = op.AllowInstanceLevel,
            ["resource"] = new JsonArray([.. op.SupportedResources.Select(s => (JsonNode)s)]),
            ["parameter"] = parameters,
        };

        return JsonSourceNodeFactory.Parse((JsonNode)def);
    }

    /// <summary>Builds a single OperationDefinition.parameter JSON literal.</summary>
    public static JsonObject Param(string name, string use, int min, string max, string type, string? documentation = null)
    {
        var p = new JsonObject
        {
            ["name"] = name,
            ["use"] = use,
            ["min"] = min,
            ["max"] = max,
            ["type"] = type,
        };

        if (!string.IsNullOrEmpty(documentation))
        {
            p["documentation"] = documentation;
        }

        return p;
    }
}
