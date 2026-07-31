// <copyright file="IVersionedResourceStore.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using FhirCandle.Models;
using FhirCandle.Search;
using Ignixa.Models;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using System.Net;

namespace FhirCandle.Storage;

/// <summary>Interface for a versioned resource store over <see cref="ResourceJsonNode"/>.</summary>
public interface IVersionedResourceStore : IResourceStore, IDisposable, IReadOnlyDictionary<string, ResourceJsonNode>
{
    /// <summary>Reads a specific instance of a resource.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>The requested resource or null.</returns>
    ResourceJsonNode? InstanceRead(string id);

    /// <summary>Gets by canonical.</summary>
    /// <param name="url">URL of the resource.</param>
    /// <returns>The by canonical.</returns>
    ResourceJsonNode? GetByCanonical(string url);

    /// <summary>Attempts to get by canonical a resource from the given string.</summary>
    /// <param name="url">     URL of the resource.</param>
    /// <param name="resource">[out] The resource.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    bool TryGetByCanonical(string url, out ResourceJsonNode? resource);

    /// <summary>Create an instance of a resource.</summary>
    /// <param name="ctx">            The context.</param>
    /// <param name="source">         The resource.</param>
    /// <param name="allowExistingId">True to allow, false to suppress the existing identifier.</param>
    /// <param name="statusCode">     [out] The status code.</param>
    /// <param name="outcome">        [out] The outcome.</param>
    /// <returns>The created resource, or null if it could not be created.</returns>
    ResourceJsonNode? InstanceCreate(
        FhirRequestContext ctx,
        ResourceJsonNode source,
        bool allowExistingId,
        out HttpStatusCode statusCode,
        out OperationOutcome outcome);

    /// <summary>Update a specific instance of a resource.</summary>
    /// <param name="source">            The resource.</param>
    /// <param name="allowCreate">       True to allow, false to suppress the create.</param>
    /// <param name="ifMatch">           A match specifying if.</param>
    /// <param name="ifNoneMatch">       A match specifying if none.</param>
    /// <param name="protectedResources">The protected resources.</param>
    /// <param name="sc">                [out] The status code.</param>
    /// <param name="outcome">           [out] The outcome.</param>
    /// <returns>The updated resource, or null if it could not be performed.</returns>
    ResourceJsonNode? InstanceUpdate(
        ResourceJsonNode source,
        bool allowCreate,
        string ifMatch,
        string ifNoneMatch,
        HashSet<string> protectedResources,
        out HttpStatusCode sc,
        out OperationOutcome outcome);

    /// <summary>Instance delete.</summary>
    /// <param name="id">                The identifier.</param>
    /// <param name="protectedResources">The protected resources.</param>
    /// <returns>The deleted resource or null.</returns>
    ResourceJsonNode? InstanceDelete(
        string id,
        HashSet<string> protectedResources);

    /// <summary>Performs a type search in this resource store.</summary>
    /// <param name="query">The parsed search query.</param>
    /// <returns>
    /// An enumerator that allows foreach to be used to process the search results in this collection.
    /// </returns>
    IEnumerable<ResourceJsonNode> TypeSearch(ParsedQuery query);

    /// <summary>Query if this type contains a resource with the specified identifier.</summary>
    /// <param name="system">  The system.</param>
    /// <param name="value">   The value.</param>
    /// <param name="resource">[out] The resolved resource.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    bool TryResolveIdentifier(string? system, string? value, out ResourceJsonNode? resource);
}
