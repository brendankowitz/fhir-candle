using FhirCandle.Search;
using Ignixa.Abstractions;
using Ignixa.Search.Indexing;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class CandleSearchQueryInterpreterTests
{
    private static readonly IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private static readonly CandleSearchService Service = new(R4, NullLoggerFactory.Instance);

    private static bool Matches(string resourceType, string resourceJson, string queryString)
    {
        IElement resource = JsonSourceNodeFactory.Parse(resourceJson).ToElement(R4);
        IReadOnlyCollection<SearchIndexEntry> index = Service.Index(resource);
        var key = new ResourceKey(resourceType, "test-id");

        ParsedQuery query = Service.ParseQuery(resourceType, queryString);
        return Service.TestForMatch(key, index, query, resource, (_, _, _) => false);
    }

    private const string ObservationWithSubject = """
        {
          "resourceType": "Observation",
          "id": "obs-ref",
          "status": "final",
          "code": {"coding": [{"system": "http://loinc.org", "code": "8480-6"}]},
          "subject": {"reference": "Patient/example"}
        }
        """;

    private const string ObservationWithTwoCodings = """
        {
          "resourceType": "Observation",
          "id": "obs-codings",
          "status": "final",
          "code": {
            "coding": [
              {"system": "http://system-a.example.org", "code": "code-a"},
              {"system": "http://system-b.example.org", "code": "code-b"}
            ]
          }
        }
        """;

    private const string ObservationWithPeriod = """
        {
          "resourceType": "Observation",
          "id": "obs-period",
          "status": "final",
          "code": {"coding": [{"system": "http://loinc.org", "code": "8480-6"}]},
          "effectivePeriod": {"start": "2024-01-01", "end": "2024-06-30"}
        }
        """;

    [Fact]
    public void ReferenceSearch_IsHandledByInterpreter_NotCustomFilter()
    {
        ParsedQuery query = Service.ParseQuery("Observation", "subject=Patient/example");

        query.CustomFilters.ShouldBeEmpty();
        query.Options.Expression.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("subject=Patient/example", true)]
    [InlineData("subject=example", true)]
    [InlineData("subject:Patient=example", true)]
    [InlineData("subject=Patient/other", false)]
    [InlineData("subject=Group/example", false)]
    public void ReferenceSearch_TypeAndIdForms_MatchExpected(string queryString, bool expected)
    {
        Matches("Observation", ObservationWithSubject, queryString).ShouldBe(expected);
    }

    [Theory]
    [InlineData("code=http://system-a.example.org|code-a", true)]
    [InlineData("code=http://system-b.example.org|code-b", true)]
    [InlineData("code=http://system-a.example.org|code-b", false)]
    [InlineData("code=http://system-b.example.org|code-a", false)]
    [InlineData("code=code-b", true)]
    public void TokenSearch_SystemAndCode_MustMatchOnSameCoding(string queryString, bool expected)
    {
        Matches("Observation", ObservationWithTwoCodings, queryString).ShouldBe(expected);
    }

    [Theory]
    [InlineData("date=2024-03-15", true)]
    [InlineData("date=2023-12-01", false)]
    [InlineData("date=2024-07-15", false)]
    [InlineData("date=ge2024-06-30", true)]
    [InlineData("date=le2024-01-01", true)]
    [InlineData("date=gt2024-05-01", true)]
    [InlineData("date=gt2024-07-01", false)]
    [InlineData("date=lt2023-12-31", false)]
    public void DateSearch_PeriodValuedIndex_ConsidersBothBounds(string queryString, bool expected)
    {
        Matches("Observation", ObservationWithPeriod, queryString).ShouldBe(expected);
    }
}
