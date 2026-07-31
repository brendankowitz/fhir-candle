using System.Globalization;
using System.Reflection;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.Search.Definition;
using Ignixa.Search.Exceptions;
using Ignixa.Search.Expressions;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.Converters;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.InMemory;
using Ignixa.Search.Models;
using Ignixa.Search.Parsing;
using Ignixa.Specification.ValueSets.Normative;
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
    private readonly ReferenceSearchValueParser _referenceParser;

    public CandleSearchService(IFhirSchemaProvider schema, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _schema = schema;
        Definitions = new SearchParameterDefinitionManager(schema, loggerFactory.CreateLogger<SearchParameterDefinitionManager>());

        _referenceParser = new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance);

        ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver resolver = () => Definitions;
        _expressionParser = new ExpressionParser(
            resolver,
            new CandleSearchParameterExpressionParser(_referenceParser, schema),
            schema);

        _optionsBuilder = new SearchOptionsBuilder(_expressionParser, Definitions);
        _indexer = CreateIndexer(schema, loggerFactory, Definitions, _referenceParser);
    }

    public SearchParameterDefinitionManager Definitions { get; }

    /// <summary>
    /// Mirrors <see cref="SearchIndexerFactory.CreateInstance"/>'s reflection scan over Ignixa's
    /// converter types, substituting the ContactPoint and Quantity converters with candle versions
    /// (see <see cref="CandleContactPointToTokenSearchValueConverter"/> /
    /// <see cref="CandleQuantityToQuantitySearchValueConverter"/> for why) - the factory offers no
    /// converter-customization hook.
    /// </summary>
    private static ISearchIndexer CreateIndexer(
        IFhirSchemaProvider schema,
        ILoggerFactory loggerFactory,
        SearchParameterDefinitionManager definitions,
        ReferenceSearchValueParser referenceParser)
    {
        var elementResolver = new LightweightReferenceToElementResolver(referenceParser, schema);
        var codeSystems = new CodeSystemResolver(schema.Version);
        object[] wellKnownArguments = [schema, referenceParser, elementResolver, codeSystems, schema.Version];

        List<IElementToSearchValueConverter> converters = typeof(ElementSearchIndexer)
            .Assembly
            .ExportedTypes
            .Where(x => typeof(IElementToSearchValueConverter).IsAssignableFrom(x) && !x.IsAbstract && !x.IsGenericType)
            .Where(x => x != typeof(ContactPointToTokenSearchValueConverter) && x != typeof(QuantityToQuantitySearchValueConverter))
            .Select(x => (IElementToSearchValueConverter)CreateWithArguments(x, wellKnownArguments))
            .ToList();

        converters.Add(new CandleContactPointToTokenSearchValueConverter());
        converters.Add(new CandleQuantityToQuantitySearchValueConverter());

        return new ElementSearchIndexer(
            new SupportedSearchParameterDefinitionManager(definitions),
            new FhirElementToSearchValueConverterManager(converters.ToArray()),
            elementResolver,
            loggerFactory.CreateLogger<ElementSearchIndexer>());
    }

    private static object CreateWithArguments(Type type, object[] availableArguments)
    {
        ConstructorInfo constructor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();

        object[] arguments = [.. constructor.GetParameters()
            .Select(parameter => availableArguments.FirstOrDefault(a => parameter.ParameterType.IsAssignableFrom(a.GetType()))
                ?? throw new InvalidOperationException($"Unable to find a constructor argument of type {parameter.ParameterType} for {type}."))];

        return constructor.Invoke(arguments);
    }

    /// <summary>
    /// Extracts the FHIRPath-derived search index, plus a synthetic <c>_lastUpdated</c> entry from
    /// <c>Resource.meta.lastUpdated</c>. Ignixa's indexer deliberately does not extract <c>_lastUpdated</c>
    /// (mirroring its SQL datalayer, which bypasses the index tables and reads the intrinsic column
    /// directly) - since this in-memory search has no equivalent direct-column path, and
    /// <see cref="ResourceKey"/> carries no last-updated field for <see cref="CandleSearchQueryInterpreter"/>
    /// to bypass onto (unlike <c>_id</c>, which maps onto <see cref="ResourceKey.Id"/>), a synthetic entry
    /// is added here instead so <c>_lastUpdated</c> flows through the ordinary indexed-date matching path.
    /// A synthetic <c>_profile</c> entry is likewise added when the version defines <c>_profile</c> as a
    /// reference parameter (R5) - Ignixa has no canonical-to-reference converter, so the indexer
    /// silently extracts nothing for it.
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

        if (!entries.Any(e => e.SearchParameter.Code == "_profile") &&
            Definitions.TryGetSearchParameter(resource.InstanceType, "_profile", out SearchParameterInfo profileParameter) &&
            profileParameter.Type == SearchParamType.Reference)
        {
            foreach (IElement profile in resource.Select("meta.profile"))
            {
                if (profile.Value is string profileUrl && !string.IsNullOrEmpty(profileUrl))
                {
                    entries.Add(new SearchIndexEntry(profileParameter, _referenceParser.Parse(profileUrl)));
                }
            }
        }

        return entries;
    }

    public ParsedQuery ParseQuery(string? resourceType, string queryString)
    {
        IReadOnlyList<QueryParameter> parameters = QueryParser.Parse(queryString);

        string[] resourceTypes = resourceType is null ? ["Resource"] : [resourceType];

        var customFilters = new List<CustomModifierFilter>();
        var chainedGroups = new List<IReadOnlyList<Expression>>();
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
                if (TryParseChained(resourceTypes, parameter.Name, parameter.Value, out IReadOnlyList<Expression> alternatives))
                {
                    chainedGroups.Add(alternatives);
                }
                else
                {
                    chainedFailures.Add(parameter.Name);
                }
            }
            else
            {
                remainingParameters.Add(parameter);
            }
        }

        // The options builder aborts on the first value that fails its type-specific parse (bad
        // date, malformed quantity, ...); collect every offender by re-building without it, so
        // strict handling can report them all and lenient handling can drop exactly those.
        var malformedParameters = new List<(string Name, string Message)>();
        List<QueryParameter> effectiveParameters = remainingParameters;
        SearchOptions options;

        while (true)
        {
            try
            {
                options = _optionsBuilder.Build(resourceType, effectiveParameters, _schema);
                break;
            }
            catch (CandleMalformedSearchParameterException ex)
            {
                List<QueryParameter> offenders = effectiveParameters
                    .Where(p => ParameterCode(p.Name).Equals(ex.ParameterCode, StringComparison.Ordinal))
                    .ToList();

                if (offenders.Count == 0)
                {
                    throw;
                }

                malformedParameters.AddRange(offenders.Select(o => (o.Name, ex.Message)));
                effectiveParameters = [.. effectiveParameters.Except(offenders)];
            }
        }

        return new ParsedQuery
        {
            Options = options,
            CustomFilters = customFilters,
            ChainedExpressions = chainedGroups,
            UnknownParameters = chainedFailures.Count == 0
                ? options.UnsupportedParams
                : [.. options.UnsupportedParams, .. chainedFailures],
            MalformedParameters = malformedParameters,
        };
    }

    private static string ParameterCode(string parameterName)
    {
        int colonIndex = parameterName.IndexOf(':');
        return colonIndex < 0 ? parameterName : parameterName[..colonIndex];
    }

    private bool TryParseChained(string[] resourceTypes, string name, string value, out IReadOnlyList<Expression> alternatives)
    {
        try
        {
            alternatives = [_expressionParser.Parse(resourceTypes, name, value)];
            return true;
        }
        catch (Exception ex) when (ex is SearchParameterNotSupportedException or InvalidSearchOperationException)
        {
            // Ignixa refuses a forward chain whose tail parameter more than one target type
            // supports ("chained parameter is ambiguous, specify subject:Patient..."); the
            // pre-migration engine evaluated the chain against every possible target, so expand
            // per target here - the candidate matches when ANY per-target chain matches.
            alternatives = ExpandChainTargets(resourceTypes, name, value);
            return alternatives.Count > 0;
        }
    }

    private IReadOnlyList<Expression> ExpandChainTargets(string[] resourceTypes, string name, string value)
    {
        if (name.StartsWith("_has:", StringComparison.Ordinal))
        {
            return [];
        }

        int dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return [];
        }

        string referenceCode = name[..dot];
        string rest = name[(dot + 1)..];

        if (referenceCode.Contains(':', StringComparison.Ordinal) ||
            !Definitions.TryGetSearchParameter(resourceTypes[0], referenceCode, out SearchParameterInfo referenceParameter) ||
            referenceParameter.Type != SearchParamType.Reference)
        {
            return [];
        }

        var alternatives = new List<Expression>();
        foreach (string target in referenceParameter.TargetResourceTypes ?? [])
        {
            try
            {
                alternatives.Add(_expressionParser.Parse(resourceTypes, $"{referenceCode}:{target}.{rest}", value));
            }
            catch (Exception ex) when (ex is SearchParameterNotSupportedException or InvalidSearchOperationException)
            {
            }
        }

        return alternatives;
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

    /// <summary>Registers (or replaces) SearchParameter definitions at runtime. Two Ignixa quirks
    /// require pre-deleting entries: its definition builder re-injects a synthetic <c>_type</c>
    /// parameter on every call and crashes with a duplicate-key error while the generated base
    /// <c>_type</c> (which has a different expression, and SearchParameterInfo's GetHashCode -
    /// unlike its Equals - includes the expression) is still registered; and it rejects any URL
    /// that is already registered, which would make re-registering an updated SearchParameter
    /// resource fail.</summary>
    public void AddPackageSearchParameters(IReadOnlyCollection<IElement> searchParameters)
    {
        TryDeleteSearchParameter(SearchParameterNames.ResourceTypeUri.ToString());

        foreach (IElement searchParameter in searchParameters)
        {
            if (searchParameter.FirstChild("url")?.Value is string url && !string.IsNullOrEmpty(url))
            {
                TryDeleteSearchParameter(url);
            }
        }

        Definitions.AddNewSearchParameters(searchParameters);
    }

    public void RemoveSearchParameter(string url) =>
        Definitions.DeleteSearchParameter(url);

    private void TryDeleteSearchParameter(string url)
    {
        try
        {
            Definitions.DeleteSearchParameter(url, calculateHash: false);
        }
        catch (BadSearchRequestException)
        {
        }
    }

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

        // token semantics per the pre-migration evaluator: bare value or `|value` match the
        // identifier value alone, `system|` matches the system alone, all case-insensitively
        int separatorIndex = filter.Value.IndexOf('|', StringComparison.Ordinal);
        string? filterSystem = separatorIndex < 0 ? null : filter.Value[..separatorIndex];
        string filterValue = separatorIndex < 0 ? filter.Value : filter.Value[(separatorIndex + 1)..];

        foreach (IElement referenceElement in resource.Select(param.Expression))
        {
            IElement? identifier = referenceElement.FirstChild("identifier");
            if (identifier is null) continue;

            string? system = identifier.FirstChild("system")?.Value as string;
            string? value = identifier.FirstChild("value")?.Value as string;

            bool systemMatches = string.IsNullOrEmpty(filterSystem) ||
                filterSystem.Equals(system, StringComparison.OrdinalIgnoreCase);
            bool valueMatches = string.IsNullOrEmpty(filterValue) ||
                filterValue.Equals(value, StringComparison.OrdinalIgnoreCase);

            if (systemMatches && valueMatches)
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

    /// <summary>Chained/<c>_has</c> expressions, one group per query parameter: every group must
    /// be satisfied, and a group is satisfied when any of its alternatives (one per possible
    /// chain target type) matches.</summary>
    public required IReadOnlyList<IReadOnlyList<Expression>> ChainedExpressions { get; init; }

    public required IReadOnlyList<string> UnknownParameters { get; init; }

    /// <summary>Parameters whose value failed type-specific parsing; already excluded from
    /// <see cref="Options"/>. Strict handling reports them as a 400, lenient handling drops them
    /// from the executed search and its self link.</summary>
    public IReadOnlyList<(string Name, string Message)> MalformedParameters { get; init; } = [];
}
