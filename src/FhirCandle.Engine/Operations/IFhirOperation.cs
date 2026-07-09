// <copyright file="IFhirOperation.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using FhirCandle.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Operations;

/// <summary>Interface for executable FHIR operations, ported to operate over <see cref="ResourceJsonNode"/>
/// instead of Firely POCOs.</summary>
public interface IFhirOperation
{
    /// <summary>Gets the name of the operation.</summary>
    string OperationName { get; }

    /// <summary>Gets the operation version.</summary>
    string OperationVersion { get; }

    /// <summary>Gets the canonical by FHIR version.</summary>
    Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion { get; }

    /// <summary>Gets a value indicating whether this object is named query.</summary>
    bool IsNamedQuery { get; }

    /// <summary>Gets a value indicating whether this operation affects the state of the store.</summary>
    bool AffectsState { get; }

    /// <summary>Gets a value indicating whether we allow get.</summary>
    bool AllowGet { get; }

    /// <summary>Gets a value indicating whether we allow post.</summary>
    bool AllowPost { get; }

    /// <summary>Gets a value indicating whether we allow system level.</summary>
    bool AllowSystemLevel { get; }

    /// <summary>Gets a value indicating whether we allow resource level.</summary>
    bool AllowResourceLevel { get; }

    /// <summary>Gets a value indicating whether we allow instance level.</summary>
    bool AllowInstanceLevel { get; }

    /// <summary>Gets a value indicating whether we can accept non-FHIR formats.</summary>
    bool AcceptsNonFhir { get; }

    /// <summary>Gets a value indicating whether we can return non-FHIR formats.</summary>
    bool ReturnsNonFhir { get; }

    /// <summary>If this operation requires a specific FHIR package to be loaded, the package identifier.</summary>
    string RequiresPackage { get; }

    /// <summary>Gets the supported resources.</summary>
    HashSet<string> SupportedResources { get; }

    /// <summary>Executes the FHIR operation.</summary>
    /// <param name="ctx">          The request context.</param>
    /// <param name="store">        The store.</param>
    /// <param name="resourceStore">The resource store (type/instance-level invocations only).</param>
    /// <param name="focusResource">The focus resource (instance-level invocations only).</param>
    /// <param name="bodyResource"> The body resource.</param>
    /// <param name="response">     [out] The response resource.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    bool DoOperation(
        FhirRequestContext ctx,
        Storage.VersionedFhirStore store,
        Storage.IVersionedResourceStore? resourceStore,
        ResourceJsonNode? focusResource,
        ResourceJsonNode? bodyResource,
        out FhirResponseContext response);

    /// <summary>Gets an OperationDefinition resource describing this operation, built as a JSON literal.</summary>
    /// <param name="fhirVersion">The FHIR version.</param>
    /// <returns>The definition, or null if this operation has no canonical for the given version.</returns>
    ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion);
}
