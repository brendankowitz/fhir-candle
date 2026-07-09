using FhirCandle.Storage;
using Ignixa.Abstractions;
using Ignixa.Search.Expressions;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.InMemory;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Search;

/// <summary>
/// Evaluates the search extras Ignixa's <see cref="SearchQueryInterpreter"/> deliberately does not
/// implement (it throws <c>SearchOperationNotSupportedException</c>/<c>NotImplementedException</c> for
/// <c>_include</c>, <c>_revinclude</c>, and chained expressions) - these require walking across stores,
/// which only candle's storage layer, not Ignixa's search library, knows how to do.
/// </summary>
/// <remarks>
/// None of these methods rely on a stored per-resource index: <see cref="ResourceStore"/> (Task 6) does
/// not expose the index it keeps internally, so each method recomputes it on demand via
/// <see cref="CandleSearchService.Index"/>. This is simple and correct but re-extracts the index on every
/// call; if this becomes a hot path, consider adding a store-level index accessor instead.
/// </remarks>
public static class SearchExecutor
{
    /// <summary>
    /// Resolves <c>_include</c> targets: for each match, walks its own reference index entries and
    /// resolves any that satisfy one of the (non-reversed) <paramref name="includes"/> against the
    /// appropriate target store.
    /// </summary>
    public static IEnumerable<ResourceJsonNode> ResolveIncludes(
        IEnumerable<ResourceJsonNode> matches,
        IReadOnlyList<IncludeExpression> includes,
        Func<string, ResourceStore?> storeResolver,
        CandleSearchService search,
        IFhirSchemaProvider schema)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(storeResolver);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(schema);

        if (includes.Count == 0)
        {
            yield break;
        }

        var seen = new HashSet<(string ResourceType, string Id)>();

        foreach (ResourceJsonNode match in matches)
        {
            IReadOnlyCollection<SearchIndexEntry> index = search.Index(match.ToElement(schema));

            foreach (IncludeExpression include in includes)
            {
                if (include.Reversed)
                {
                    continue;
                }

                foreach (SearchIndexEntry entry in index)
                {
                    if (entry.Value is not ReferenceSearchValue reference)
                    {
                        continue;
                    }

                    if (!include.WildCard &&
                        !string.Equals(entry.SearchParameter.Code, include.ReferenceSearchParameter.Code, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (include.TargetResourceType is not null &&
                        !string.Equals(reference.ResourceType, include.TargetResourceType, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!seen.Add((reference.ResourceType, reference.ResourceId)))
                    {
                        continue;
                    }

                    ResourceJsonNode? resolved = storeResolver(reference.ResourceType)?.InstanceRead(reference.ResourceId);
                    if (resolved is not null)
                    {
                        yield return resolved;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves <c>_revinclude</c> targets: for each reversed include, scans the referencing (source)
    /// type's store for resources whose reference index entries point at one of the <paramref name="matches"/>.
    /// </summary>
    public static IEnumerable<ResourceJsonNode> ResolveRevIncludes(
        IEnumerable<ResourceJsonNode> matches,
        IReadOnlyList<IncludeExpression> revIncludes,
        Func<string, ResourceStore?> storeResolver,
        CandleSearchService search,
        IFhirSchemaProvider schema)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(revIncludes);
        ArgumentNullException.ThrowIfNull(storeResolver);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(schema);

        if (revIncludes.Count == 0)
        {
            yield break;
        }

        var matchKeys = matches.Select(m => (m.ResourceType, m.Id)).ToHashSet();
        var seen = new HashSet<(string ResourceType, string Id)>();

        foreach (IncludeExpression revInclude in revIncludes)
        {
            if (!revInclude.Reversed)
            {
                continue;
            }

            ResourceStore? sourceStore = storeResolver(revInclude.SourceResourceType);
            if (sourceStore is null)
            {
                continue;
            }

            foreach (ResourceJsonNode candidate in sourceStore.Values)
            {
                IReadOnlyCollection<SearchIndexEntry> index = search.Index(candidate.ToElement(schema));

                bool referencesMatch = index.Any(entry =>
                    entry.Value is ReferenceSearchValue reference &&
                    string.Equals(entry.SearchParameter.Code, revInclude.ReferenceSearchParameter.Code, StringComparison.Ordinal) &&
                    matchKeys.Contains((reference.ResourceType, reference.ResourceId)));

                if (referencesMatch && seen.Add((candidate.ResourceType, candidate.Id)))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Evaluates a chained expression (forward chain, e.g. <c>subject:Patient.name=Chalmers</c>, or a
    /// reversed <c>_has</c> chain) against a single candidate resource, recursing into nested chains.
    /// </summary>
    public static bool EvaluateChained(
        Expression chainRoot,
        ResourceJsonNode candidate,
        Func<string, ResourceStore?> storeResolver,
        CandleSearchService search,
        IFhirSchemaProvider schema)
    {
        ArgumentNullException.ThrowIfNull(chainRoot);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(storeResolver);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(schema);

        if (chainRoot is not ChainedExpression chained)
        {
            return false;
        }

        return chained.Reversed
            ? EvaluateReversedChain(chained, candidate, storeResolver, search, schema)
            : EvaluateForwardChain(chained, candidate, storeResolver, search, schema);
    }

    /// <summary>
    /// Forward chain: <paramref name="candidate"/> holds the reference (e.g. an Observation);
    /// resolve the referenced resource and test the chain's inner expression against it.
    /// </summary>
    private static bool EvaluateForwardChain(
        ChainedExpression chained,
        ResourceJsonNode candidate,
        Func<string, ResourceStore?> storeResolver,
        CandleSearchService search,
        IFhirSchemaProvider schema)
    {
        IReadOnlyCollection<SearchIndexEntry> index = search.Index(candidate.ToElement(schema));

        foreach (SearchIndexEntry entry in index)
        {
            if (entry.Value is not ReferenceSearchValue reference)
            {
                continue;
            }

            if (!string.Equals(entry.SearchParameter.Code, chained.ReferenceSearchParameter.Code, StringComparison.Ordinal))
            {
                continue;
            }

            if (chained.TargetResourceTypes.Length > 0 &&
                !chained.TargetResourceTypes.Contains(reference.ResourceType, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            ResourceJsonNode? target = storeResolver(reference.ResourceType)?.InstanceRead(reference.ResourceId);
            if (target is not null && MatchesSubExpression(chained.Expression, target, storeResolver, search, schema))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reversed (<c>_has</c>) chain: <paramref name="candidate"/> is the resource being referenced;
    /// scan the source type's store for any resource that both references it and matches the chain's
    /// inner expression.
    /// </summary>
    private static bool EvaluateReversedChain(
        ChainedExpression chained,
        ResourceJsonNode candidate,
        Func<string, ResourceStore?> storeResolver,
        CandleSearchService search,
        IFhirSchemaProvider schema)
    {
        foreach (string sourceResourceType in chained.ResourceTypes)
        {
            ResourceStore? sourceStore = storeResolver(sourceResourceType);
            if (sourceStore is null)
            {
                continue;
            }

            foreach (ResourceJsonNode sourceResource in sourceStore.Values)
            {
                IReadOnlyCollection<SearchIndexEntry> sourceIndex = search.Index(sourceResource.ToElement(schema));

                bool referencesCandidate = sourceIndex.Any(entry =>
                    entry.Value is ReferenceSearchValue reference &&
                    string.Equals(entry.SearchParameter.Code, chained.ReferenceSearchParameter.Code, StringComparison.Ordinal) &&
                    string.Equals(reference.ResourceType, candidate.ResourceType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(reference.ResourceId, candidate.Id, StringComparison.Ordinal));

                if (referencesCandidate && MatchesSubExpression(chained.Expression, sourceResource, storeResolver, search, schema))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Tests a chain's inner expression against a resolved resource - recursing via
    /// <see cref="EvaluateChained"/> for multi-level chains, or compiling a leaf predicate via
    /// <see cref="SearchQueryInterpreter"/> otherwise.
    /// </summary>
    private static bool MatchesSubExpression(
        Expression expression,
        ResourceJsonNode resource,
        Func<string, ResourceStore?> storeResolver,
        CandleSearchService search,
        IFhirSchemaProvider schema)
    {
        if (expression is ChainedExpression nested)
        {
            return EvaluateChained(nested, resource, storeResolver, search, schema);
        }

        SearchPredicate predicate = expression.AcceptVisitor(new SearchQueryInterpreter(), default);
        var key = new ResourceKey(resource.ResourceType, resource.Id);
        var corpus = new[] { (key, search.Index(resource.ToElement(schema))) };
        return predicate(corpus).Any();
    }
}
