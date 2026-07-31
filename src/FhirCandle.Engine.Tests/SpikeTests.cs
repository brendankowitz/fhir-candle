using Ignixa.Abstractions;
using Ignixa.Search.Definition;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.InMemory;
using Ignixa.Search.Parsing;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class SpikeTests
{
    private const string PatientJson =
        """{"resourceType":"Patient","id":"pat-1","active":true,"name":[{"use":"official","family":"Chalmers","given":["Peter"]}],"birthDate":"1974-12-25"}""";

    [Theory]
    [InlineData(FhirVersion.R4)]
    [InlineData(FhirVersion.R4B)]
    [InlineData(FhirVersion.R5)]
    public void ParseIndexSearchSerialize_SingleAssembly_AllVersions(FhirVersion version)
    {
        IFhirSchemaProvider schema = FhirCandle.Schema.FhirSchemas.GetByIgnixaVersion(version);

        ResourceJsonNode patient = JsonSourceNodeFactory.Parse(PatientJson);
        patient.Meta.VersionId = "1";
        patient.Meta.LastUpdatedOffset = DateTimeOffset.UtcNow;
        patient.ResourceType.ShouldBe("Patient");

        var spManager = new SearchParameterDefinitionManager(
            schema, NullLogger<SearchParameterDefinitionManager>.Instance);
        var indexer = SearchIndexerFactory.CreateInstance(schema, NullLoggerFactory.Instance, spManager, NullFhirBaseUriProvider.Instance);

        IElement element = patient.ToElement(schema);
        IReadOnlyCollection<SearchIndexEntry> index = indexer.Extract(element);
        index.ShouldNotBeEmpty();

        ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver resolver =
            () => spManager;
        var expressionParser = new ExpressionParser(
            resolver,
            new SearchParameterExpressionParser(new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance), schema),
            schema);
        var builder = new SearchOptionsBuilder(expressionParser, spManager);

        var query = new QueryParameterParser().Parse("birthdate=ge1970-01-01&name=Chalmers");
        var options = builder.Build("Patient", query, schema);
        options.Expression.ShouldNotBeNull();

        var predicate = options.Expression!.AcceptVisitor(new SearchQueryInterpreter(), default);
        var corpus = new[] { (new ResourceKey("Patient", "pat-1"), (IReadOnlyCollection<SearchIndexEntry>)index) };
        predicate(corpus).ShouldHaveSingleItem();

        var missQuery = new QueryParameterParser().Parse("birthdate=lt1970-01-01");
        var missOptions = builder.Build("Patient", missQuery, schema);
        var missPredicate = missOptions.Expression!.AcceptVisitor(new SearchQueryInterpreter(), default);
        missPredicate(corpus).ShouldBeEmpty();

        string roundTripped = patient.SerializeToString();
        roundTripped.ShouldContain("\"birthDate\":\"1974-12-25\"");
        roundTripped.ShouldContain("\"versionId\":\"1\"");
    }

    [Fact]
    public void SequenceCodeMapping_CoversR4R4BR5()
    {
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4).FullVersion.ShouldBe("4.0.1");
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B).Version.ShouldBe(FhirVersion.R4B);
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5).Version.ShouldBe(FhirVersion.R5);
    }
}
