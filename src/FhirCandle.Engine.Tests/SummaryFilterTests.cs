using FhirCandle.Serialization;
using Ignixa.Abstractions;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class SummaryFilterTests
{
    private static readonly IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private const string PatientJson =
        """
        {
          "resourceType": "Patient",
          "id": "p1",
          "text": {"status": "generated", "div": "<div xmlns=\"http://www.w3.org/1999/xhtml\">ok</div>"},
          "name": [{"family": "Chalmers", "given": ["Peter"]}],
          "photo": [{"contentType": "image/jpeg", "data": "cGhvdG8="}]
        }
        """;

    private static ResourceJsonNode ParsePatient() =>
        SerializationUtils.TryDeserializeFhir(PatientJson, "json", out ResourceJsonNode? r, out _) switch
        {
            System.Net.HttpStatusCode.OK => r!,
            _ => throw new InvalidOperationException("Failed to parse test Patient"),
        };

    [Fact]
    public void Apply_True_KeepsSummaryElements_RemovesNonSummary_AddsSubsettedTag()
    {
        ResourceJsonNode patient = ParsePatient();

        ResourceJsonNode result = SummaryFilter.Apply(patient, R4, "true");
        string json = result.SerializeToString();

        json.ShouldContain("\"name\"");
        json.ShouldNotContain("\"photo\"");
        json.ShouldContain("\"SUBSETTED\"");
        json.ShouldContain("http://terminology.hl7.org/CodeSystem/v3-ObservationValue");

        // original must be untouched
        patient.SerializeToString().ShouldContain("\"photo\"");
    }

    [Fact]
    public void Apply_Text_KeepsOnlyTextIdAndMeta()
    {
        ResourceJsonNode patient = ParsePatient();

        ResourceJsonNode result = SummaryFilter.Apply(patient, R4, "text");
        string json = result.SerializeToString();

        json.ShouldContain("\"text\"");
        json.ShouldContain("\"id\"");
        json.ShouldContain("\"meta\"");
        json.ShouldNotContain("\"name\"");
        json.ShouldNotContain("\"photo\"");

        patient.SerializeToString().ShouldContain("\"photo\"");
    }

    [Fact]
    public void Apply_Data_RemovesText()
    {
        ResourceJsonNode patient = ParsePatient();

        ResourceJsonNode result = SummaryFilter.Apply(patient, R4, "data");
        string json = result.SerializeToString();

        json.ShouldNotContain("\"text\"");
        json.ShouldContain("\"name\"");
        json.ShouldContain("\"photo\"");

        patient.SerializeToString().ShouldContain("\"photo\"");
    }

    [Fact]
    public void Apply_OriginalResourceUnmutated_AfterAllModes()
    {
        ResourceJsonNode patient = ParsePatient();

        SummaryFilter.Apply(patient, R4, "true");
        SummaryFilter.Apply(patient, R4, "text");
        SummaryFilter.Apply(patient, R4, "data");

        string json = patient.SerializeToString();
        json.ShouldContain("\"photo\"");
        json.ShouldContain("\"text\"");
        json.ShouldContain("\"name\"");
        json.ShouldNotContain("\"SUBSETTED\"");
    }
}
