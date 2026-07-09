using System.Net;
using FhirCandle.Models;
using FhirCandle.Serialization;
using FhirCandle.Storage;
using FhirCandle.Utils;
using Ignixa.Abstractions;
using Ignixa.Serialization.SourceNodes;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class TerminologyTests
{
    private static readonly IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirReleases.FhirSequenceCodes.R4);

    private const string ValueSetComposeJson = """
        {
          "resourceType": "ValueSet",
          "id": "vs-compose",
          "url": "http://example.org/vs/compose-test",
          "status": "active",
          "compose": {
            "include": [
              {
                "system": "http://loinc.org",
                "concept": [
                  { "code": "8480-6", "display": "Systolic blood pressure" },
                  { "code": "8462-4", "display": "Diastolic blood pressure" }
                ]
              }
            ]
          }
        }
        """;

    private const string ValueSetExpansionJson = """
        {
          "resourceType": "ValueSet",
          "id": "vs-expansion",
          "url": "http://example.org/vs/expansion-test",
          "status": "active",
          "expansion": {
            "contains": [
              {
                "system": "http://example.org/cs",
                "code": "parent-1",
                "contains": [
                  { "system": "http://example.org/cs", "code": "child-1" },
                  { "system": "http://example.org/cs", "code": "child-2" }
                ]
              },
              { "system": "http://example.org/cs", "code": "sibling-1" }
            ]
          }
        }
        """;

    private static IElement ParseValueSet(string json)
    {
        SerializationUtils.TryDeserializeFhir(json, "json", out ResourceJsonNode? resource, out string exMessage, R4)
            .ShouldBe(HttpStatusCode.OK, exMessage);
        return resource!.ToElement(R4);
    }

    [Fact]
    public void StoreProcessValueSet_ComposeInclude_VsContainsMatchesMemberOnly()
    {
        var service = new StoreTerminologyService(R4);

        service.StoreProcessValueSet(ParseValueSet(ValueSetComposeJson));

        service.VsContains("http://example.org/vs/compose-test", "http://loinc.org", "8480-6").ShouldBeTrue();
        service.VsContains("http://example.org/vs/compose-test", null, "8480-6").ShouldBeTrue();
        service.VsContains("http://example.org/vs/compose-test", "http://loinc.org", "not-a-member").ShouldBeFalse();
    }

    [Fact]
    public void StoreProcessValueSet_ExpansionContainsWithNested_VsContainsMatchesNestedMembers()
    {
        var service = new StoreTerminologyService(R4);

        service.StoreProcessValueSet(ParseValueSet(ValueSetExpansionJson));

        service.VsContains("http://example.org/vs/expansion-test", "http://example.org/cs", "parent-1").ShouldBeTrue();
        service.VsContains("http://example.org/vs/expansion-test", "http://example.org/cs", "child-1").ShouldBeTrue();
        service.VsContains("http://example.org/vs/expansion-test", "http://example.org/cs", "child-2").ShouldBeTrue();
        service.VsContains("http://example.org/vs/expansion-test", "http://example.org/cs", "sibling-1").ShouldBeTrue();
        service.VsContains("http://example.org/vs/expansion-test", "http://example.org/cs", "not-a-member").ShouldBeFalse();
    }

    [Fact]
    public void StoreProcessValueSet_Remove_VsContainsReportsFalseAfterRemoval()
    {
        var service = new StoreTerminologyService(R4);

        service.StoreProcessValueSet(ParseValueSet(ValueSetComposeJson));
        service.VsContains("http://example.org/vs/compose-test", "http://loinc.org", "8480-6").ShouldBeTrue();

        service.StoreProcessValueSet(ParseValueSet(ValueSetComposeJson), remove: true);

        service.VsContains("http://example.org/vs/compose-test", "http://loinc.org", "8480-6").ShouldBeFalse();
    }

    [Fact]
    public void ValueSetValidateCode_MemberCode_ReturnsResultTrue()
    {
        var service = new StoreTerminologyService(R4);
        service.StoreProcessValueSet(ParseValueSet(ValueSetComposeJson));

        ResourceJsonNode parameters = ResourceJsonNode.Parse("""
            {
              "resourceType": "Parameters",
              "parameter": [
                { "name": "url", "valueUri": "http://example.org/vs/compose-test" },
                { "name": "system", "valueUri": "http://loinc.org" },
                { "name": "code", "valueCode": "8480-6" }
              ]
            }
            """);

        ResourceJsonNode result = service.ValueSetValidateCode(parameters);
        IElement root = result.ToElement(R4);

        result.ResourceType.ShouldBe("Parameters");
        FindParamValue(root, "result").ShouldBe("True");
    }

    [Fact]
    public void ValueSetValidateCode_NonMemberCode_ReturnsResultFalseWithMessage()
    {
        var service = new StoreTerminologyService(R4);
        service.StoreProcessValueSet(ParseValueSet(ValueSetComposeJson));

        ResourceJsonNode parameters = ResourceJsonNode.Parse("""
            {
              "resourceType": "Parameters",
              "parameter": [
                { "name": "url", "valueUri": "http://example.org/vs/compose-test" },
                { "name": "system", "valueUri": "http://loinc.org" },
                { "name": "code", "valueCode": "not-a-real-code" }
              ]
            }
            """);

        ResourceJsonNode result = service.ValueSetValidateCode(parameters);
        IElement root = result.ToElement(R4);

        FindParamValue(root, "result").ShouldBe("False");
        FindParamValue(root, "message").ShouldNotBeNullOrEmpty();
    }

    private static string? FindParamValue(IElement parametersRoot, string name) =>
        parametersRoot.Children("parameter")
            .FirstOrDefault(p => (p.FirstChild("name")?.Value as string) == name)?
            .FirstChild("value")?.Value?.ToString();

    private static VersionedFhirStore CreateStore()
    {
        var store = new VersionedFhirStore();
        store.Init(new TenantConfiguration
        {
            FhirVersion = FhirReleases.FhirSequenceCodes.R4,
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

    [Fact]
    public void TypeSearch_TokenInModifier_MatchesObservationInStoredValueSet()
    {
        VersionedFhirStore store = CreateStore();

        bool vsCreated = store.InstanceCreate(
            Ctx(store, "POST", "ValueSet", ValueSetComposeJson),
            out FhirResponseContext vsResponse);
        vsCreated.ShouldBeTrue();
        vsResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        bool obsCreated = store.InstanceCreate(
            Ctx(store, "POST", "Observation", """
                {
                  "resourceType": "Observation",
                  "status": "final",
                  "code": {
                    "coding": [{"system": "http://loinc.org", "code": "8480-6", "display": "Systolic blood pressure"}]
                  }
                }
                """),
            out FhirResponseContext obsResponse);
        obsCreated.ShouldBeTrue();
        obsResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        bool searchOk = store.TypeSearch(
            Ctx(store, "GET", "Observation?code:in=http://example.org/vs/compose-test"),
            out FhirResponseContext searchResponse);

        searchOk.ShouldBeTrue();
        searchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        System.Text.Json.Nodes.JsonNode bundle = System.Text.Json.Nodes.JsonNode.Parse(searchResponse.SerializedResource)!;
        bundle["total"]!.GetValue<int>().ShouldBe(1);

        bool notInSearchOk = store.TypeSearch(
            Ctx(store, "GET", "Observation?code:not-in=http://example.org/vs/compose-test"),
            out FhirResponseContext notInResponse);

        notInSearchOk.ShouldBeTrue();
        System.Text.Json.Nodes.JsonNode notInBundle = System.Text.Json.Nodes.JsonNode.Parse(notInResponse.SerializedResource)!;
        notInBundle["total"]!.GetValue<int>().ShouldBe(0);
    }
}
