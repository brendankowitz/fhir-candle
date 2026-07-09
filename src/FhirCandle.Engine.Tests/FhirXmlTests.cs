using FhirCandle.Serialization;
using Ignixa.Serialization;
using Shouldly;
using System.Xml.Linq;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class FhirXmlTests
{
    private static readonly Ignixa.Abstractions.IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private const string PatientJson =
        """{"resourceType":"Patient","id":"p1","active":true,"name":[{"family":"Chalmers","given":["Peter","James"]}],"birthDate":"1974-12-25","text":{"status":"generated","div":"<div xmlns=\"http://www.w3.org/1999/xhtml\">ok</div>"}}""";

    [Fact]
    public void Serialize_ProducesFhirXmlShape()
    {
        SerializationUtils.TryDeserializeFhir(PatientJson, "json", out var patient, out _);
        string xml = FhirXml.Serialize(patient!, R4);
        XElement root = XElement.Parse(xml);
        XNamespace f = "http://hl7.org/fhir";
        root.Name.ShouldBe(f + "Patient");
        root.Element(f + "id")!.Attribute("value")!.Value.ShouldBe("p1");
        root.Element(f + "active")!.Attribute("value")!.Value.ShouldBe("true");
        root.Element(f + "name")!.Elements(f + "given").Count().ShouldBe(2);
        root.Element(f + "birthDate")!.Attribute("value")!.Value.ShouldBe("1974-12-25");
    }

    [Fact]
    public void RoundTrip_XmlToJsonToXml_IsStable()
    {
        SerializationUtils.TryDeserializeFhir(PatientJson, "json", out var patient, out _);
        string xml1 = FhirXml.Serialize(patient!, R4);
        var reparsed = FhirXml.Parse(xml1, R4);
        reparsed.ResourceType.ShouldBe("Patient");
        reparsed.Id.ShouldBe("p1");
        FhirXml.Serialize(reparsed, R4).ShouldBe(xml1);
    }

    [Fact]
    public void Parse_ContainedResourceAndChoiceType_Work()
    {
        const string obsXml =
            """<Observation xmlns="http://hl7.org/fhir"><id value="o1"/><contained><Patient><id value="cp"/></Patient></contained><status value="final"/><code><coding><system value="http://loinc.org"/><code value="8480-6"/></coding></code><valueQuantity><value value="120"/><unit value="mmHg"/></valueQuantity></Observation>""";
        var obs = FhirXml.Parse(obsXml, R4);
        string json = obs.SerializeToString();
        json.ShouldContain("\"valueQuantity\":{\"value\":120");
        json.ShouldContain("\"contained\":[{\"resourceType\":\"Patient\",\"id\":\"cp\"}]");
        json.ShouldContain("\"status\":\"final\"");
    }
}
