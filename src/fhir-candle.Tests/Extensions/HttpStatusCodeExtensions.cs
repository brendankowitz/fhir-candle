using System.Net;

namespace fhir.candle.Tests.Extensions;

/// <summary>
/// Restores the 2xx success check previously provided by the REST SDK,
/// now that it has been removed.
/// </summary>
public static class HttpStatusCodeExtensions
{
    public static bool IsSuccessful(this HttpStatusCode code) =>
        (int)code >= 200 && (int)code < 300;
}
