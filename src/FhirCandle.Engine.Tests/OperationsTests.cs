using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Storage;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class OperationsTests
{
    private static VersionedFhirStore CreateStore()
    {
        var store = new VersionedFhirStore();
        store.Init(new TenantConfiguration
        {
            FhirVersion = FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4,
            ControllerName = "test",
            BaseUrl = "http://localhost/fhir/test",
        });
        return store;
    }

    private static FhirRequestContext Ctx(VersionedFhirStore store, string httpMethod, string url, string? sourceContent = null) => new()
    {
        TenantName = "test",
        Store = store,
        HttpMethod = httpMethod,
        Url = url,
        Authorization = null,
        SourceContent = sourceContent ?? string.Empty,
        SourceFormat = sourceContent is null ? string.Empty : "application/fhir+json",
    };

    private static void PutResource(VersionedFhirStore store, string resourceType, string id, string json)
    {
        bool ok = store.InstanceUpdate(Ctx(store, "PUT", $"{resourceType}/{id}", json), out FhirResponseContext response);
        ok.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public void Validate_ValidPatient_ReturnsInformationalIssueAndNoErrors()
    {
        VersionedFhirStore store = CreateStore();

        bool success = store.TypeOperation(
            Ctx(store, "POST", "Patient/$validate", """{"resourceType":"Patient","id":"pat-1","birthDate":"1990-01-01"}"""),
            out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode outcome = JsonNode.Parse(response.SerializedOutcome)!;
        outcome["resourceType"]!.GetValue<string>().ShouldBe("OperationOutcome");

        JsonArray issues = outcome["issue"]!.AsArray();
        issues.Any(i => i!["severity"]!.GetValue<string>() == "information").ShouldBeTrue();
        issues.Any(i => i!["severity"]!.GetValue<string>() is "error" or "fatal").ShouldBeFalse();
    }

    [Fact]
    public void Validate_PatientWithInvalidBirthDate_ReturnsErrorSeverityIssue()
    {
        VersionedFhirStore store = CreateStore();

        bool success = store.TypeOperation(
            Ctx(store, "POST", "Patient/$validate", """{"resourceType":"Patient","id":"pat-2","birthDate":"not-a-date"}"""),
            out FhirResponseContext response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode outcome = JsonNode.Parse(response.SerializedOutcome)!;
        JsonArray issues = outcome["issue"]!.AsArray();
        issues.Any(i => i!["severity"]!.GetValue<string>() is "error" or "fatal").ShouldBeTrue();
    }

    [Fact]
    public void ResetStore_ClearsAndReloadsNonProtectedResources()
    {
        VersionedFhirStore store = CreateStore();
        PutResource(store, "Patient", "pat-1", """{"resourceType":"Patient","id":"pat-1"}""");

        bool success = store.SystemOperation(
            Ctx(store, "POST", "$reset-store"),
            out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        bool readSuccess = store.InstanceRead(Ctx(store, "GET", "Patient/pat-1"), out FhirResponseContext readResponse);
        readSuccess.ShouldBeFalse();
        readResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void SubscriptionEvents_ExistingSubscription_ReturnsHistoryBundleShape()
    {
        VersionedFhirStore store = CreateStore();
        PutResource(store, "Subscription", "sub-1", """
            {
              "resourceType": "Subscription",
              "id": "sub-1",
              "status": "active",
              "reason": "test",
              "criteria": "Patient",
              "channel": { "type": "rest-hook", "endpoint": "http://example.org/hook" }
            }
            """);

        bool success = store.InstanceOperation(
            Ctx(store, "GET", "Subscription/sub-1/$events"),
            out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        bundle["resourceType"]!.GetValue<string>().ShouldBe("Bundle");
        bundle["type"]!.GetValue<string>().ShouldBe("history");
    }
}
