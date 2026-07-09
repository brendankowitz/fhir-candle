// <copyright file="ResourceStore.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using FhirCandle.Models;
using FhirCandle.Search;
using FhirCandle.Serialization;
using Ignixa.Abstractions;
using Ignixa.Search.Indexing;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace FhirCandle.Storage;

/// <summary>
/// A resource store for a single resource type, over <see cref="ResourceJsonNode"/>.
/// </summary>
/// <remarks>
/// Unlike its Firely-era predecessor (<c>ResourceStore&lt;T&gt;</c>), this store does not hold a back-reference
/// to its owning <c>VersionedFhirStore</c> (that type does not exist yet - it is built in a later task). Instead,
/// it exposes the minimal concrete pieces its owner needs to wire up: the <see cref="OnInstanceCreated"/> /
/// <see cref="OnInstanceUpdated"/> / <see cref="OnInstanceDeleted"/> events, and, for post-CRUD side effects
/// whose real implementation does not exist yet (CompartmentDefinition and ValueSet processing),
/// the <see cref="OnCompartmentDefinitionChanged"/> / <see cref="OnCompartmentDefinitionRemoved"/> /
/// <see cref="OnValueSetChanged"/> / <see cref="OnValueSetRemoved"/> events. SearchParameter side effects are
/// wired directly to <see cref="CandleSearchService.Definitions"/>, which already exists.
/// </remarks>
public sealed class ResourceStore : IVersionedResourceStore
{
    /// <summary>A delegate used to validate a Basic-wrapped SubscriptionTopic, SubscriptionTopic, or
    /// Subscription resource before it is stored. Returns false (with an error message) to reject the
    /// request. No subscriber means such resources are stored without validation.</summary>
    public delegate bool SpecialResourceValidator(ResourceJsonNode source, out string? errorMessage);

    private readonly string _resourceName;
    private readonly IFhirSchemaProvider _schema;
    private readonly CandleSearchService _search;
    private readonly Func<string?, string?, string?, bool> _vsContains;

    private readonly bool _resourceHasUrl;
    private readonly bool _resourceHasIdentifier;
    private readonly bool _resourceHasName;

    private readonly ConcurrentDictionary<string, ResourceJsonNode> _resourceStore = new();
    private readonly ConcurrentDictionary<string, IReadOnlyCollection<SearchIndexEntry>> _indexes = new();

    /// <summary>(Immutable) Conformance URL to ID Map.</summary>
    private readonly ConcurrentDictionary<string, string> _conformanceUrlToId = new();

    /// <summary>(Immutable) Identifier (system|value) to ID map.</summary>
    private readonly ConcurrentDictionary<string, string> _identifierToId = new();

#if NET9_0_OR_GREATER
    private readonly Lock _lockObject = new();
#else
    private readonly object _lockObject = new();
#endif

    /// <summary>Occurs when On Instance Created.</summary>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceCreated;

    /// <summary>Occurs when On Instance Updated.</summary>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceUpdated;

    /// <summary>Occurs when On Instance Deleted.</summary>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceDeleted;

    /// <summary>Occurs when a CompartmentDefinition is created or updated. Task 11 (or later) should
    /// subscribe to register it with the compartment engine, which does not exist yet.</summary>
    public event EventHandler<ResourceJsonNode>? OnCompartmentDefinitionChanged;

    /// <summary>Occurs when a CompartmentDefinition is deleted, carrying the compartment type code.</summary>
    public event EventHandler<string>? OnCompartmentDefinitionRemoved;

    /// <summary>Occurs when a ValueSet is created or updated. Task 12 (terminology) should subscribe to
    /// register it with the terminology service, which does not exist yet.</summary>
    public event EventHandler<ResourceJsonNode>? OnValueSetChanged;

    /// <summary>Occurs when a ValueSet is deleted.</summary>
    public event EventHandler<ResourceJsonNode>? OnValueSetRemoved;

    /// <summary>Validates Basic-wrapped SubscriptionTopic and SubscriptionTopic resources. Unset until a
    /// later (Subscriptions) task wires in a real topic converter.</summary>
    public SpecialResourceValidator? SubscriptionTopicValidator { get; set; }

    /// <summary>Validates Subscription resources. Unset until a later (Subscriptions) task wires in a real
    /// subscription converter.</summary>
    public SpecialResourceValidator? SubscriptionValidator { get; set; }

    /// <summary>Initializes a new instance of the <see cref="ResourceStore"/> class.</summary>
    /// <param name="resourceType">Name of the FHIR resource type this store holds.</param>
    /// <param name="schema">      The FHIR schema provider.</param>
    /// <param name="search">      The shared search service.</param>
    /// <param name="vsContains">  Callback used to test ValueSet membership for the <c>:in</c>/<c>:not-in</c>
    ///  search modifiers (see <see cref="CandleSearchService.TestForMatch"/>).</param>
    public ResourceStore(
        string resourceType,
        IFhirSchemaProvider schema,
        CandleSearchService search,
        Func<string?, string?, string?, bool> vsContains)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(vsContains);

        _resourceName = resourceType;
        _schema = schema;
        _search = search;
        _vsContains = vsContains;

        IType? typeDef = schema.GetTypeDefinition(resourceType);
        _resourceHasUrl = typeDef?.Children.Any(c => c.Info.Name == "url") ?? false;
        _resourceHasIdentifier = typeDef?.Children.Any(c => c.Info.Name == "identifier") ?? false;
        _resourceHasName = typeDef?.Children.Any(c => c.Info.Name == "name") ?? false;
    }

    /// <inheritdoc/>
    public bool ResourcesAreConformance => _resourceHasUrl;

    /// <inheritdoc/>
    public bool ResourcesAreIdentifiable => _resourceHasIdentifier;

    /// <inheritdoc/>
    public bool ResourcesHaveName => _resourceHasName;

    /// <inheritdoc/>
    public IQueryable<InstanceTableRec> GetInstanceTableView() =>
        _resourceStore.Select(kvp => new InstanceTableRec()
        {
            Id = kvp.Key,
            Name = _resourceHasName ? GetDisplayName(kvp.Value) : string.Empty,
            Url = _resourceHasUrl ? (GetCanonicalUrl(kvp.Value) ?? string.Empty) : string.Empty,
            Description = GetDescription(kvp.Value),
            Identifiers = _resourceHasIdentifier ? string.Join(", ", GetIdentifierKeys(kvp.Value)) : string.Empty,
        }).AsQueryable();

    /// <inheritdoc/>
    public ResourceJsonNode? GetByCanonical(string url) =>
        TryGetByCanonical(url, out ResourceJsonNode? resource) ? resource : null;

    /// <inheritdoc/>
    public bool TryGetByCanonical(string url, out ResourceJsonNode? resource)
    {
        if (!string.IsNullOrEmpty(url) &&
            _conformanceUrlToId.TryGetValue(url, out string? id) &&
            _resourceStore.TryGetValue(id, out resource))
        {
            return true;
        }

        resource = null;
        return false;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? InstanceRead(string id) =>
        (!string.IsNullOrEmpty(id) && _resourceStore.TryGetValue(id, out ResourceJsonNode? resource))
            ? resource
            : null;

    /// <inheritdoc/>
    public bool TryResolveIdentifier(string? system, string? value, out ResourceJsonNode? resource)
    {
        if (_identifierToId.TryGetValue($"{system}|{value}", out string? id) &&
            _resourceStore.TryGetValue(id, out resource))
        {
            return true;
        }

        resource = null;
        return false;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? InstanceCreate(
        FhirRequestContext ctx,
        ResourceJsonNode source,
        bool allowExistingId,
        out HttpStatusCode statusCode,
        out OperationOutcomeJsonNode outcome)
    {
        if (source is null || source.ResourceType != _resourceName)
        {
            statusCode = HttpStatusCode.BadRequest;
            outcome = SerializationUtils.BuildOutcomeForRequest(statusCode, $"Invalid resource content for {_resourceName}");
            return null;
        }

        if (!allowExistingId || string.IsNullOrEmpty(source.Id))
        {
            source.Id = Guid.NewGuid().ToString();
        }

        if (ValidateSpecialCase(source) is { } validationFailure)
        {
            statusCode = validationFailure.StatusCode;
            outcome = validationFailure.Outcome;
            return null;
        }

        if (_resourceName == "Observation")
        {
            ApplyObservationPatientSuppliedTagging(ctx, source);
        }

        lock (_lockObject)
        {
            if (_resourceStore.ContainsKey(source.Id))
            {
                statusCode = HttpStatusCode.Conflict;
                outcome = SerializationUtils.BuildOutcomeForRequest(
                    statusCode,
                    $"Resource {_resourceName}/{source.Id} already exists; POST-based create interaction cannot overwrite existing resources",
                    OperationOutcomeJsonNode.IssueType.Duplicate);
                return null;
            }

            source.Meta.VersionId = "1";
            source.Meta.LastUpdated = DateTimeOffset.UtcNow;

            if (!_resourceStore.TryAdd(source.Id, source))
            {
                statusCode = HttpStatusCode.InternalServerError;
                outcome = SerializationUtils.BuildOutcomeForRequest(statusCode, $"Failed to create resource {_resourceName}/{source.Id}");
                return null;
            }

            _indexes[source.Id] = _search.Index(source.ToElement(_schema));
        }

        RegisterInstanceCreated(source.Id);

        AddToSecondaryIndexes(source);
        RunPostCrudSideEffects(source, isDelete: false);

        statusCode = HttpStatusCode.Created;
        outcome = SerializationUtils.BuildOutcomeForRequest(statusCode, $"Created {_resourceName}/{source.Id}");
        return source;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? InstanceUpdate(
        ResourceJsonNode source,
        bool allowCreate,
        string ifMatch,
        string ifNoneMatch,
        HashSet<string> protectedResources,
        out HttpStatusCode sc,
        out OperationOutcomeJsonNode outcome)
    {
        if (source is null || source.ResourceType != _resourceName)
        {
            sc = HttpStatusCode.BadRequest;
            outcome = SerializationUtils.BuildOutcomeForRequest(sc, $"Invalid resource content for {_resourceName}");
            return null;
        }

        if (string.IsNullOrEmpty(source.Id))
        {
            sc = HttpStatusCode.BadRequest;
            outcome = SerializationUtils.BuildOutcomeForRequest(sc, "Cannot update resources without an ID");
            return null;
        }

        if (protectedResources.Count > 0 && protectedResources.Contains(_resourceName + "/" + source.Id))
        {
            sc = HttpStatusCode.Unauthorized;
            outcome = SerializationUtils.BuildOutcomeForRequest(sc, $"Resource {_resourceName}/{source.Id} is protected and cannot be changed");
            return null;
        }

        if (ValidateSpecialCase(source) is { } validationFailure)
        {
            sc = validationFailure.StatusCode;
            outcome = validationFailure.Outcome;
            return null;
        }

        ResourceJsonNode? previous;

        lock (_lockObject)
        {
            if (!_resourceStore.TryGetValue(source.Id, out ResourceJsonNode? existing))
            {
                if (!allowCreate)
                {
                    sc = HttpStatusCode.BadRequest;
                    outcome = SerializationUtils.BuildOutcomeForRequest(sc, "Update as Create is disabled");
                    return null;
                }

                source.Meta.VersionId = "1";
                previous = null;
            }
            else
            {
                previous = JsonSourceNodeFactory.Parse((JsonNode)existing.MutableNode.DeepClone());

                source.Meta.VersionId = int.TryParse(previous.Meta.VersionId, out int version)
                    ? (version + 1).ToString()
                    : "1";
            }

            // check preconditions - applies whether or not a prior version exists, matching the
            // ported behavior of the old ResourceStore<T>.InstanceUpdate.
            if (ifNoneMatch.Equals("*", StringComparison.Ordinal))
            {
                sc = HttpStatusCode.PreconditionFailed;
                outcome = SerializationUtils.BuildOutcomeForRequest(sc, "Prior version exists, but If-None-Match is *");
                return null;
            }

            if (!string.IsNullOrEmpty(ifNoneMatch) &&
                ifNoneMatch.Equals($"W/\"{previous?.Meta.VersionId ?? string.Empty}\"", StringComparison.Ordinal))
            {
                sc = HttpStatusCode.PreconditionFailed;
                outcome = SerializationUtils.BuildOutcomeForRequest(
                    sc,
                    $"Conditional update query returned a match with version: {previous?.Meta.VersionId}, If-None-Match: {ifNoneMatch}");
                return null;
            }

            if (!string.IsNullOrEmpty(ifMatch) &&
                !ifMatch.Equals($"W/\"{previous?.Meta.VersionId}\"", StringComparison.Ordinal))
            {
                sc = HttpStatusCode.PreconditionFailed;
                outcome = SerializationUtils.BuildOutcomeForRequest(
                    sc,
                    $"Conditional update query returned a match with version: {previous?.Meta.VersionId}, If-Match: {ifMatch}");
                return null;
            }

            source.Meta.LastUpdated = DateTimeOffset.UtcNow;

            _resourceStore[source.Id] = source;
            _indexes[source.Id] = _search.Index(source.ToElement(_schema));
        }

        RegisterInstanceUpdated(source.Id);

        if (previous is not null)
        {
            RemoveFromSecondaryIndexes(previous);
        }

        AddToSecondaryIndexes(source);
        RunPostCrudSideEffects(source, isDelete: false);

        if (previous is null)
        {
            sc = HttpStatusCode.Created;
            outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, $"Created {_resourceName}/{source.Id} to version {source.Meta.VersionId}");
        }
        else
        {
            sc = HttpStatusCode.OK;
            outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, $"Updated {_resourceName}/{source.Id} to version {source.Meta.VersionId}");
        }

        return source;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? InstanceDelete(string id, HashSet<string> protectedResources)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (protectedResources.Count > 0 && protectedResources.Contains(_resourceName + "/" + id))
        {
            return null;
        }

        ResourceJsonNode? previous;

        lock (_lockObject)
        {
            if (!_resourceStore.TryRemove(id, out previous))
            {
                return null;
            }

            _indexes.TryRemove(id, out _);
        }

        RegisterInstanceDeleted(id);

        RemoveFromSecondaryIndexes(previous);
        RunPostCrudSideEffects(previous, isDelete: true);

        return previous;
    }

    /// <inheritdoc/>
    public IEnumerable<ResourceJsonNode> TypeSearch(ParsedQuery query)
    {
        foreach (KeyValuePair<string, ResourceJsonNode> kvp in _resourceStore)
        {
            if (!_indexes.TryGetValue(kvp.Key, out IReadOnlyCollection<SearchIndexEntry>? index))
            {
                continue;
            }

            IElement element = kvp.Value.ToElement(_schema);
            var key = new ResourceKey(_resourceName, kvp.Key);

            if (_search.TestForMatch(key, index, query, element, _vsContains))
            {
                yield return kvp.Value;
            }
        }
    }

    /// <summary>Registers that an instance has been created.</summary>
    /// <param name="resourceId">Identifier for the resource.</param>
    public void RegisterInstanceCreated(string resourceId) =>
        OnInstanceCreated?.Invoke(this, new() { ResourceType = _resourceName, ResourceId = resourceId });

    /// <summary>Registers that an instance has been updated.</summary>
    /// <param name="resourceId">Identifier for the resource.</param>
    public void RegisterInstanceUpdated(string resourceId) =>
        OnInstanceUpdated?.Invoke(this, new() { ResourceType = _resourceName, ResourceId = resourceId });

    /// <summary>Registers that an instance has been deleted.</summary>
    /// <param name="resourceId">Identifier for the resource.</param>
    public void RegisterInstanceDeleted(string resourceId) =>
        OnInstanceDeleted?.Invoke(this, new() { ResourceType = _resourceName, ResourceId = resourceId });

    private (HttpStatusCode StatusCode, OperationOutcomeJsonNode Outcome)? ValidateSpecialCase(ResourceJsonNode source)
    {
        switch (_resourceName)
        {
            case "Basic":
                {
                    string? basicFhirType = GetBasicFhirType(source);
                    if (basicFhirType == "SubscriptionTopic" &&
                        SubscriptionTopicValidator is not null &&
                        !SubscriptionTopicValidator(source, out string? error))
                    {
                        return Fail(error ?? "Basic-wrapped SubscriptionTopic could not be parsed!");
                    }
                }
                break;

            case "SubscriptionTopic":
                if (SubscriptionTopicValidator is not null &&
                    !SubscriptionTopicValidator(source, out string? topicError))
                {
                    return Fail(topicError ?? "SubscriptionTopic could not be parsed!");
                }
                break;

            case "Subscription":
                if (SubscriptionValidator is not null &&
                    !SubscriptionValidator(source, out string? subscriptionError))
                {
                    return Fail(subscriptionError ?? "Subscription could not be parsed!");
                }
                break;
        }

        return null;

        static (HttpStatusCode, OperationOutcomeJsonNode) Fail(string message) =>
            (HttpStatusCode.BadRequest, SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, message));
    }

    private string? GetBasicFhirType(ResourceJsonNode source) =>
        source.ToElement(_schema).FirstChild("code")?
            .Children("coding")
            .FirstOrDefault(c => string.Equals(c.FirstChild("system")?.Value as string, "http://hl7.org/fhir/fhir-types", StringComparison.Ordinal))?
            .FirstChild("code")?.Value as string;

    /// <summary>
    /// Special-case handling for the Vitals Write Project (https://hackmd.io/jgLf4IF4RNCqtDABAmVrug?view):
    /// tag patient-supplied Observations so downstream consumers can distinguish them.
    /// </summary>
    private void ApplyObservationPatientSuppliedTagging(FhirRequestContext ctx, ResourceJsonNode source)
    {
        IElement element = source.ToElement(_schema);

        bool isPatientLaunch = ctx.Authorization?.UserId.StartsWith("Patient", StringComparison.Ordinal) ?? false;

        string? subjectReference = element.FirstChild("subject")?.FirstChild("reference")?.Value as string;
        bool performerIsSubject = element.Children("performer")
            .Any(p => string.Equals(p.FirstChild("reference")?.Value as string, subjectReference, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(subjectReference));

        if (!isPatientLaunch && !performerIsSubject)
        {
            return;
        }

        AddMetaTagIfMissing(source, "http://hl7.org/fhir/us/core/CodeSystem/us-core-tags", "patient-supplied");
        source.InvalidateCaches();
    }

    private static void AddMetaTagIfMissing(ResourceJsonNode source, string system, string code)
    {
        if (source.MutableNode["meta"] is not JsonObject metaObject)
        {
            metaObject = new JsonObject();
            source.MutableNode["meta"] = metaObject;
        }

        if (metaObject["tag"] is not JsonArray tagArray)
        {
            tagArray = new JsonArray();
            metaObject["tag"] = tagArray;
        }

        bool alreadyTagged = tagArray.Any(t =>
            t is JsonObject tagObject &&
            string.Equals(tagObject["system"]?.GetValue<string>(), system, StringComparison.Ordinal) &&
            string.Equals(tagObject["code"]?.GetValue<string>(), code, StringComparison.Ordinal));

        if (!alreadyTagged)
        {
            tagArray.Add(new JsonObject { ["system"] = system, ["code"] = code });
        }
    }

    private void RunPostCrudSideEffects(ResourceJsonNode resource, bool isDelete)
    {
        switch (_resourceName)
        {
            case "CompartmentDefinition":
                if (isDelete)
                {
                    string? code = resource.ToElement(_schema).FirstChild("code")?.Value as string;
                    if (!string.IsNullOrEmpty(code))
                    {
                        OnCompartmentDefinitionRemoved?.Invoke(this, code);
                    }
                }
                else
                {
                    OnCompartmentDefinitionChanged?.Invoke(this, resource);
                }
                break;

            case "SearchParameter":
                // Best-effort: a malformed SearchParameter should not prevent it from being stored -
                // mirrors the old ResourceStore<T>'s per-target try/catch around SetExecutableSearchParameter.
                try
                {
                    if (isDelete)
                    {
                        string? url = GetCanonicalUrl(resource);
                        if (!string.IsNullOrEmpty(url))
                        {
                            _search.RemoveSearchParameter(url);
                        }
                    }
                    else
                    {
                        _search.AddPackageSearchParameters([resource.ToElement(_schema)]);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ResourceStore[{_resourceName}] <<< Exception processing SearchParameter {resource.Id}: {ex.Message}");
                }
                break;

            case "ValueSet":
                if (isDelete)
                {
                    OnValueSetRemoved?.Invoke(this, resource);
                }
                else
                {
                    OnValueSetChanged?.Invoke(this, resource);
                }
                break;
        }
    }

    private void AddToSecondaryIndexes(ResourceJsonNode resource)
    {
        if (_resourceHasUrl)
        {
            string? url = GetCanonicalUrl(resource);
            if (!string.IsNullOrEmpty(url))
            {
                _conformanceUrlToId.TryAdd(url, resource.Id);
            }
        }

        if (_resourceHasIdentifier)
        {
            foreach (string key in GetIdentifierKeys(resource))
            {
                _identifierToId.TryAdd(key, resource.Id);
            }
        }
    }

    private void RemoveFromSecondaryIndexes(ResourceJsonNode resource)
    {
        if (_resourceHasUrl)
        {
            string? url = GetCanonicalUrl(resource);
            if (!string.IsNullOrEmpty(url))
            {
                _conformanceUrlToId.TryRemove(url, out _);
            }
        }

        if (_resourceHasIdentifier)
        {
            foreach (string key in GetIdentifierKeys(resource))
            {
                _identifierToId.TryRemove(key, out _);
            }
        }
    }

    private string? GetCanonicalUrl(ResourceJsonNode resource) =>
        resource.ToElement(_schema).FirstChild("url")?.Value as string;

    private IEnumerable<string> GetIdentifierKeys(ResourceJsonNode resource) =>
        resource.ToElement(_schema).Children("identifier")
            .Select(identifier => $"{identifier.FirstChild("system")?.Value as string}|{identifier.FirstChild("value")?.Value as string}");

    private string GetDisplayName(ResourceJsonNode resource)
    {
        IElement? name = resource.ToElement(_schema).FirstChild("name");
        if (name is null)
        {
            return string.Empty;
        }

        if (name.Value is string simpleName)
        {
            return simpleName;
        }

        string family = name.FirstChild("family")?.Value as string ?? string.Empty;
        string given = string.Join(' ', name.Children("given").Select(g => g.Value as string ?? string.Empty));

        return string.IsNullOrEmpty(family) && string.IsNullOrEmpty(given)
            ? name.FirstChild("text")?.Value as string ?? string.Empty
            : $"{family}, {given}".Trim(',', ' ');
    }

    private string GetDescription(ResourceJsonNode resource) =>
        resource.ToElement(_schema).FirstChild("description")?.Value as string ?? string.Empty;

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _resourceStore.Keys;

    /// <inheritdoc/>
    public IEnumerable<ResourceJsonNode> Values => _resourceStore.Values;

    /// <inheritdoc/>
    public int Count => _resourceStore.Count;

    /// <inheritdoc/>
    public ResourceJsonNode this[string key] => _resourceStore[key];

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _resourceStore.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out ResourceJsonNode value) => _resourceStore.TryGetValue(key, out value!);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, ResourceJsonNode>> GetEnumerator() => _resourceStore.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    IEnumerable<string> IReadOnlyDictionary<string, object>.Keys => _resourceStore.Keys;

    IEnumerable<object> IReadOnlyDictionary<string, object>.Values => _resourceStore.Values;

    int IReadOnlyCollection<KeyValuePair<string, object>>.Count => _resourceStore.Count;

    object IReadOnlyDictionary<string, object>.this[string key] => _resourceStore[key];

    bool IReadOnlyDictionary<string, object>.ContainsKey(string key) => _resourceStore.ContainsKey(key);

    bool IReadOnlyDictionary<string, object>.TryGetValue(string key, out object value)
    {
        bool result = _resourceStore.TryGetValue(key, out ResourceJsonNode? resource);
        value = resource ?? null!;
        return result;
    }

    IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator() =>
        _resourceStore.Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value)).GetEnumerator();

    /// <inheritdoc/>
    public void Dispose()
    {
        // No unmanaged resources or IDisposable members are owned by this store.
    }
}
