using System.Net;

namespace fhir.candle.Tests.Extensions;

/// <summary>
/// Restores the 2xx success check previously provided by the Firely
/// <c>Hl7.Fhir.Rest</c> extension of the same name, now that the SDK is gone.
/// </summary>
public static class HttpStatusCodeExtensions
{
    public static bool IsSuccessful(this HttpStatusCode code) =>
        (int)code >= 200 && (int)code < 300;
}
