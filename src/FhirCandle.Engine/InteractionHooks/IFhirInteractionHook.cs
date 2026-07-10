// <copyright file="IFhirInteractionHook.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using FhirCandle.Models;
using FhirCandle.Storage;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.InteractionHooks;

/// <summary>Interface for FHIR interaction hooks, ported to operate over <see cref="ResourceJsonNode"/>
/// instead of Firely POCOs. Hooks run inside CRUD dispatch (see the Post-hook invocation in
/// <see cref="VersionedFhirStore"/>'s create/update paths) and may inspect or replace the resource,
/// perform additional store interactions, or complete the request themselves by setting a
/// status code on their response.</summary>
public interface IFhirInteractionHook
{
    /// <summary>Gets the name of the hook - used for logging.</summary>
    string Name { get; }

    /// <summary>Gets the identifier of the hook - MUST be UNIQUE.</summary>
    string Id { get; }

    /// <summary>Gets the supported FHIR versions. An empty set means all versions.</summary>
    HashSet<FhirCandle.Utils.FhirReleases.FhirSequenceCodes> SupportedFhirVersions { get; }

    /// <summary>
    /// If this hook requires a specific FHIR package to be loaded, the package identifier.
    /// </summary>
    string RequiresPackage { get; }

    /// <summary>Gets the interactions by resource.</summary>
    Dictionary<string, HashSet<Common.StoreInteractionCodes>> InteractionsByResource { get; }

    /// <summary>Gets a list of states of the hook requests.</summary>
    HashSet<Common.HookRequestStateCodes> HookRequestStates { get; }

    /// <summary>Gets or sets a value indicating whether this object is enabled.</summary>
    bool Enabled { get; set; }

    /// <summary>Executes the interaction hook operation.</summary>
    /// <param name="ctx">          The context.</param>
    /// <param name="store">        The store.</param>
    /// <param name="resourceStore">The resource store.</param>
    /// <param name="resource">     The resource.</param>
    /// <param name="hookResponse"> [out] Hook response information.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    /// <remarks>If the hook wishes to stop further processing, it should set a hookResponse.StatusCode.</remarks>
    bool DoInteractionHook(
        FhirRequestContext ctx,
        VersionedFhirStore store,
        IVersionedResourceStore? resourceStore,
        ResourceJsonNode? resource,
        out FhirResponseContext hookResponse);
}
