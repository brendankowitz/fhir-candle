using System.Globalization;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.Search.Definition;
using Ignixa.Search.Expressions;
using Ignixa.Search.Models;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Search;

/// <summary>
/// Orders <see cref="ResourceJsonNode"/> instances per a FHIR <c>_sort</c> query, evaluating each sort
/// parameter's FHIRPath expression against the resource and comparing the first resulting value.
/// </summary>
/// <remarks>
/// <see cref="List{T}.Sort"/> is not guaranteed stable; callers that need FHIR's "insertion order preserved
/// for ties" semantics should apply this comparer via a stable sort (e.g. <c>Enumerable.OrderBy</c>).
/// </remarks>
public sealed class FhirSortComparer : IComparer<ResourceJsonNode>
{
    private readonly IFhirSchemaProvider _schema;
    private readonly IReadOnlyList<SortExpression> _sorts;
    private readonly SearchParameterDefinitionManager _definitions;

    public FhirSortComparer(
        IFhirSchemaProvider schema,
        IReadOnlyList<SortExpression> sorts,
        SearchParameterDefinitionManager definitions)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(definitions);

        _schema = schema;
        _sorts = sorts;
        _definitions = definitions;
    }

    public int Compare(ResourceJsonNode? x, ResourceJsonNode? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null) return -1;
        if (y is null) return 1;

        IElement xElement = x.ToElement(_schema);
        IElement yElement = y.ToElement(_schema);

        foreach (SortExpression sort in _sorts)
        {
            string xExpression = ResolveExpression(sort.Parameter, x.ResourceType);
            string yExpression = ResolveExpression(sort.Parameter, y.ResourceType);

            object? xValue = xElement.Select(xExpression).FirstOrDefault()?.Value;
            object? yValue = yElement.Select(yExpression).FirstOrDefault()?.Value;

            int comparison = CompareValues(xValue, yValue);
            if (comparison != 0)
            {
                return sort.SortOrder == SortOrder.Descending ? -comparison : comparison;
            }
        }

        return 0;
    }

    /// <summary>
    /// Resolves the sort parameter's code against the resource's own type. Mirrors how <c>_sort</c>
    /// codes are normally resolved for a single resource type, but re-resolves per instance so this
    /// comparer also behaves correctly if it is ever handed a mixed-type result set (e.g. after
    /// <see cref="SearchExecutor.ResolveIncludes"/>).
    /// </summary>
    private string ResolveExpression(SearchParameterInfo parameter, string resourceType) =>
        _definitions.TryGetSearchParameter(resourceType, parameter.Code, out SearchParameterInfo resolved)
            ? resolved.Expression
            : parameter.Expression;

    private static int CompareValues(object? x, object? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (IsNumeric(x) && IsNumeric(y))
        {
            decimal xd = Convert.ToDecimal(x, CultureInfo.InvariantCulture);
            decimal yd = Convert.ToDecimal(y, CultureInfo.InvariantCulture);
            return xd.CompareTo(yd);
        }

        return string.CompareOrdinal(ToComparableString(x), ToComparableString(y));
    }

    private static bool IsNumeric(object value) =>
        value is decimal or int or long or double or float;

    private static string ToComparableString(object value) => value switch
    {
        // Dates/times are compared as ISO-8601 strings - ordinal comparison of that format is
        // chronologically correct because more-significant date components appear first.
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
