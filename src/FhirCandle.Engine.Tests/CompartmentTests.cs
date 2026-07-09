using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FhirCandle.Compartments;
using FhirCandle.Models;
using FhirCandle.Serialization;
using FhirCandle.Storage;
using Ignixa.Abstractions;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class CompartmentTests
{
    private static readonly IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private static string ReadCompartmentDefinitionPatientJson([CallerFilePath] string sourceFilePath = "")
    {
        string testProjectDir = Path.GetDirectoryName(sourceFilePath)!;
        string path = Path.Combine(testProjectDir, "..", "fhir-candle.Tests", "data", "r4", "CompartmentDefinition-patient.json");
        return File.ReadAllText(path);
    }

    [Fact]
    public void GetCompartments_R4_PatientCompartmentObservationHasSubjectAndPerformer()
    {
        List<ParsedCompartment> compartments = CoreCompartmentSource.GetCompartments(FhirVersion.R4).ToList();

        ParsedCompartment patientCompartment = compartments.Single(c => c.CompartmentType == "Patient");

        patientCompartment.IncludedResources.ShouldContainKey("Observation");
        ParsedCompartment.IncludedResource observation = patientCompartment.IncludedResources["Observation"];
        observation.SearchParamCodes.ShouldContain("subject");
        observation.SearchParamCodes.ShouldContain("performer");
    }

    [Fact]
    public void ParsedCompartment_FromCompartmentDefinitionPatientAsset_MatchesCoreShape()
    {
        string json = ReadCompartmentDefinitionPatientJson();

        SerializationUtils.TryDeserializeFhir(json, "json", out var resource, out string exMessage, R4)
            .ShouldBe(HttpStatusCode.OK, exMessage);

        var parsed = new ParsedCompartment(resource!.ToElement(R4));

        parsed.CompartmentType.ShouldBe("Patient");
        parsed.IncludedResources.ShouldContainKey("Observation");
        parsed.IncludedResources["Observation"].SearchParamCodes.ShouldContain("subject");
        parsed.IncludedResources["Observation"].SearchParamCodes.ShouldContain("performer");

        // matches the shape produced by the core (Ignixa-manager-backed) source for the same compartment
        ParsedCompartment coreEquivalent = CoreCompartmentSource.GetCompartments(FhirVersion.R4)
            .Single(c => c.CompartmentType == "Patient");
        parsed.IncludedResources["Observation"].SearchParamCodes.OrderBy(c => c)
            .ShouldBe(coreEquivalent.IncludedResources["Observation"].SearchParamCodes.OrderBy(c => c));
    }

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

    private static void CreatePatient(VersionedFhirStore store, string id)
    {
        bool ok = store.InstanceUpdate(
            Ctx(store, "PUT", $"Patient/{id}", $$"""{"resourceType":"Patient","id":"{{id}}"}"""),
            out FhirResponseContext response);
        ok.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static void CreateObservation(VersionedFhirStore store, string id, string subjectReference)
    {
        bool ok = store.InstanceUpdate(
            Ctx(store, "PUT", $"Observation/{id}", $$"""
                {
                  "resourceType": "Observation",
                  "id": "{{id}}",
                  "status": "final",
                  "code": {"coding": [{"system": "http://loinc.org", "code": "8480-6"}]},
                  "subject": {"reference": "{{subjectReference}}"}
                }
                """),
            out FhirResponseContext response);
        ok.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public void CompartmentTypeSearch_PatientObservation_ReturnsOnlyMatchingPatientsObservations()
    {
        VersionedFhirStore store = CreateStore();

        CreatePatient(store, "pat-1");
        CreatePatient(store, "pat-2");
        CreateObservation(store, "obs-1", "Patient/pat-1");
        CreateObservation(store, "obs-2", "Patient/pat-2");

        bool success = store.CompartmentTypeSearch(
            Ctx(store, "GET", "Patient/pat-1/Observation"),
            out FhirResponseContext response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonNode bundle = JsonNode.Parse(response.SerializedResource)!;
        bundle["total"]!.GetValue<int>().ShouldBe(1);
        bundle["entry"]![0]!["resource"]!["id"]!.GetValue<string>().ShouldBe("obs-1");
    }
}
