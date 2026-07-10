using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Storage;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

/// <summary>
/// Tests for the R4 IG extras ported in Task 15: the DaVinci CDex Task-process interaction hook and
/// the DaVinci PAS <c>$submit</c>/<c>$inquire</c> operations, including their runtime version/package
/// registration gating.
/// </summary>
public class IgExtrasTests : IDisposable
{
    private const string CdexPackageDirective = "hl7.fhir.us.davinci-cdex#2.0.0";
    private const string PasPackageDirective = "hl7.fhir.us.davinci-pas#2.0.1";

    private readonly string _emptyPackageDir;

    public IgExtrasTests()
    {
        _emptyPackageDir = Path.Combine(Path.GetTempPath(), $"candle-ig-extras-{Guid.NewGuid()}");
        Directory.CreateDirectory(_emptyPackageDir);
    }

    public void Dispose() => Directory.Delete(_emptyPackageDir, recursive: true);

    private static VersionedFhirStore CreateStore(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var store = new VersionedFhirStore();
        store.Init(new TenantConfiguration
        {
            FhirVersion = fhirVersion,
            ControllerName = "test",
            BaseUrl = "http://localhost/fhir/test",
        });
        return store;
    }

    private VersionedFhirStore CreateStoreWithPackage(
        FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion,
        string packageDirective)
    {
        VersionedFhirStore store = CreateStore(fhirVersion);

        // Registration gates key off the loaded directive/package id, not package content, so an
        // empty directory is enough to satisfy a RequiresPackage check in a unit test.
        store.LoadPackage(packageDirective, _emptyPackageDir, string.Empty, includeExamples: false);
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

    private const string TriggerTaskJson = """
        {
          "resourceType": "Task",
          "status": "requested",
          "intent": "order",
          "input": [
            {
              "type": {
                "coding": [
                  { "system": "http://hl7.org/fhir/us/davinci-hrex/CodeSystem/hrex-temp", "code": "data-query" }
                ]
              },
              "valueString": "Patient?family=Cdex"
            }
          ]
        }
        """;

    [Fact]
    public void CDexHook_TriggerTaskOnR4_CompletesTaskWithContainedResults()
    {
        VersionedFhirStore store = CreateStoreWithPackage(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, CdexPackageDirective);
        PutResource(store, "Patient", "pat-cdex", """{"resourceType":"Patient","id":"pat-cdex","name":[{"family":"Cdex"}]}""");

        bool success = store.InstanceCreate(Ctx(store, "POST", "Task", TriggerTaskJson), out FhirResponseContext response);

        success.ShouldBeTrue();

        // The hook completes the request itself, so the create returns the hook's 200 rather than 201.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode task = JsonNode.Parse(response.SerializedResource)!;
        task["resourceType"]!.GetValue<string>().ShouldBe("Task");
        task["status"]!.GetValue<string>().ShouldBe("completed");

        JsonArray contained = task["contained"]!.AsArray();
        contained.Count.ShouldBe(1);
        contained[0]!["resourceType"]!.GetValue<string>().ShouldBe("Bundle");
        string containedId = contained[0]!["id"]!.GetValue<string>();

        JsonArray output = task["output"]!.AsArray();
        output.Count.ShouldBe(1);
        output[0]!["valueReference"]!["reference"]!.GetValue<string>().ShouldBe($"#{containedId}");

        JsonArray containedEntries = contained[0]!["entry"]!.AsArray();
        containedEntries.Count.ShouldBe(1);
        containedEntries[0]!["resource"]!["resourceType"]!.GetValue<string>().ShouldBe("Patient");
        containedEntries[0]!["resource"]!["id"]!.GetValue<string>().ShouldBe("pat-cdex");

        // The completed task was persisted, not just returned.
        string taskId = task["id"]!.GetValue<string>();
        bool readOk = store.InstanceRead(Ctx(store, "GET", $"Task/{taskId}"), out FhirResponseContext readResponse);
        readOk.ShouldBeTrue();
        JsonNode storedTask = JsonNode.Parse(readResponse.SerializedResource)!;
        storedTask["status"]!.GetValue<string>().ShouldBe("completed");
    }

    [Fact]
    public void CDexHook_NonMatchingTaskOnR4_IsStoredUnchanged()
    {
        VersionedFhirStore store = CreateStoreWithPackage(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, CdexPackageDirective);

        string draftTask = TriggerTaskJson.Replace("\"requested\"", "\"draft\"");
        bool success = store.InstanceCreate(Ctx(store, "POST", "Task", draftTask), out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        JsonNode task = JsonNode.Parse(response.SerializedResource)!;
        task["status"]!.GetValue<string>().ShouldBe("draft");
        (task["contained"] is null).ShouldBeTrue();
        (task["output"] is null).ShouldBeTrue();
    }

    [Fact]
    public void CDexHook_TriggerTaskOnR5_DoesNotFire()
    {
        VersionedFhirStore store = CreateStoreWithPackage(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, CdexPackageDirective);

        bool success = store.InstanceCreate(Ctx(store, "POST", "Task", TriggerTaskJson), out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        JsonNode task = JsonNode.Parse(response.SerializedResource)!;
        task["status"]!.GetValue<string>().ShouldBe("requested");
        (task["contained"] is null).ShouldBeTrue();
        (task["output"] is null).ShouldBeTrue();
    }

    private const string PasRequestBundleJson = """
        {
          "resourceType": "Bundle",
          "identifier": { "system": "http://example.org/SUBMITTER_TRANSACTION_IDENTIFIER", "value": "B56789" },
          "type": "collection",
          "entry": [
            {
              "fullUrl": "http://example.org/fhir/Claim/claim-01",
              "resource": {
                "resourceType": "Claim",
                "id": "claim-01",
                "identifier": [ { "system": "http://example.org/PATIENT_EVENT_TRACE_NUMBER", "value": "111099" } ],
                "status": "active",
                "type": { "coding": [ { "system": "http://terminology.hl7.org/CodeSystem/claim-type", "code": "professional" } ] },
                "use": "preauthorization",
                "patient": { "reference": "Patient/pat-pas" },
                "created": "2024-07-20T11:01:00+05:00",
                "insurer": { "reference": "Organization/insurer-pas" },
                "provider": { "reference": "Organization/umo-pas" },
                "priority": { "coding": [ { "system": "http://terminology.hl7.org/CodeSystem/processpriority", "code": "normal" } ] },
                "insurance": [ { "sequence": 1, "focal": true, "coverage": { "reference": "Coverage/cov-pas" } } ],
                "item": [
                  {
                    "sequence": 1,
                    "productOrService": { "coding": [ { "system": "http://example.org/services", "code": "G0154" } ] }
                  }
                ]
              }
            },
            {
              "fullUrl": "http://example.org/fhir/Patient/pat-pas",
              "resource": { "resourceType": "Patient", "id": "pat-pas" }
            },
            {
              "fullUrl": "http://example.org/fhir/Organization/umo-pas",
              "resource": { "resourceType": "Organization", "id": "umo-pas", "name": "UMO" }
            },
            {
              "fullUrl": "http://example.org/fhir/Coverage/cov-pas",
              "resource": {
                "resourceType": "Coverage",
                "id": "cov-pas",
                "status": "active",
                "beneficiary": { "reference": "Patient/pat-pas" },
                "payor": [ { "reference": "Organization/insurer-pas" } ]
              }
            }
          ]
        }
        """;

    [Fact]
    public void PasClaimSubmit_MinimalRequestBundle_ReturnsClaimResponseBundle()
    {
        VersionedFhirStore store = CreateStoreWithPackage(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, PasPackageDirective);

        bool success = store.TypeOperation(Ctx(store, "POST", "Claim/$submit", PasRequestBundleJson), out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        bundle["resourceType"]!.GetValue<string>().ShouldBe("Bundle");
        bundle["type"]!.GetValue<string>().ShouldBe("collection");
        bundle["identifier"]!["value"]!.GetValue<string>().ShouldBe("B56789");

        JsonArray entries = bundle["entry"]!.AsArray();
        JsonNode claimResponse = entries
            .Select(e => e!["resource"]!)
            .Single(r => r["resourceType"]!.GetValue<string>() == "ClaimResponse");

        claimResponse["outcome"]!.GetValue<string>().ShouldBe("queued");
        claimResponse["use"]!.GetValue<string>().ShouldBe("preauthorization");
        claimResponse["request"]!["reference"]!.GetValue<string>().ShouldStartWith("Claim/");

        // Organization, Patient, and Coverage entries are copied through; the Claim itself is not.
        entries.Count(e => e!["resource"]!["resourceType"]!.GetValue<string>() == "Patient").ShouldBe(1);
        entries.Count(e => e!["resource"]!["resourceType"]!.GetValue<string>() == "Organization").ShouldBe(1);
        entries.Count(e => e!["resource"]!["resourceType"]!.GetValue<string>() == "Coverage").ShouldBe(1);
        entries.Count(e => e!["resource"]!["resourceType"]!.GetValue<string>() == "Claim").ShouldBe(0);
    }

    [Fact]
    public void PasClaimInquiry_RequestBundle_ReturnsCompleteClaimResponseBundle()
    {
        VersionedFhirStore store = CreateStoreWithPackage(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, PasPackageDirective);

        bool success = store.TypeOperation(Ctx(store, "POST", "Claim/$inquire", PasRequestBundleJson), out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        bundle["resourceType"]!.GetValue<string>().ShouldBe("Bundle");
        bundle["type"]!.GetValue<string>().ShouldBe("collection");

        JsonNode claimResponse = bundle["entry"]!.AsArray()
            .Select(e => e!["resource"]!)
            .Single(r => r["resourceType"]!.GetValue<string>() == "ClaimResponse");

        claimResponse["outcome"]!.GetValue<string>().ShouldBe("complete");
        claimResponse["request"]!["reference"]!.GetValue<string>().ShouldBe("Claim/claim-01");
        claimResponse["item"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void PasOperations_OnR5Store_AreNotRegistered()
    {
        VersionedFhirStore store = CreateStoreWithPackage(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, PasPackageDirective);

        bool success = store.TypeOperation(Ctx(store, "POST", "Claim/$submit", PasRequestBundleJson), out FhirResponseContext response);

        success.ShouldBeFalse();
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void PasOperations_WithoutPackage_AreNotRegistered()
    {
        VersionedFhirStore store = CreateStore(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

        bool success = store.TypeOperation(Ctx(store, "POST", "Claim/$submit", PasRequestBundleJson), out FhirResponseContext response);

        success.ShouldBeFalse();
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
