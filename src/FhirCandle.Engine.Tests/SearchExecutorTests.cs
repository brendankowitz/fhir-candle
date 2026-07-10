using System.Net;
using FhirCandle.Models;
using FhirCandle.Search;
using FhirCandle.Serialization;
using FhirCandle.Storage;
using Ignixa.Abstractions;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class SearchExecutorTests
{
    private static readonly IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private static FhirRequestContext Ctx() => new()
    {
        TenantName = "test",
        Store = null!,
        HttpMethod = "POST",
        Url = "Patient",
        Authorization = null,
    };

    private static ResourceJsonNode Parse(string json)
    {
        SerializationUtils.TryDeserializeFhir(json, "json", out ResourceJsonNode? resource, out string exMessage)
            .ShouldBe(HttpStatusCode.OK, exMessage);
        return resource!;
    }

    private sealed class Fixture
    {
        public CandleSearchService Search { get; } = new(R4, NullLoggerFactory.Instance);
        public ResourceStore PatientStore { get; }
        public ResourceStore ObservationStore { get; }

        public Fixture()
        {
            PatientStore = new ResourceStore("Patient", R4, Search, (_, _, _) => false, _ => null);
            ObservationStore = new ResourceStore("Observation", R4, Search, (_, _, _) => false, _ => null);
        }

        public ResourceStore? Resolve(string resourceType) => resourceType switch
        {
            "Patient" => PatientStore,
            "Observation" => ObservationStore,
            _ => null,
        };

        public ResourceJsonNode CreatePatient(string id, string family, string birthDate)
        {
            ResourceJsonNode patient = Parse(
                $$"""
                {
                  "resourceType": "Patient",
                  "id": "{{id}}",
                  "name": [{"family": "{{family}}"}],
                  "birthDate": "{{birthDate}}"
                }
                """);
            PatientStore.InstanceCreate(Ctx(), patient, allowExistingId: true, out HttpStatusCode sc, out _);
            sc.ShouldBe(HttpStatusCode.Created);
            return PatientStore.InstanceRead(id)!;
        }

        public ResourceJsonNode CreateObservation(string id, string subjectReference)
        {
            ResourceJsonNode observation = Parse(
                $$"""
                {
                  "resourceType": "Observation",
                  "id": "{{id}}",
                  "status": "final",
                  "code": {"coding": [{"system": "http://loinc.org", "code": "8480-6"}]},
                  "subject": {"reference": "{{subjectReference}}"}
                }
                """);
            ObservationStore.InstanceCreate(Ctx(), observation, allowExistingId: true, out HttpStatusCode sc, out _);
            sc.ShouldBe(HttpStatusCode.Created);
            return ObservationStore.InstanceRead(id)!;
        }
    }

    [Fact]
    public void ResolveIncludes_ObservationSubject_YieldsReferencedPatient()
    {
        Fixture fx = new();
        fx.CreatePatient("pat-1", "Chalmers", "1974-12-25");
        fx.CreateObservation("obs-1", "Patient/pat-1");

        ParsedQuery query = fx.Search.ParseQuery("Observation", "_include=Observation:subject");
        List<ResourceJsonNode> matches = fx.ObservationStore.TypeSearch(query).ToList();
        matches.ShouldHaveSingleItem();

        List<ResourceJsonNode> included = SearchExecutor.ResolveIncludes(
            matches, query.Options.Include, fx.Resolve, fx.Search, R4).ToList();

        included.ShouldHaveSingleItem();
        included[0].ResourceType.ShouldBe("Patient");
        included[0].Id.ShouldBe("pat-1");
    }

    [Fact]
    public void ResolveRevIncludes_ObservationSubject_YieldsReferencingObservation()
    {
        Fixture fx = new();
        fx.CreatePatient("pat-1", "Chalmers", "1974-12-25");
        fx.CreateObservation("obs-1", "Patient/pat-1");

        ParsedQuery query = fx.Search.ParseQuery("Patient", "_revinclude=Observation:subject");
        List<ResourceJsonNode> matches = fx.PatientStore.TypeSearch(query).ToList();
        matches.ShouldHaveSingleItem();

        List<ResourceJsonNode> revIncluded = SearchExecutor.ResolveRevIncludes(
            matches, query.Options.RevInclude, fx.Resolve, fx.Search, R4).ToList();

        revIncluded.ShouldHaveSingleItem();
        revIncluded[0].ResourceType.ShouldBe("Observation");
        revIncluded[0].Id.ShouldBe("obs-1");
    }

    [Fact]
    public void EvaluateChained_SubjectNameChalmers_MatchesOnlyOnCorrectValue()
    {
        Fixture fx = new();
        fx.CreatePatient("pat-1", "Chalmers", "1974-12-25");
        ResourceJsonNode observation = fx.CreateObservation("obs-1", "Patient/pat-1");

        ParsedQuery matchQuery = fx.Search.ParseQuery("Observation", "subject:Patient.name=Chalmers");
        matchQuery.ChainedExpressions.ShouldHaveSingleItem();
        SearchExecutor.EvaluateChained(matchQuery.ChainedExpressions[0][0], observation, fx.Resolve, fx.Search, R4)
            .ShouldBeTrue();

        ParsedQuery noMatchQuery = fx.Search.ParseQuery("Observation", "subject:Patient.name=Nomatch");
        noMatchQuery.ChainedExpressions.ShouldHaveSingleItem();
        SearchExecutor.EvaluateChained(noMatchQuery.ChainedExpressions[0][0], observation, fx.Resolve, fx.Search, R4)
            .ShouldBeFalse();
    }

    [Fact]
    public void EvaluateChained_HasObservationSubjectCode_MatchesReferencedPatient()
    {
        Fixture fx = new();
        ResourceJsonNode patient = fx.CreatePatient("pat-1", "Chalmers", "1974-12-25");
        fx.CreateObservation("obs-1", "Patient/pat-1");

        ParsedQuery hasQuery = fx.Search.ParseQuery("Patient", "_has:Observation:subject:code=http://loinc.org|8480-6");
        hasQuery.ChainedExpressions.ShouldHaveSingleItem();

        SearchExecutor.EvaluateChained(hasQuery.ChainedExpressions[0][0], patient, fx.Resolve, fx.Search, R4)
            .ShouldBeTrue();

        ParsedQuery noMatchQuery = fx.Search.ParseQuery("Patient", "_has:Observation:subject:code=http://loinc.org|1234-5");
        SearchExecutor.EvaluateChained(noMatchQuery.ChainedExpressions[0][0], patient, fx.Resolve, fx.Search, R4)
            .ShouldBeFalse();
    }

    [Fact]
    public void FhirSortComparer_SortsByBirthdate_AscendingAndDescending()
    {
        Fixture fx = new();
        ResourceJsonNode older = fx.CreatePatient("p1", "Older", "1970-01-01");
        ResourceJsonNode younger = fx.CreatePatient("p2", "Younger", "1990-06-15");

        ParsedQuery ascendingQuery = fx.Search.ParseQuery("Patient", "_sort=birthdate");
        var ascendingComparer = new FhirSortComparer(R4, ascendingQuery.Options.Sort, fx.Search.Definitions);
        List<ResourceJsonNode> ascending = new List<ResourceJsonNode> { younger, older }
            .OrderBy(x => x, ascendingComparer)
            .ToList();
        ascending[0].Id.ShouldBe("p1");
        ascending[1].Id.ShouldBe("p2");

        ParsedQuery descendingQuery = fx.Search.ParseQuery("Patient", "_sort=-birthdate");
        var descendingComparer = new FhirSortComparer(R4, descendingQuery.Options.Sort, fx.Search.Definitions);
        List<ResourceJsonNode> descending = new List<ResourceJsonNode> { older, younger }
            .OrderBy(x => x, descendingComparer)
            .ToList();
        descending[0].Id.ShouldBe("p2");
        descending[1].Id.ShouldBe("p1");
    }
}
