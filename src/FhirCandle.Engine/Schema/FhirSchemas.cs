using Ignixa.Abstractions;
using Ignixa.Specification.Extensions;
using FhirCandle.Utils;

namespace FhirCandle.Schema;

public static class FhirSchemas
{
    private static readonly Dictionary<FhirVersion, IFhirSchemaProvider> _providers =
        new()
        {
            [FhirVersion.R4] = FhirVersion.R4.GetSchemaProvider(),
            [FhirVersion.R4B] = FhirVersion.R4B.GetSchemaProvider(),
            [FhirVersion.R5] = FhirVersion.R5.GetSchemaProvider(),
        };

    public static FhirVersion ToIgnixaVersion(FhirReleases.FhirSequenceCodes sequence) => sequence switch
    {
        FhirReleases.FhirSequenceCodes.R4 => FhirVersion.R4,
        FhirReleases.FhirSequenceCodes.R4B => FhirVersion.R4B,
        FhirReleases.FhirSequenceCodes.R5 => FhirVersion.R5,
        _ => throw new NotSupportedException($"FHIR sequence {sequence} is not supported."),
    };

    public static IFhirSchemaProvider Get(FhirReleases.FhirSequenceCodes sequence) =>
        _providers[ToIgnixaVersion(sequence)];

    public static IFhirSchemaProvider GetByIgnixaVersion(FhirVersion version) => _providers[version];
}
