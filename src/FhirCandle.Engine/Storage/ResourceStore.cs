// <copyright file="ResourceStore.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using FhirCandle.Models;
using FhirCandle.Search;
using FhirCandle.Serialization;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.Models;
using Ignixa.Search.Indexing;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace FhirCandle.Storage;

/// <summary>Raised when a stored-resource change matches a subscription's topic triggers and
/// filters. The owning store allocates the event number, resolves any additional-context
/// resources, and forwards the event to its public subscription surface.</summary>
public sealed class SubscriptionMatchedEventArgs : EventArgs
{
    /// <summary>Gets or initializes the matched subscription id.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Gets or initializes the canonical URL of the matched topic.</summary>
    public required string TopicUrl { get; init; }

    /// <summary>Gets or initializes the focus resource of the event.</summary>
    public required ResourceJsonNode Focus { get; init; }

    /// <summary>Gets or initializes the topic's notification-shape include/revinclude query, for the
    /// owner to resolve additional-context resources with.</summary>
    public ParsedQuery? AdditionalContext { get; init; }
}

/// <summary>Raised when evaluating a subscription topic trigger throws.</summary>
public sealed class SubscriptionTriggerErrorEventArgs : EventArgs
{
    /// <summary>Gets or initializes the affected subscription id.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Gets or initializes the error message.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// A resource store for a single resource type, over <see cref="ResourceJsonNode"/>.
/// </summary>
/// <remarks>
/// Unlike its earlier predecessor (<c>ResourceStore&lt;T&gt;</c>), this store does not hold a back-reference
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
    private readonly Func<string, IElement?> _resolveElement;

    /// <summary>Executable subscription state, keyed by topic URL. Maintained by the owning store
    /// via <see cref="SetExecutableSubscriptionTopic"/>/<see cref="SetExecutableSubscription"/>.</summary>
    private readonly Dictionary<string, ExecutableSubscriptionInfo> _executableSubscriptions = [];

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

    /// <summary>Occurs when a SubscriptionTopic (or R4 Basic-wrapped topic) is created or updated.</summary>
    public event EventHandler<ResourceJsonNode>? OnSubscriptionTopicChanged;

    /// <summary>Occurs when a SubscriptionTopic (or R4 Basic-wrapped topic) is deleted.</summary>
    public event EventHandler<ResourceJsonNode>? OnSubscriptionTopicRemoved;

    /// <summary>Occurs when a Subscription is created or updated.</summary>
    public event EventHandler<ResourceJsonNode>? OnSubscriptionChanged;

    /// <summary>Occurs when a Subscription is deleted.</summary>
    public event EventHandler<ResourceJsonNode>? OnSubscriptionRemoved;

    /// <summary>Occurs when a resource change matches a subscription (topic triggers plus
    /// subscription filters).</summary>
    /// <summary>Raised after a stored SearchParameter resource has been (un)registered with the
    /// search service - listeners re-derive state that depends on the active definition set
    /// (stored indexes, capability statement).</summary>
    public event EventHandler? OnSearchParametersChanged;

    public event EventHandler<SubscriptionMatchedEventArgs>? OnSubscriptionEventMatched;

    /// <summary>Occurs when a subscription topic trigger fails to evaluate.</summary>
    public event EventHandler<SubscriptionTriggerErrorEventArgs>? OnSubscriptionTriggerError;

    /// <summary>Validates Basic-wrapped SubscriptionTopic and SubscriptionTopic resources. Set by the
    /// owning store to the topic converter's parse check.</summary>
    public SpecialResourceValidator? SubscriptionTopicValidator { get; set; }

    /// <summary>Validates Subscription resources. Set by the owning store to the subscription
    /// converter's parse check.</summary>
    public SpecialResourceValidator? SubscriptionValidator { get; set; }

    /// <summary>Initializes a new instance of the <see cref="ResourceStore"/> class.</summary>
    /// <param name="resourceType">  Name of the FHIR resource type this store holds.</param>
    /// <param name="schema">        The FHIR schema provider.</param>
    /// <param name="search">        The shared search service.</param>
    /// <param name="vsContains">    Callback used to test ValueSet membership for the <c>:in</c>/<c>:not-in</c>
    ///  search modifiers (see <see cref="CandleSearchService.TestForMatch"/>).</param>
    /// <param name="resolveElement">Callback used as the FHIRPath <c>resolve()</c> hook when evaluating
    ///  subscription topic triggers (returns null when the reference cannot be resolved).</param>
    public ResourceStore(
        string resourceType,
        IFhirSchemaProvider schema,
        CandleSearchService search,
        Func<string?, string?, string?, bool> vsContains,
        Func<string, IElement?> resolveElement)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(vsContains);
        ArgumentNullException.ThrowIfNull(resolveElement);

        _resourceName = resourceType;
        _schema = schema;
        _search = search;
        _vsContains = vsContains;
        _resolveElement = resolveElement;

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
        out OperationOutcome outcome)
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
                    $"Resource {_resourceName}/{source.Id} already exists; POST-base create interaction cannot overwrite existing resources",
                    OperationOutcomeIssue.IssueTypeCommon.Duplicate);
                return null;
            }

            source.Meta.VersionId = "1";
            source.Meta.LastUpdatedOffset = DateTimeOffset.UtcNow;

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
        TestCreateAgainstSubscriptions(source);
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
        out OperationOutcome outcome)
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

            source.Meta.LastUpdatedOffset = DateTimeOffset.UtcNow;

            _resourceStore[source.Id] = source;
            _indexes[source.Id] = _search.Index(source.ToElement(_schema));
        }

        RegisterInstanceUpdated(source.Id);

        if (previous is not null)
        {
            RemoveFromSecondaryIndexes(previous);
        }

        AddToSecondaryIndexes(source);

        if (previous is null)
        {
            TestCreateAgainstSubscriptions(source);
        }
        else
        {
            TestUpdateAgainstSubscriptions(source, previous);
        }

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
        TestDeleteAgainstSubscriptions(previous);
        RunPostCrudSideEffects(previous, isDelete: true);

        return previous;
    }

    /// <summary>Recomputes every stored resource's search index - required after the active
    /// SearchParameter definition set changes, since indexes are extracted at write time.</summary>
    public void RebuildIndexes()
    {
        lock (_lockObject)
        {
            foreach ((string id, ResourceJsonNode resource) in _resourceStore)
            {
                _indexes[id] = _search.Index(resource.ToElement(_schema));
            }
        }
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

    /// <summary>Sets (or replaces) the executable trigger definitions for a subscription topic.</summary>
    public void SetExecutableSubscriptionTopic(
        string topicUrl,
        IReadOnlyList<ExecutableSubscriptionInfo.InteractionOnlyTrigger> interactionTriggers,
        IReadOnlyList<ExecutableSubscriptionInfo.FhirPathTrigger> fhirPathTriggers,
        IReadOnlyList<ExecutableSubscriptionInfo.QueryTrigger> queryTriggers,
        ParsedQuery? additionalContext)
    {
        if (!_executableSubscriptions.TryGetValue(topicUrl, out ExecutableSubscriptionInfo? executable))
        {
            executable = new() { TopicUrl = topicUrl };
            _executableSubscriptions[topicUrl] = executable;
        }

        executable.InteractionTriggers = interactionTriggers;
        executable.FhirPathTriggers = fhirPathTriggers;
        executable.QueryTriggers = queryTriggers;
        executable.AdditionalContext = additionalContext;
    }

    /// <summary>Sets (or replaces) a subscription's filters under a topic. A null
    /// <paramref name="filters"/> means the subscription is unfiltered for this resource type.</summary>
    public void SetExecutableSubscription(string topicUrl, string subscriptionId, ParsedQuery? filters)
    {
        if (!_executableSubscriptions.TryGetValue(topicUrl, out ExecutableSubscriptionInfo? executable))
        {
            executable = new() { TopicUrl = topicUrl };
            _executableSubscriptions[topicUrl] = executable;
        }

        executable.FiltersBySubscription[subscriptionId] = filters;
    }

    /// <summary>Removes the executable state for a subscription topic.</summary>
    public void RemoveExecutableSubscriptionTopic(string topicUrl) => _executableSubscriptions.Remove(topicUrl);

    /// <summary>Removes one subscription's filters from under a topic.</summary>
    public void RemoveExecutableSubscription(string topicUrl, string subscriptionId)
    {
        if (_executableSubscriptions.TryGetValue(topicUrl, out ExecutableSubscriptionInfo? executable))
        {
            executable.FiltersBySubscription.Remove(subscriptionId);
        }
    }

    /// <summary>Tests a create interaction against all executable subscriptions.</summary>
    public void TestCreateAgainstSubscriptions(ResourceJsonNode current)
    {
        if (_executableSubscriptions.Count == 0)
        {
            return;
        }

        try
        {
            IElement currentElement = current.ToElement(_schema);

            EvaluationContext fpContext = new FhirEvaluationContext
            {
                Resource = currentElement,
                RootResource = currentElement,
                ElementResolver = _resolveElement,
            }
                .WithEnvironmentVariable("current", currentElement)
                .WithEnvironmentVariable("previous", Array.Empty<IElement>());

            PerformSubscriptionTest(current, currentElement, null, null, fpContext, ExecutableSubscriptionInfo.InteractionTypes.Create);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ResourceStore[{_resourceName}] <<< TestCreateAgainstSubscriptions caught: {ex.Message}");
        }
    }

    /// <summary>Tests an update interaction against all executable subscriptions.</summary>
    public void TestUpdateAgainstSubscriptions(ResourceJsonNode current, ResourceJsonNode previous)
    {
        if (_executableSubscriptions.Count == 0)
        {
            return;
        }

        try
        {
            IElement currentElement = current.ToElement(_schema);
            IElement previousElement = previous.ToElement(_schema);

            EvaluationContext fpContext = new FhirEvaluationContext
            {
                Resource = currentElement,
                RootResource = currentElement,
                ElementResolver = _resolveElement,
            }
                .WithEnvironmentVariable("current", currentElement)
                .WithEnvironmentVariable("previous", previousElement);

            PerformSubscriptionTest(current, currentElement, previous, previousElement, fpContext, ExecutableSubscriptionInfo.InteractionTypes.Update);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ResourceStore[{_resourceName}] <<< TestUpdateAgainstSubscriptions caught: {ex.Message}");
        }
    }

    /// <summary>Tests a delete interaction against all executable subscriptions.</summary>
    public void TestDeleteAgainstSubscriptions(ResourceJsonNode previous)
    {
        if (_executableSubscriptions.Count == 0)
        {
            return;
        }

        try
        {
            IElement previousElement = previous.ToElement(_schema);

            EvaluationContext fpContext = new FhirEvaluationContext
            {
                Resource = previousElement,
                RootResource = previousElement,
                ElementResolver = _resolveElement,
            }
                .WithEnvironmentVariable("current", Array.Empty<IElement>())
                .WithEnvironmentVariable("previous", previousElement);

            PerformSubscriptionTest(null, null, previous, previousElement, fpContext, ExecutableSubscriptionInfo.InteractionTypes.Delete);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ResourceStore[{_resourceName}] <<< TestDeleteAgainstSubscriptions caught: {ex.Message}");
        }
    }

    /// <summary>Evaluates every executable topic's triggers against a change, then every matched
    /// topic's per-subscription filters, raising <see cref="OnSubscriptionEventMatched"/> once per
    /// matched subscription and <see cref="OnSubscriptionTriggerError"/> for evaluation failures.</summary>
    private void PerformSubscriptionTest(
        ResourceJsonNode? current,
        IElement? currentElement,
        ResourceJsonNode? previous,
        IElement? previousElement,
        EvaluationContext fpContext,
        ExecutableSubscriptionInfo.InteractionTypes interaction)
    {
        switch (interaction)
        {
            case ExecutableSubscriptionInfo.InteractionTypes.Create when currentElement is null:
            case ExecutableSubscriptionInfo.InteractionTypes.Update when currentElement is null || previousElement is null:
            case ExecutableSubscriptionInfo.InteractionTypes.Delete when previousElement is null:
                return;
        }

        var matchedTopics = new List<string>();
        var topicErrors = new Dictionary<string, List<string>>();

        // computed lazily - only query triggers and subscription filters need indexes
        IReadOnlyCollection<SearchIndexEntry>? currentIndex = null;
        IReadOnlyCollection<SearchIndexEntry>? previousIndex = null;

        IReadOnlyCollection<SearchIndexEntry> CurrentIndex() => currentIndex ??= _search.Index(currentElement!);
        IReadOnlyCollection<SearchIndexEntry> PreviousIndex() => previousIndex ??= _search.Index(previousElement!);

        bool TestQuery(ParsedQuery? query, ResourceJsonNode resource, IElement element, Func<IReadOnlyCollection<SearchIndexEntry>> index) =>
            query is null ||
            _search.TestForMatch(new ResourceKey(_resourceName, resource.Id), index(), query, element, _vsContains);

        void RecordError(string topicUrl, string kind, Exception ex)
        {
            Console.WriteLine($"ResourceStore[{_resourceName}] <<< Error evaluating {kind} trigger for topic {topicUrl}: {ex.Message}");

            if (!topicErrors.TryGetValue(topicUrl, out List<string>? errors))
            {
                errors = [];
                topicErrors[topicUrl] = errors;
            }

            errors.Add(ex.InnerException is null
                ? $"Error while evaluating {kind} trigger for topic {topicUrl} on resource {_resourceName}: {ex.Message}"
                : $"Error while evaluating {kind} trigger for topic {topicUrl} on resource {_resourceName}: {ex.Message}:{ex.InnerException.Message}");
        }

        foreach ((string topicUrl, ExecutableSubscriptionInfo executable) in _executableSubscriptions)
        {
            // first, interaction-only triggers
            bool matched = interaction switch
            {
                ExecutableSubscriptionInfo.InteractionTypes.Create => executable.InteractionTriggers.Any(t => t.OnCreate),
                ExecutableSubscriptionInfo.InteractionTypes.Update => executable.InteractionTriggers.Any(t => t.OnUpdate),
                ExecutableSubscriptionInfo.InteractionTypes.Delete => executable.InteractionTriggers.Any(t => t.OnDelete),
                _ => false,
            };

            // second, FHIRPath triggers (evaluated against the focus with %current/%previous bound)
            if (!matched)
            {
                foreach (ExecutableSubscriptionInfo.FhirPathTrigger trigger in executable.FhirPathTriggers)
                {
                    try
                    {
                        IElement? focus = currentElement ?? previousElement;

                        if (focus is not null && focus.IsTrue(trigger.Expression, fpContext))
                        {
                            matched = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordError(topicUrl, "FhirPath", ex);
                    }
                }
            }

            // finally, query triggers against pre/post index snapshots
            if (!matched)
            {
                foreach (ExecutableSubscriptionInfo.QueryTrigger trigger in executable.QueryTriggers)
                {
                    bool previousPassed;
                    bool currentPassed;

                    try
                    {
                        switch (interaction)
                        {
                            case ExecutableSubscriptionInfo.InteractionTypes.Create when trigger.OnCreate:
                                previousPassed = trigger.CreateAutoPasses;
                                currentPassed = TestQuery(trigger.CurrentTest, current!, currentElement!, CurrentIndex);
                                break;

                            case ExecutableSubscriptionInfo.InteractionTypes.Update when trigger.OnUpdate:
                                previousPassed = TestQuery(trigger.PreviousTest, previous!, previousElement!, PreviousIndex);
                                currentPassed = TestQuery(trigger.CurrentTest, current!, currentElement!, CurrentIndex);
                                break;

                            case ExecutableSubscriptionInfo.InteractionTypes.Delete when trigger.OnDelete:
                                previousPassed = TestQuery(trigger.PreviousTest, previous!, previousElement!, PreviousIndex);
                                currentPassed = trigger.DeleteAutoPasses;
                                break;

                            default:
                                continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordError(topicUrl, "Query", ex);
                        continue;
                    }

                    if (trigger.RequireBothTests ? previousPassed && currentPassed : previousPassed || currentPassed)
                    {
                        matched = true;
                        break;
                    }
                }
            }

            if (matched)
            {
                matchedTopics.Add(topicUrl);
            }
        }

        ResourceJsonNode focusResource = current ?? previous!;
        IElement focusElement = currentElement ?? previousElement!;
        Func<IReadOnlyCollection<SearchIndexEntry>> focusIndex = currentElement is not null ? CurrentIndex : PreviousIndex;

        var notifiedSubscriptions = new HashSet<string>();

        foreach (string topicUrl in matchedTopics)
        {
            ExecutableSubscriptionInfo executable = _executableSubscriptions[topicUrl];

            foreach ((string subscriptionId, ParsedQuery? filters) in executable.FiltersBySubscription)
            {
                if (!notifiedSubscriptions.Add(subscriptionId))
                {
                    continue;
                }

                bool filtersPass;
                try
                {
                    filtersPass = TestQuery(filters, focusResource, focusElement, focusIndex);
                }
                catch (Exception ex)
                {
                    RecordError(topicUrl, "filter", ex);
                    notifiedSubscriptions.Remove(subscriptionId);
                    continue;
                }

                if (!filtersPass)
                {
                    notifiedSubscriptions.Remove(subscriptionId);
                    continue;
                }

                OnSubscriptionEventMatched?.Invoke(this, new()
                {
                    SubscriptionId = subscriptionId,
                    TopicUrl = topicUrl,
                    Focus = focusResource,
                    AdditionalContext = executable.AdditionalContext,
                });
            }
        }

        foreach ((string topicUrl, List<string> errors) in topicErrors)
        {
            if (!_executableSubscriptions.TryGetValue(topicUrl, out ExecutableSubscriptionInfo? executable))
            {
                continue;
            }

            foreach (string subscriptionId in executable.FiltersBySubscription.Keys)
            {
                foreach (string error in errors)
                {
                    OnSubscriptionTriggerError?.Invoke(this, new() { SubscriptionId = subscriptionId, Message = error });
                }
            }
        }
    }

    private (HttpStatusCode StatusCode, OperationOutcome Outcome)? ValidateSpecialCase(ResourceJsonNode source)
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

        static (HttpStatusCode, OperationOutcome) Fail(string message) =>
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

                    OnSearchParametersChanged?.Invoke(this, EventArgs.Empty);
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

            case "SubscriptionTopic":
                if (isDelete)
                {
                    OnSubscriptionTopicRemoved?.Invoke(this, resource);
                }
                else
                {
                    OnSubscriptionTopicChanged?.Invoke(this, resource);
                }
                break;

            case "Basic":
                if (GetBasicFhirType(resource) == "SubscriptionTopic")
                {
                    if (isDelete)
                    {
                        OnSubscriptionTopicRemoved?.Invoke(this, resource);
                    }
                    else
                    {
                        OnSubscriptionTopicChanged?.Invoke(this, resource);
                    }
                }
                break;

            case "Subscription":
                if (isDelete)
                {
                    OnSubscriptionRemoved?.Invoke(this, resource);
                }
                else
                {
                    OnSubscriptionChanged?.Invoke(this, resource);
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
