using System.Net;
using FhirCandle.Models;
using FhirCandle.Search;
using FhirCandle.Serialization;
using FhirCandle.Storage;
using Ignixa.Abstractions;
using Ignixa.Models;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class ResourceStoreTests
{
    private static readonly IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private static CandleSearchService CreateSearch() => new(R4, NullLoggerFactory.Instance);

    private static ResourceStore CreateStore(string resourceType, CandleSearchService search) =>
        new(resourceType, R4, search, (_, _, _) => false, _ => null);

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

    [Fact]
    public void InstanceCreate_AssignsIdAndInitialVersion()
    {
        CandleSearchService search = CreateSearch();
        ResourceStore store = CreateStore("Patient", search);
        ResourceJsonNode patient = Parse("""{"resourceType":"Patient"}""");

        ResourceJsonNode? created = store.InstanceCreate(Ctx(), patient, allowExistingId: false, out HttpStatusCode sc, out OperationOutcome outcome);

        sc.ShouldBe(HttpStatusCode.Created);
        created.ShouldNotBeNull();
        created!.Id.ShouldNotBeNullOrEmpty();
        created.Meta.VersionId.ShouldBe("1");
        created.Meta.LastUpdated.ShouldNotBeNull();
    }

    [Fact]
    public void InstanceUpdate_BumpsVersion_AndHonorsIfMatch()
    {
        CandleSearchService search = CreateSearch();
        ResourceStore store = CreateStore("Patient", search);
        ResourceJsonNode patient = Parse("""{"resourceType":"Patient","id":"p1"}""");
        store.InstanceCreate(Ctx(), patient, allowExistingId: true, out _, out _);

        ResourceJsonNode updateGood = Parse("""{"resourceType":"Patient","id":"p1","active":true}""");
        ResourceJsonNode? updated = store.InstanceUpdate(
            updateGood, allowCreate: false, ifMatch: "W/\"1\"", ifNoneMatch: "", protectedResources: [],
            out HttpStatusCode sc, out OperationOutcome outcome);

        sc.ShouldBe(HttpStatusCode.OK);
        updated.ShouldNotBeNull();
        updated!.Meta.VersionId.ShouldBe("2");

        ResourceJsonNode updateStale = Parse("""{"resourceType":"Patient","id":"p1","active":false}""");
        ResourceJsonNode? rejected = store.InstanceUpdate(
            updateStale, allowCreate: false, ifMatch: "W/\"9\"", ifNoneMatch: "", protectedResources: [],
            out HttpStatusCode staleSc, out OperationOutcome staleOutcome);

        staleSc.ShouldBe(HttpStatusCode.PreconditionFailed);
        rejected.ShouldBeNull();
    }

    [Fact]
    public void InstanceDelete_RemovesResource()
    {
        CandleSearchService search = CreateSearch();
        ResourceStore store = CreateStore("Patient", search);
        ResourceJsonNode patient = Parse("""{"resourceType":"Patient","id":"p1"}""");
        store.InstanceCreate(Ctx(), patient, allowExistingId: true, out _, out _);

        ResourceJsonNode? deleted = store.InstanceDelete("p1", []);

        deleted.ShouldNotBeNull();
        deleted!.Id.ShouldBe("p1");
        store.InstanceRead("p1").ShouldBeNull();
    }

    [Fact]
    public void CanonicalIndex_TryGetByCanonical_FindsStoredSearchParameter()
    {
        CandleSearchService search = CreateSearch();
        ResourceStore store = CreateStore("SearchParameter", search);
        ResourceJsonNode sp = Parse(
            """
            {
              "resourceType": "SearchParameter",
              "id": "sp-1",
              "url": "http://example.org/SearchParameter/patient-foo",
              "name": "foo",
              "status": "active",
              "description": "test parameter",
              "code": "foo",
              "base": ["Patient"],
              "type": "string",
              "expression": "Patient.name.family"
            }
            """);
        store.InstanceCreate(Ctx(), sp, allowExistingId: true, out HttpStatusCode sc, out _);
        sc.ShouldBe(HttpStatusCode.Created);

        bool found = store.TryGetByCanonical("http://example.org/SearchParameter/patient-foo", out ResourceJsonNode? resource);

        found.ShouldBeTrue();
        resource.ShouldNotBeNull();
        resource!.Id.ShouldBe("sp-1");
    }

    [Fact]
    public void IdentifierIndex_TryResolveIdentifier_FindsStoredPatient()
    {
        CandleSearchService search = CreateSearch();
        ResourceStore store = CreateStore("Patient", search);
        ResourceJsonNode patient = Parse(
            """
            {
              "resourceType": "Patient",
              "id": "p1",
              "identifier": [{"system": "http://sys", "value": "val"}]
            }
            """);
        store.InstanceCreate(Ctx(), patient, allowExistingId: true, out _, out _);

        bool found = store.TryResolveIdentifier("http://sys", "val", out ResourceJsonNode? resource);

        found.ShouldBeTrue();
        resource.ShouldNotBeNull();
        resource!.Id.ShouldBe("p1");
    }

    [Fact]
    public void TypeSearch_BirthdateQuery_ReturnsMatch()
    {
        CandleSearchService search = CreateSearch();
        ResourceStore store = CreateStore("Patient", search);
        ResourceJsonNode match = Parse("""{"resourceType":"Patient","id":"p1","birthDate":"1980-01-01"}""");
        ResourceJsonNode noMatch = Parse("""{"resourceType":"Patient","id":"p2","birthDate":"1960-01-01"}""");
        store.InstanceCreate(Ctx(), match, allowExistingId: true, out _, out _);
        store.InstanceCreate(Ctx(), noMatch, allowExistingId: true, out _, out _);

        ParsedQuery query = search.ParseQuery("Patient", "birthdate=ge1970-01-01");
        List<ResourceJsonNode> results = store.TypeSearch(query).ToList();

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe("p1");
    }
}
