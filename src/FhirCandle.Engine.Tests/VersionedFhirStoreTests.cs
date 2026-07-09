using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Storage;
using FhirCandle.Utils;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class VersionedFhirStoreTests
{
    private static VersionedFhirStore CreateStore(FhirReleases.FhirSequenceCodes version)
    {
        var store = new VersionedFhirStore();
        store.Init(new TenantConfiguration
        {
            FhirVersion = version,
            ControllerName = "test",
            BaseUrl = "http://localhost/fhir/test",
            SupportNotChanged = true,
        });
        return store;
    }

    private static FhirRequestContext Ctx(
        VersionedFhirStore store,
        string httpMethod,
        string url,
        string? sourceContent = null,
        string ifMatch = "",
        string ifNoneMatch = "") => new()
    {
        TenantName = "test",
        Store = store,
        HttpMethod = httpMethod,
        Url = url,
        Authorization = null,
        SourceContent = sourceContent ?? string.Empty,
        SourceFormat = sourceContent is null ? string.Empty : "application/fhir+json",
        IfMatch = ifMatch,
        IfNoneMatch = ifNoneMatch,
    };

    [Fact]
    public void FullCrudAndSearchLifecycle_R4()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R4);

        // create
        bool createOk = store.InstanceCreate(
            Ctx(store, "POST", "Patient", """{"resourceType":"Patient","birthDate":"1980-01-01"}"""),
            out FhirResponseContext createResponse);

        createOk.ShouldBeTrue();
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        createResponse.SerializedResource.ShouldContain("\"versionId\":\"1\"");

        string id = createResponse.Id;
        id.ShouldNotBeNullOrEmpty();

        // read
        bool readOk = store.InstanceRead(Ctx(store, "GET", $"Patient/{id}"), out FhirResponseContext readResponse);
        readOk.ShouldBeTrue();
        readResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // conditional read - not modified
        bool condReadOk = store.InstanceRead(
            Ctx(store, "GET", $"Patient/{id}", ifNoneMatch: "W/\"1\""),
            out FhirResponseContext condReadResponse);
        condReadResponse.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        // update
        bool updateOk = store.InstanceUpdate(
            Ctx(store, "PUT", $"Patient/{id}", $$"""{"resourceType":"Patient","id":"{{id}}","birthDate":"1980-01-01","active":true}"""),
            out FhirResponseContext updateResponse);

        updateOk.ShouldBeTrue();
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        updateResponse.SerializedResource.ShouldContain("\"versionId\":\"2\"");

        // search
        bool searchOk = store.TypeSearch(Ctx(store, "GET", "Patient?birthdate=ge1970-01-01"), out FhirResponseContext searchResponse);
        searchOk.ShouldBeTrue();
        searchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode bundle = JsonNode.Parse(searchResponse.SerializedResource)!;
        bundle["total"]!.GetValue<int>().ShouldBe(1);
        bundle["entry"]![0]!["search"]!["mode"]!.GetValue<string>().ShouldBe("match");

        // delete
        bool deleteOk = store.InstanceDelete(Ctx(store, "DELETE", $"Patient/{id}"), out FhirResponseContext deleteResponse);
        deleteOk.ShouldBeTrue();
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // read after delete - not found
        bool readAfterDeleteOk = store.InstanceRead(Ctx(store, "GET", $"Patient/{id}"), out FhirResponseContext readAfterDeleteResponse);
        readAfterDeleteOk.ShouldBeFalse();
        readAfterDeleteResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void CreateAndSearch_R5()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R5);

        bool createOk = store.InstanceCreate(
            Ctx(store, "POST", "Patient", """{"resourceType":"Patient","birthDate":"1990-05-05"}"""),
            out FhirResponseContext createResponse);

        createOk.ShouldBeTrue();
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        bool searchOk = store.TypeSearch(Ctx(store, "GET", "Patient?birthdate=ge1970-01-01"), out FhirResponseContext searchResponse);
        searchOk.ShouldBeTrue();

        JsonNode bundle = JsonNode.Parse(searchResponse.SerializedResource)!;
        bundle["total"]!.GetValue<int>().ShouldBe(1);
    }
}
