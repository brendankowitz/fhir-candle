using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Storage;
using FhirCandle.Utils;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class BundleProcessingTests
{
    private static VersionedFhirStore CreateStore()
    {
        var store = new VersionedFhirStore();
        store.Init(new TenantConfiguration
        {
            FhirVersion = FhirReleases.FhirSequenceCodes.R4,
            ControllerName = "test",
            BaseUrl = "http://localhost/fhir/test",
            AllowExistingId = true,
            AllowCreateAsUpdate = true,
        });
        return store;
    }

    private static FhirRequestContext Ctx(VersionedFhirStore store, string httpMethod, string url, string sourceContent) => new()
    {
        TenantName = "test",
        Store = store,
        HttpMethod = httpMethod,
        Url = url,
        Authorization = null,
        SourceContent = sourceContent,
        SourceFormat = "application/fhir+json",
    };

    /// <summary>
    /// Documents that <c>MutableJsonList&lt;BundleComponentJsonNode&gt;</c>'s indexer/enumerator do
    /// NOT throw in practice, despite the type-mismatch theory flagged by Task 8 (reflection-based
    /// factory lookup for <c>(JsonObject, FhirVersion)</c> vs. the real <c>(JsonObject, FhirVersion?)</c>
    /// constructor). Empirically, <see cref="Type.GetConstructor(Type[])"/> resolves a formal
    /// <c>Nullable&lt;FhirVersion&gt;</c> parameter against a requested <c>typeof(FhirVersion)</c> argument
    /// type as a match (a documented .NET reflection default-binder behavior for <c>Nullable&lt;T&gt;</c>
    /// value-type parameters) - confirmed directly below via reflection, and via successful round-trip
    /// indexing/enumeration/LINQ over a reparsed bundle. This means <see cref="VersionedFhirStore"/>'s
    /// bundle processing can use the typed <c>BundleJsonNode.Entry</c> list directly (foreach/LINQ),
    /// no raw <see cref="JsonArray"/> workaround needed.
    /// </summary>
    [Fact]
    public void MutableJsonListIndexerAndEnumerator_DoNotThrow()
    {
        Ignixa.Abstractions.FhirVersion nonNullable = default;
        typeof(BundleComponentJsonNode)
            .GetConstructor(new[] { typeof(JsonObject), nonNullable.GetType() })
            .ShouldNotBeNull("Type.GetConstructor(Type[]) resolves FhirVersion? via the non-nullable FhirVersion Type token");

        var bundle = new BundleJsonNode
        {
            Id = "workaround-proof",
            Type = BundleJsonNode.BundleType.Collection,
        };

        bundle.Entry.Add(new BundleComponentJsonNode
        {
            FullUrl = "urn:uuid:aaaaaaaa-0000-0000-0000-000000000001",
            Resource = ResourceJsonNode.Parse("""{"resourceType":"Patient","id":"a"}"""),
        });

        bundle.Entry.Add(new BundleComponentJsonNode
        {
            FullUrl = "urn:uuid:bbbbbbbb-0000-0000-0000-000000000002",
            Resource = ResourceJsonNode.Parse("""{"resourceType":"Patient","id":"b"}"""),
        });

        string json = bundle.SerializeToString();

        ResourceJsonNode reparsedGeneric = JsonSourceNodeFactory.Parse(json);
        var reparsedBundle = new BundleJsonNode(reparsedGeneric.MutableNode, reparsedGeneric.FhirVersion);

        // Indexer
        reparsedBundle.Entry[0].FullUrl.ShouldBe("urn:uuid:aaaaaaaa-0000-0000-0000-000000000001");
        reparsedBundle.Entry[0].Resource.Id.ShouldBe("a");

        // Enumerator / foreach / LINQ (.ToList(), .Count())
        List<BundleComponentJsonNode> viaLinq = reparsedBundle.Entry.ToList();
        viaLinq.Count.ShouldBe(2);
        viaLinq[1].FullUrl.ShouldBe("urn:uuid:bbbbbbbb-0000-0000-0000-000000000002");
        viaLinq[1].Resource.Id.ShouldBe("b");
    }

    /// <summary>
    /// Issue #26: transaction response bundle entries must carry a formatted status code
    /// (e.g. "201 Created"), not a bare number.
    /// </summary>
    [Fact]
    public void TransactionResponseStatus_IsFormattedCode()
    {
        VersionedFhirStore store = CreateStore();

        string json = """
            {
              "resourceType": "Bundle",
              "type": "transaction",
              "entry": [
                {
                  "request": { "method": "POST", "url": "Patient" },
                  "resource": { "resourceType": "Patient", "id": "example" }
                }
              ]
            }
            """;

        bool success = store.ProcessBundle(Ctx(store, "POST", store.Config.BaseUrl, json), out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.SerializedResource.ShouldNotBeNullOrEmpty();

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        bundle["type"]!.GetValue<string>().ShouldBe("transaction-response");

        JsonNode entry = bundle["entry"]![0]!;
        entry["response"]!["status"]!.GetValue<string>().ShouldBe("201 Created");
    }

    [Fact]
    public void Transaction_RewritesFullUrlReferenceToNewId()
    {
        VersionedFhirStore store = CreateStore();

        string json = """
            {
              "resourceType": "Bundle",
              "type": "transaction",
              "entry": [
                {
                  "fullUrl": "urn:uuid:11111111-1111-1111-1111-111111111111",
                  "request": { "method": "POST", "url": "Patient" },
                  "resource": { "resourceType": "Patient" }
                },
                {
                  "request": { "method": "POST", "url": "Observation" },
                  "resource": {
                    "resourceType": "Observation",
                    "status": "final",
                    "subject": { "reference": "urn:uuid:11111111-1111-1111-1111-111111111111" }
                  }
                }
              ]
            }
            """;

        bool success = store.ProcessBundle(Ctx(store, "POST", store.Config.BaseUrl, json), out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        bundle["type"]!.GetValue<string>().ShouldBe("transaction-response");

        JsonNode entry0 = bundle["entry"]![0]!;
        JsonNode entry1 = bundle["entry"]![1]!;

        entry0["response"]!["status"]!.GetValue<string>().ShouldBe("201 Created");
        entry1["response"]!["status"]!.GetValue<string>().ShouldBe("201 Created");

        string newPatientId = entry0["response"]!["location"]!.GetValue<string>().Split('/')[^1];

        // stored entry-2's reference must be rewritten to "Patient/{newId}"
        bool readOk = store.InstanceRead(
            new FhirRequestContext
            {
                TenantName = "test",
                Store = store,
                HttpMethod = "GET",
                Url = $"{store.Config.BaseUrl}/Observation/{entry1["response"]!["location"]!.GetValue<string>().Split('/')[^1]}",
                Authorization = null,
            },
            out FhirResponseContext obsResponse);

        readOk.ShouldBeTrue();
        JsonNode storedObs = JsonNode.Parse(obsResponse.SerializedResource)!;
        storedObs["subject"]!["reference"]!.GetValue<string>().ShouldBe($"Patient/{newPatientId}");
    }

    [Fact]
    public void Batch_OneBadEntry_OthersSucceed()
    {
        VersionedFhirStore store = CreateStore();

        string json = """
            {
              "resourceType": "Bundle",
              "type": "batch",
              "entry": [
                {
                  "request": { "method": "POST", "url": "Patient" },
                  "resource": { "resourceType": "Patient", "active": true }
                },
                {
                  "request": { "method": "POST", "url": "Patient" },
                  "resource": { "resourceType": "Observation", "status": "final" }
                },
                {
                  "request": { "method": "POST", "url": "Patient" },
                  "resource": { "resourceType": "Patient", "active": false }
                }
              ]
            }
            """;

        bool success = store.ProcessBundle(Ctx(store, "POST", store.Config.BaseUrl, json), out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        bundle["type"]!.GetValue<string>().ShouldBe("batch-response");

        JsonArray entries = bundle["entry"]!.AsArray();
        entries.Count.ShouldBe(3);

        entries[0]!["response"]!["status"]!.GetValue<string>().ShouldBe("201 Created");
        entries[1]!["response"]!["status"]!.GetValue<string>().ShouldStartWith("422");
        entries[2]!["response"]!["status"]!.GetValue<string>().ShouldBe("201 Created");
    }

    [Fact]
    public void Batch_ConditionalCreateViaIfNoneExist_Honored()
    {
        VersionedFhirStore store = CreateStore();

        bool seedOk = store.InstanceCreate(
            Ctx(store, "POST", "Organization", """{"resourceType":"Organization","name":"Acme"}"""),
            out FhirResponseContext seedResponse);
        seedOk.ShouldBeTrue();
        string existingId = seedResponse.Id;

        string json = """
            {
              "resourceType": "Bundle",
              "type": "batch",
              "entry": [
                {
                  "request": { "method": "POST", "url": "Organization", "ifNoneExist": "name=Acme" },
                  "resource": { "resourceType": "Organization", "name": "Acme" }
                }
              ]
            }
            """;

        bool success = store.ProcessBundle(Ctx(store, "POST", store.Config.BaseUrl, json), out FhirResponseContext response);

        success.ShouldBeTrue();

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        JsonNode entry = bundle["entry"]![0]!;

        entry["response"]!["status"]!.GetValue<string>().ShouldBe("200 OK");
        entry["response"]!["location"]!.GetValue<string>().ShouldBe($"{store.Config.BaseUrl}/Organization/{existingId}");

        // no second Organization was created
        bool searchOk = store.TypeSearch(Ctx(store, "GET", "Organization", string.Empty), out FhirResponseContext searchResponse);
        searchOk.ShouldBeTrue();
        JsonNode searchBundle = JsonNode.Parse(searchResponse.SerializedResource)!;
        searchBundle["total"]!.GetValue<int>().ShouldBe(1);
    }
}
