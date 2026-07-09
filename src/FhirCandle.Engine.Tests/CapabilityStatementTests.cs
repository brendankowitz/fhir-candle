using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Storage;
using FhirCandle.Utils;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class CapabilityStatementTests
{
    private static VersionedFhirStore CreateStore(FhirReleases.FhirSequenceCodes version)
    {
        var store = new VersionedFhirStore();
        store.Init(new TenantConfiguration
        {
            FhirVersion = version,
            ControllerName = "test",
            BaseUrl = "http://localhost/fhir/test",
        });
        return store;
    }

    private static FhirRequestContext Ctx(VersionedFhirStore store) => new()
    {
        TenantName = "test",
        Store = store,
        HttpMethod = "GET",
        Url = "metadata",
        Authorization = null,
    };

    private static JsonObject GetPatientResourceComponent(JsonObject rest0)
    {
        JsonObject? patient = rest0["resource"]!.AsArray()
            .Select(r => r!.AsObject())
            .FirstOrDefault(r => r["type"]!.GetValue<string>() == "Patient");

        patient.ShouldNotBeNull();
        return patient!;
    }

    [Fact]
    public void GetMetadata_R4_ReturnsCapabilityStatement()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R4);

        bool ok = store.GetMetadata(Ctx(store), out FhirResponseContext response);

        ok.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject cs = JsonNode.Parse(response.SerializedResource)!.AsObject();
        cs["resourceType"]!.GetValue<string>().ShouldBe("CapabilityStatement");
        cs["fhirVersion"]!.GetValue<string>().ShouldBe("4.0.1");
        cs["status"]!.GetValue<string>().ShouldBe("active");

        JsonObject rest0 = cs["rest"]![0]!.AsObject();
        rest0["mode"]!.GetValue<string>().ShouldBe("server");

        JsonObject patient = GetPatientResourceComponent(rest0);

        List<string> interactions = patient["interaction"]!.AsArray()
            .Select(i => i!["code"]!.GetValue<string>())
            .ToList();
        interactions.ShouldContain("read");

        List<string> searchParams = patient["searchParam"]!.AsArray()
            .Select(sp => sp!["name"]!.GetValue<string>())
            .ToList();
        searchParams.ShouldContain("birthdate");
    }

    [Fact]
    public void GetMetadata_R5_ReturnsCapabilityStatement()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R5);

        bool ok = store.GetMetadata(Ctx(store), out FhirResponseContext response);

        ok.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject cs = JsonNode.Parse(response.SerializedResource)!.AsObject();
        cs["resourceType"]!.GetValue<string>().ShouldBe("CapabilityStatement");
        cs["fhirVersion"]!.GetValue<string>().ShouldBe("5.0.0");
        cs["status"]!.GetValue<string>().ShouldBe("active");

        JsonObject rest0 = cs["rest"]![0]!.AsObject();
        rest0["mode"]!.GetValue<string>().ShouldBe("server");

        JsonObject patient = GetPatientResourceComponent(rest0);

        List<string> interactions = patient["interaction"]!.AsArray()
            .Select(i => i!["code"]!.GetValue<string>())
            .ToList();
        interactions.ShouldContain("read");

        List<string> searchParams = patient["searchParam"]!.AsArray()
            .Select(sp => sp!["name"]!.GetValue<string>())
            .ToList();
        searchParams.ShouldContain("birthdate");
    }
}
