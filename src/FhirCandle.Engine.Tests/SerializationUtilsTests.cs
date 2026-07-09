using System.Net;
using FhirCandle.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class SerializationUtilsTests
{
    [Fact]
    public void TryDeserializeFhir_ValidJson_ReturnsOk()
    {
        var sc = SerializationUtils.TryDeserializeFhir(
            """{"resourceType":"Patient","id":"p1"}""", "application/fhir+json",
            out ResourceJsonNode? r, out _);
        sc.ShouldBe(HttpStatusCode.OK);
        r!.ResourceType.ShouldBe("Patient");
        r.Id.ShouldBe("p1");
    }

    [Fact]
    public void TryDeserializeFhir_Garbage_ReturnsUnsupportedMediaTypeMessage()
    {
        var sc = SerializationUtils.TryDeserializeFhir("not fhir", "application/fhir+json", out var r, out string msg);
        sc.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        r.ShouldBeNull();
        msg.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void BuildOutcome_ProducesIssueWithSeverityAndCode()
    {
        OperationOutcomeJsonNode oo = SerializationUtils.BuildOutcomeForRequest(
            HttpStatusCode.NotFound, "Resource not found",
            OperationOutcomeJsonNode.IssueType.NotFound);
        oo.ResourceType.ShouldBe("OperationOutcome");
        oo.Issue.Count.ShouldBe(1);
        oo.Issue[0].Code.ShouldBe(OperationOutcomeJsonNode.IssueType.NotFound);
        oo.Issue[0].Diagnostics.ShouldContain("Resource not found");
    }

    [Fact]
    public void SerializeFhir_Json_PrettyAndMinified()
    {
        SerializationUtils.TryDeserializeFhir("""{"resourceType":"Patient","id":"p1"}""",
            "json", out var r, out _);
        var schema = FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);
        SerializationUtils.SerializeFhir(r!, schema, "application/fhir+json", pretty: false)
            .ShouldBe("""{"resourceType":"Patient","id":"p1"}""");
        SerializationUtils.SerializeFhir(r!, schema, "json", pretty: true).ShouldContain("\n");
    }
}
