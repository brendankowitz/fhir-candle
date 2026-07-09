using FhirCandle.Search;
using Ignixa.Abstractions;
using Ignixa.Search.Indexing;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class CandleSearchServiceTests
{
    private static readonly IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private const string ObservationJson =
        """
        {
          "resourceType": "Observation",
          "id": "obs-1",
          "status": "final",
          "code": {
            "coding": [{"system": "http://loinc.org", "code": "8480-6", "display": "Systolic blood pressure"}]
          }
        }
        """;

    private static CandleSearchService CreateService() => new(R4, NullLoggerFactory.Instance);

    private static IElement ParseObservation()
    {
        ResourceJsonNode node = JsonSourceNodeFactory.Parse(ObservationJson);
        return node.ToElement(R4);
    }

    [Fact]
    public void ParseQuery_SimpleQueryWithCount_ParsesWithNoCustomFilters()
    {
        CandleSearchService service = CreateService();

        ParsedQuery query = service.ParseQuery("Patient", "name=Chalmers&_count=5");

        query.Options.MaxItemCount.ShouldBe(5);
        query.CustomFilters.ShouldBeEmpty();
        query.Options.Expression.ShouldNotBeNull();
    }

    [Fact]
    public void ParseQuery_CodeInModifier_IsInterceptedAsCustomFilter()
    {
        CandleSearchService service = CreateService();

        ParsedQuery query = service.ParseQuery("Observation", "code:in=http://example.org/vs/x");

        query.CustomFilters.ShouldHaveSingleItem();
        query.CustomFilters[0].ShouldBe(new CustomModifierFilter("code", "in", "http://example.org/vs/x"));
        query.Options.Expression.ShouldBeNull();
    }

    [Fact]
    public void TestForMatch_CodeInModifier_VsContainsStub_TrueAndFalse()
    {
        CandleSearchService service = CreateService();
        IElement observation = ParseObservation();
        IReadOnlyCollection<SearchIndexEntry> index = service.Index(observation);
        var key = new ResourceKey("Observation", "obs-1");

        ParsedQuery query = service.ParseQuery("Observation", "code:in=http://example.org/vs/x");

        bool matchWhenContained = service.TestForMatch(
            key, index, query, observation,
            (vsUrl, system, code) => vsUrl == "http://example.org/vs/x" && system == "http://loinc.org" && code == "8480-6");
        matchWhenContained.ShouldBeTrue();

        bool matchWhenNotContained = service.TestForMatch(
            key, index, query, observation,
            (_, _, _) => false);
        matchWhenNotContained.ShouldBeFalse();
    }

    [Fact]
    public void ParseQuery_UnrecognizedParameter_SurfacesInUnknownParameters()
    {
        CandleSearchService service = CreateService();

        ParsedQuery query = service.ParseQuery("Patient", "bogusparam=1");

        query.UnknownParameters.ShouldContain("bogusparam");
    }
}
