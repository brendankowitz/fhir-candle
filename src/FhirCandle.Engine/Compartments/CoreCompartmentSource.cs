using Ignixa.Abstractions;
using Ignixa.Search.Definition;
using Ignixa.Specification.ValueSets.Normative;

namespace FhirCandle.Compartments;

/// <summary>
/// Adapts <see cref="CompartmentDefinitionManager"/>'s pre-generated core (HL7-defined) compartment
/// definitions - Device, Encounter, Patient, Practitioner, RelatedPerson - into <see cref="ParsedCompartment"/>
/// instances, in the same shape a runtime-POSTed CompartmentDefinition would produce.
/// </summary>
public static class CoreCompartmentSource
{
    /// <summary>Gets the five core compartment definitions for the given FHIR version.</summary>
    public static IEnumerable<ParsedCompartment> GetCompartments(FhirVersion fhirVersion)
    {
        var manager = new CompartmentDefinitionManager(fhirVersion);

        foreach (CompartmentType compartmentType in Enum.GetValues<CompartmentType>())
        {
            if (!manager.TryGetResourceTypes(compartmentType, out HashSet<string> resourceTypes))
            {
                continue;
            }

            string code = compartmentType.ToString();

            var includedResources = new Dictionary<string, ParsedCompartment.IncludedResource>();

            foreach (string resourceType in resourceTypes)
            {
                if (!manager.TryGetSearchParams(resourceType, compartmentType, out HashSet<string> searchParams) ||
                    searchParams.Count == 0)
                {
                    continue;
                }

                includedResources[resourceType] = new ParsedCompartment.IncludedResource
                {
                    ResourceType = resourceType,
                    SearchParamCodes = [.. searchParams],
                };
            }

            yield return new ParsedCompartment(
                url: $"http://hl7.org/fhir/CompartmentDefinition/{code.ToLowerInvariant()}",
                name: $"Base FHIR compartment definition for {code}",
                version: string.Empty,
                compartmentType: code,
                includedResources: includedResources);
        }
    }
}
