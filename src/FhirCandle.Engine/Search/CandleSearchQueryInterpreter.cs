// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------
//
// Forked and extended from Ignixa.Search.InMemory.SearchQueryInterpreter (itself ported from
// microsoft/fhir-server). Differences from the original:
//  - Same-entry semantics: the expression under a SearchParameterExpression is compiled into a
//    per-SearchIndexEntry matcher, so multi-field criteria (token system|code, quantity
//    system|code|value, date Start/End range checks, reference type/id) must all be satisfied by a
//    single index entry. The original evaluated each field as a whole-resource predicate and
//    intersected, allowing e.g. `code=sys|val` to cross-match system and code from different codings.
//  - FieldName-aware comparisons: Binary/String/MissingField expressions dispatch on FieldName
//    instead of SearchParameter.Type, adding Reference, Uri, Quantity, and token-system/text support
//    (the original threw NotImplementedException for Reference/Uri/Quantity string comparisons).
//  - Range-aware bounds: DateTimeStart/DateTimeEnd compare the corresponding bound of a
//    DateTimeSearchValue, and Number/Quantity comparisons pick the Low/High bound appropriate for
//    the operator (the original compared only DateTimeSearchValue.Start / NumberSearchValue.High).

using Ignixa.Search.Expressions;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.InMemory;

namespace FhirCandle.Search;

/// <summary>
/// Converts a parsed FHIR search <see cref="Expression"/> tree into a <see cref="SearchPredicate"/>
/// for in-memory filtering over <see cref="SearchIndexEntry"/> collections.
/// </summary>
public sealed class CandleSearchQueryInterpreter : IExpressionVisitorWithInitialContext<CandleSearchQueryInterpreter.Context, SearchPredicate>
{
    public Context InitialContext => default;

    public SearchPredicate VisitSearchParameter(SearchParameterExpression expression, Context context)
    {
        ArgumentNullException.ThrowIfNull(expression);

        string parameterName = expression.Parameter.Name;

        if (parameterName == "_type")
        {
            return input => input.Where(x => MatchesResourceType(expression.Expression, x.Location.ResourceType));
        }

        // _id is intrinsic to ResourceKey rather than FHIRPath-derived, so - unlike ordinary search
        // parameters - it never gets a SearchIndexEntry; match Parameter.Code rather than Name since
        // Ignixa gives this parameter's Name a synthetic value ("ID-SEARCH-PARAMETER") distinct from
        // its code ("_id").
        if (expression.Parameter.Code == "_id")
        {
            return input => input.Where(x => MatchesId(expression.Expression, x.Location.Id));
        }

        // Token :not compiles to Not(equality) inside the parameter expression; FHIR semantics
        // require "no entry matches" (including resources without the parameter), not "any entry
        // that differs", so negate the whole existence check.
        if (expression.Expression is NotExpression not)
        {
            Func<SearchIndexEntry, bool> negated = CompileEntryMatcher(not.Expression);
            return input => input.Where(x => !x.Index.Any(y => y.SearchParameter.Name == parameterName && negated(y)));
        }

        Func<SearchIndexEntry, bool> matcher = CompileEntryMatcher(expression.Expression);
        return input => input.Where(x => x.Index.Any(y => y.SearchParameter.Name == parameterName && matcher(y)));
    }

    public SearchPredicate VisitMissingSearchParameter(MissingSearchParameterExpression expression, Context context)
    {
        ArgumentNullException.ThrowIfNull(expression);

        string parameterName = expression.Parameter.Name;

        return expression.IsMissing
            ? input => input.Where(x => !x.Index.Any(y => y.SearchParameter.Name == parameterName))
            : input => input.Where(x => x.Index.Any(y => y.SearchParameter.Name == parameterName));
    }

    public SearchPredicate VisitMultiary(MultiaryExpression expression, Context context)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return expression.Expressions
            .Select(x => x.AcceptVisitor(this, context))
            .Aggregate((x, y) => expression.MultiaryOperation switch
            {
                MultiaryOperator.And => p => x(p).Intersect(y(p)),
                MultiaryOperator.Or => p => x(p).Union(y(p)),
                _ => throw new SearchOperationNotSupportedException($"MultiaryOperator {expression.MultiaryOperation} is not supported."),
            });
    }

    public SearchPredicate VisitUnion(UnionExpression expression, Context context)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return expression.Expressions
            .Select(x => x.AcceptVisitor(this, context))
            .Aggregate((x, y) => (SearchPredicate)(p => x(p).Union(y(p))));
    }

    public SearchPredicate VisitNotExpression(NotExpression expression, Context context)
    {
        ArgumentNullException.ThrowIfNull(expression);

        SearchPredicate inner = expression.Expression.AcceptVisitor(this, context);
        return input => input.Except(inner(input));
    }

    // Binary/String/MissingField only occur beneath a SearchParameterExpression, which compiles
    // them per index entry without re-entering this visitor.
    public SearchPredicate VisitBinary(BinaryExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("BinaryExpression is only supported within a search parameter expression.");

    public SearchPredicate VisitString(StringExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("StringExpression is only supported within a search parameter expression.");

    public SearchPredicate VisitMissingField(MissingFieldExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("MissingFieldExpression is only supported within a search parameter expression.");

    public SearchPredicate VisitChained(ChainedExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("ChainedExpression is not supported.");

    public SearchPredicate VisitCompartment(CompartmentSearchExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("Compartment search is not supported.");

    public SearchPredicate VisitPatientEverything(PatientEverythingExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("Patient $everything is not supported in in-memory search.");

    public SearchPredicate VisitInclude(IncludeExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("IncludeExpression is not supported in in-memory search.");

    public SearchPredicate VisitSortParameter(SortExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("SortExpression is not supported in in-memory search.");

    public SearchPredicate VisitIn<T>(InExpression<T> expression, Context context) =>
        throw new SearchOperationNotSupportedException("InExpression is not supported in in-memory search.");

    public SearchPredicate VisitNotReferenced(NotReferencedExpression expression, Context context) =>
        throw new SearchOperationNotSupportedException("_not-referenced is not supported in in-memory search.");

    private static Func<SearchIndexEntry, bool> CompileEntryMatcher(Expression expression)
    {
        switch (expression)
        {
            case MultiaryExpression multiary:
            {
                Func<SearchIndexEntry, bool>[] children = [.. multiary.Expressions.Select(CompileEntryMatcher)];
                return multiary.MultiaryOperation switch
                {
                    MultiaryOperator.And => entry => children.All(child => child(entry)),
                    MultiaryOperator.Or => entry => children.Any(child => child(entry)),
                    _ => throw new SearchOperationNotSupportedException($"MultiaryOperator {multiary.MultiaryOperation} is not supported."),
                };
            }

            case UnionExpression union:
            {
                Func<SearchIndexEntry, bool>[] children = [.. union.Expressions.Select(CompileEntryMatcher)];
                return entry => children.Any(child => child(entry));
            }

            case NotExpression not:
            {
                Func<SearchIndexEntry, bool> inner = CompileEntryMatcher(not.Expression);
                return entry => !inner(entry);
            }

            case StringExpression str:
                return entry => ResolveValues(entry.Value, str.ComponentIndex).Any(value => MatchesString(str, value));

            case BinaryExpression binary:
                return entry => ResolveValues(entry.Value, binary.ComponentIndex).Any(value => MatchesBinary(binary, value));

            case MissingFieldExpression missing:
                return entry => ResolveValues(entry.Value, missing.ComponentIndex).Any(value => IsFieldMissing(missing.FieldName, value));

            default:
                throw new SearchOperationNotSupportedException($"{expression.GetType().Name} is not supported within a search parameter expression.");
        }
    }

    private static IReadOnlyList<ISearchValue> ResolveValues(ISearchValue value, int? componentIndex)
    {
        if (componentIndex is int index && value is CompositeSearchValue composite)
        {
            return index < composite.Components.Count ? composite.Components[index] : [];
        }

        return [value];
    }

    private static bool MatchesString(StringExpression expression, ISearchValue value)
    {
        string? fieldValue = GetStringField(expression.FieldName, value);
        if (fieldValue is null)
        {
            return false;
        }

        StringComparison comparison = expression.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return expression.StringOperator switch
        {
            StringOperator.Equals => string.Equals(fieldValue, expression.Value, comparison),
            StringOperator.StartsWith => fieldValue.StartsWith(expression.Value, comparison),
            StringOperator.Contains => fieldValue.Contains(expression.Value, comparison),
            StringOperator.EndsWith => fieldValue.EndsWith(expression.Value, comparison),
            StringOperator.NotStartsWith => !fieldValue.StartsWith(expression.Value, comparison),
            StringOperator.NotContains => !fieldValue.Contains(expression.Value, comparison),
            StringOperator.NotEndsWith => !fieldValue.EndsWith(expression.Value, comparison),
            StringOperator.LeftSideStartsWith => expression.Value.StartsWith(fieldValue, comparison),
            _ => throw new SearchOperationNotSupportedException($"StringOperator {expression.StringOperator} is not supported."),
        };
    }

    private static string? GetStringField(FieldName fieldName, ISearchValue value) =>
        (fieldName, value) switch
        {
            (FieldName.String, StringSearchValue s) => s.String,
            (FieldName.TokenCode, TokenSearchValue token) => token.Code,
            (FieldName.TokenCode, OfTypeTokenSearchValue ofType) => ofType.IdentifierValue,
            (FieldName.TokenSystem, TokenSearchValue token) => token.System,
            (FieldName.TokenText, TokenSearchValue token) => token.Text,
            (FieldName.ReferenceResourceId, ReferenceSearchValue reference) => reference.ResourceId,
            (FieldName.ReferenceResourceType, ReferenceSearchValue reference) => reference.ResourceType,
            (FieldName.ReferenceBaseUri, ReferenceSearchValue reference) => reference.BaseUri?.ToString(),
            (FieldName.QuantitySystem, QuantitySearchValue quantity) => quantity.System,
            (FieldName.QuantityCode, QuantitySearchValue quantity) => quantity.Code,
            (FieldName.Uri, UriSearchValue uri) => uri.Uri,
            (FieldName.UriVersion, UriSearchValue uri) => uri.Version,
            (FieldName.UriFragment, UriSearchValue uri) => uri.Fragment,
            (FieldName.IdentifierTypeSystem, OfTypeTokenSearchValue ofType) => ofType.TypeSystem,
            (FieldName.IdentifierTypeSystem, TokenSearchValue token) => token.IdentifierTypeSystem,
            (FieldName.IdentifierTypeCode, OfTypeTokenSearchValue ofType) => ofType.TypeCode,
            (FieldName.IdentifierTypeCode, TokenSearchValue token) => token.IdentifierTypeCode,
            _ => null,
        };

    private static bool MatchesBinary(BinaryExpression expression, ISearchValue value) =>
        (expression.FieldName, value) switch
        {
            (FieldName.DateTimeStart, DateTimeSearchValue date) =>
                Satisfies(date.Start.CompareTo((DateTimeOffset)expression.Value), expression.BinaryOperator),
            (FieldName.DateTimeEnd, DateTimeSearchValue date) =>
                Satisfies(date.End.CompareTo((DateTimeOffset)expression.Value), expression.BinaryOperator),
            (FieldName.Number, NumberSearchValue number) =>
                CompareRange(number.Low, number.High, (decimal)expression.Value, expression.BinaryOperator),
            (FieldName.Quantity, QuantitySearchValue quantity) =>
                CompareRange(quantity.Low, quantity.High, (decimal)expression.Value, expression.BinaryOperator),
            _ => false,
        };

    /// <summary>
    /// Compares a possibly ranged (Low..High) index value: greater-than comparisons test the upper
    /// bound and less-than comparisons the lower bound, so a range matches when any part of it
    /// satisfies the comparison (overlap semantics, mirroring the DateTimeStart/DateTimeEnd split).
    /// </summary>
    private static bool CompareRange(decimal? low, decimal? high, decimal target, BinaryOperator binaryOperator)
    {
        decimal? bound = binaryOperator switch
        {
            BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => high ?? low,
            BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual => low ?? high,
            _ => low ?? high,
        };

        return bound is decimal boundValue && Satisfies(boundValue.CompareTo(target), binaryOperator);
    }

    private static bool Satisfies(int comparison, BinaryOperator binaryOperator) =>
        binaryOperator switch
        {
            BinaryOperator.Equal => comparison == 0,
            BinaryOperator.NotEqual => comparison != 0,
            BinaryOperator.GreaterThan => comparison > 0,
            BinaryOperator.GreaterThanOrEqual => comparison >= 0,
            BinaryOperator.LessThan => comparison < 0,
            BinaryOperator.LessThanOrEqual => comparison <= 0,
            _ => throw new SearchOperationNotSupportedException($"BinaryOperator {binaryOperator} is not supported."),
        };

    private static bool IsFieldMissing(FieldName fieldName, ISearchValue value) =>
        (fieldName, value) switch
        {
            (FieldName.ReferenceBaseUri, ReferenceSearchValue reference) => reference.BaseUri is null,
            (FieldName.TokenSystem, TokenSearchValue token) => string.IsNullOrEmpty(token.System),
            (FieldName.TokenText, TokenSearchValue token) => string.IsNullOrEmpty(token.Text),
            (FieldName.UriVersion, UriSearchValue uri) => string.IsNullOrEmpty(uri.Version),
            (FieldName.UriFragment, UriSearchValue uri) => string.IsNullOrEmpty(uri.Fragment),
            _ => false,
        };

    private static bool MatchesResourceType(Expression expression, string resourceType) =>
        expression switch
        {
            StringExpression str => string.Equals(
                resourceType,
                str.Value,
                str.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            MultiaryExpression { MultiaryOperation: MultiaryOperator.Or } multiary =>
                multiary.Expressions.Any(x => MatchesResourceType(x, resourceType)),
            MultiaryExpression { MultiaryOperation: MultiaryOperator.And } multiary =>
                multiary.Expressions.All(x => MatchesResourceType(x, resourceType)),
            UnionExpression union => union.Expressions.Any(x => MatchesResourceType(x, resourceType)),
            _ => throw new SearchOperationNotSupportedException($"{expression.GetType().Name} is not supported for _type."),
        };

    private static bool MatchesId(Expression expression, string resourceId) =>
        expression switch
        {
            StringExpression str => string.Equals(
                resourceId,
                str.Value,
                str.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            MultiaryExpression { MultiaryOperation: MultiaryOperator.Or } multiary =>
                multiary.Expressions.Any(x => MatchesId(x, resourceId)),
            MultiaryExpression { MultiaryOperation: MultiaryOperator.And } multiary =>
                multiary.Expressions.All(x => MatchesId(x, resourceId)),
            UnionExpression union => union.Expressions.Any(x => MatchesId(x, resourceId)),
            _ => throw new SearchOperationNotSupportedException($"{expression.GetType().Name} is not supported for _id."),
        };

    /// <summary>
    /// Visitor context. The original threaded the active parameter name through this; the fork
    /// compiles everything beneath a SearchParameterExpression directly, so no state remains.
    /// </summary>
    public readonly record struct Context;
}
