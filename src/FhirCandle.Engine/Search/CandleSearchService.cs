using System.Globalization;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.Search.Definition;
using Ignixa.Search.Expressions;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.InMemory;
using Ignixa.Search.Models;
using Ignixa.Search.Parsing;
using Microsoft.Extensions.Logging;

namespace FhirCandle.Search;

/// <summary>
/// Wraps the Ignixa.Search pipeline (definitions, indexer, expression parser, options builder) behind
/// a single reusable service, and shims the FHIR search modifiers Ignixa does not support natively -
/// token `:in`/`:not-in` (against a ValueSet) and reference `:identifier` - by intercepting them before
/// they reach Ignixa's expression parser (which throws on them) and evaluating them separately in
/// <see cref="TestForMatch"/>.
/// </summary>
public sealed class CandleSearchService
{
    private static readonly QueryParameterParser QueryParser = new();

    private readonly IFhirSchemaProvider _schema;
    private readonly SearchOptionsBuilder _optionsBuilder;
    private readonly ISearchIndexer _indexer;
    private readonly ExpressionParser _expressionParser;

    public CandleSearchService(IFhirSchemaProvider schema, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _schema = schema;
        Definitions = new SearchParameterDefinitionManager(schema, loggerFactory.CreateLogger<SearchParameterDefinitionManager>());

        ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver resolver = () => Definitions;
        _expressionParser = new ExpressionParser(
            resolver,
            new SearchParameterExpressionParser(new ReferenceSearchValueParser(schema), schema),
            schema);

        _optionsBuilder = new SearchOptionsBuilder(_expressionParser, Definitions);
        _indexer = SearchIndexerFactory.CreateInstance(schema, loggerFactory, Definitions);
    }

    public SearchParameterDefinitionManager Definitions { get; }

    /// <summary>
    /// Extracts the FHIRPath-derived search index, plus a synthetic <c>_lastUpdated</c> entry from
    /// <c>Resource.meta.lastUpdated</c>. Ignixa's indexer deliberately does not extract <c>_lastUpdated</c>
    /// (mirroring its SQL datalayer, which bypasses the index tables and reads the intrinsic column
    /// directly) - since this in-memory search has no equivalent direct-column path, and
    /// <see cref="ResourceKey"/> carries no last-updated field for <see cref="CandleSearchQueryInterpreter"/>
    /// to bypass onto (unlike <c>_id</c>, which maps onto <see cref="ResourceKey.Id"/>), a synthetic entry
    /// is added here instead so <c>_lastUpdated</c> flows through the ordinary indexed-date matching path.
    /// </summary>
    public IReadOnlyCollection<SearchIndexEntry> Index(IElement resource)
    {
        List<SearchIndexEntry> entries = [.. _indexer.Extract(resource)];

        if (resource.FirstChild("meta")?.FirstChild("lastUpdated")?.Value is string lastUpdated &&
            DateTimeOffset.TryParse(lastUpdated, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsedLastUpdated) &&
            Definitions.TryGetSearchParameter(resource.InstanceType, "_lastUpdated", out SearchParameterInfo lastUpdatedParameter))
        {
            entries.Add(new SearchIndexEntry(lastUpdatedParameter, new DateTimeSearchValue(parsedLastUpdated)));
        }

        return entries;
    }

    public ParsedQuery ParseQuery(string? resourceType, string queryString)
    {
        IReadOnlyList<QueryParameter> parameters = QueryParser.Parse(queryString);

        string[] resourceTypes = resourceType is null ? ["Resource"] : [resourceType];

        var customFilters = new List<CustomModifierFilter>();
        var chainedExpressions = new List<Expression>();
        var chainedFailures = new List<string>();
        var remainingParameters = new List<QueryParameter>(parameters.Count);

        foreach (QueryParameter parameter in parameters)
        {
            if (TryExtractCustomModifier(parameter.Name, out string code, out string modifier))
            {
                customFilters.Add(new CustomModifierFilter(code, modifier, parameter.Value));
            }
            else if (IsChainedOrHasParameter(parameter.Name))
            {
                try
                {
                    chainedExpressions.Add(_expressionParser.Parse(resourceTypes, parameter.Name, parameter.Value));
                }
                catch (Exception ex) when (ex is SearchParameterNotSupportedException or InvalidSearchOperationException)
                {
                    chainedFailures.Add(parameter.Name);
                }
            }
            else
            {
                remainingParameters.Add(parameter);
            }
        }

        SearchOptions options = _optionsBuilder.Build(resourceType, remainingParameters, _schema);

        return new ParsedQuery
        {
            Options = options,
            CustomFilters = customFilters,
            ChainedExpressions = chainedExpressions,
            UnknownParameters = chainedFailures.Count == 0
                ? options.UnsupportedParams
                : [.. options.UnsupportedParams, .. chainedFailures],
        };
    }

    private static bool IsChainedOrHasParameter(string parameterName) =>
        parameterName.Contains('.', StringComparison.Ordinal) ||
        parameterName.StartsWith("_has:", StringComparison.Ordinal);

    public SearchPredicate CompilePredicate(ParsedQuery query) =>
        query.Options.Expression is null
            ? input => input
            : query.Options.Expression.AcceptVisitor(new CandleSearchQueryInterpreter(), default);

    public bool TestForMatch(
        ResourceKey key,
        IReadOnlyCollection<SearchIndexEntry> index,
        ParsedQuery query,
        IElement resource,
        Func<string?, string?, string?, bool> vsContains)
    {
        SearchPredicate predicate = CompilePredicate(query);
        var corpus = new[] { (key, index) };

        if (!predicate(corpus).Any())
        {
            return false;
        }

        foreach (CustomModifierFilter filter in query.CustomFilters)
        {
            if (!MatchesCustomFilter(query, filter, index, resource, vsContains))
            {
                return false;
            }
        }

        return true;
    }

    public void AddPackageSearchParameters(IReadOnlyCollection<IElement> searchParameters) =>
        Definitions.AddNewSearchParameters(searchParameters);

    public void RemoveSearchParameter(string url) =>
        Definitions.DeleteSearchParameter(url);

    private bool MatchesCustomFilter(
        ParsedQuery query,
        CustomModifierFilter filter,
        IReadOnlyCollection<SearchIndexEntry> index,
        IElement resource,
        Func<string?, string?, string?, bool> vsContains)
    {
        switch (filter.Modifier)
        {
            case "in":
            case "not-in":
                bool anyInValueSet = index
                    .Where(entry => string.Equals(entry.SearchParameter.Code, filter.Code, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Value as TokenSearchValue)
                    .OfType<TokenSearchValue>()
                    .Any(token => vsContains(filter.Value, token.System, token.Code));
                return filter.Modifier == "in" ? anyInValueSet : !anyInValueSet;

            case "identifier":
                return MatchesIdentifier(query.Options.ResourceType, filter, resource);

            default:
                throw new NotSupportedException($"Unsupported custom search modifier '{filter.Modifier}'.");
        }
    }

    private bool MatchesIdentifier(string? resourceType, CustomModifierFilter filter, IElement resource)
    {
        if (resourceType is null || !Definitions.TryGetSearchParameter(resourceType, filter.Code, out SearchParameterInfo param))
        {
            return false;
        }

        foreach (IElement referenceElement in resource.Select(param.Expression))
        {
            IElement? identifier = referenceElement.FirstChild("identifier");
            if (identifier is null) continue;

            string? system = identifier.FirstChild("system")?.Value as string;
            string? value = identifier.FirstChild("value")?.Value as string;
            string token = system is null ? value ?? string.Empty : $"{system}|{value}";

            if (string.Equals(token, filter.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractCustomModifier(string parameterName, out string code, out string modifier)
    {
        int colonIndex = parameterName.IndexOf(':');
        if (colonIndex > 0)
        {
            string candidateModifier = parameterName[(colonIndex + 1)..];
            if (candidateModifier is "in" or "not-in" or "identifier")
            {
                code = parameterName[..colonIndex];
                modifier = candidateModifier;
                return true;
            }
        }

        code = string.Empty;
        modifier = string.Empty;
        return false;
    }
}

public sealed class ParsedQuery
{
    public required SearchOptions Options { get; init; }
    public required IReadOnlyList<CustomModifierFilter> CustomFilters { get; init; }
    public required IReadOnlyList<Expression> ChainedExpressions { get; init; }
    public required IReadOnlyList<string> UnknownParameters { get; init; }
}
