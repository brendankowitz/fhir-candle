using System.Net;
using Ignixa.Abstractions;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Serialization;

public static class SerializationUtils
{
    public static HttpStatusCode TryDeserializeFhir(
        string content, string format, out ResourceJsonNode? resource, out string exMessage)
    {
        exMessage = string.Empty;
        switch (SniffFormat(format, content))
        {
            case "json":
                try
                {
                    resource = JsonSourceNodeFactory.Parse(content);
                    return string.IsNullOrEmpty(resource.ResourceType)
                        ? Fail(out resource, out exMessage, "Missing resourceType")
                        : HttpStatusCode.OK;
                }
                catch (Exception ex) { return Fail(out resource, out exMessage, ex.Message); }
            case "xml":
                // wired in Task 3 (FhirXml.Parse); until then:
                return Fail(out resource, out exMessage, "XML parsing not yet wired");
            default:
                return Fail(out resource, out exMessage, $"Unsupported format: {format}");
        }
    }

    public static string SerializeFhir(
        ResourceJsonNode instance, IFhirSchemaProvider schema, string format, bool pretty, string summaryFlag = "")
    {
        ResourceJsonNode toSerialize = instance;   // summaryFlag handling added in Task 4
        return SniffFormat(format, "{") switch
        {
            "json" => toSerialize.SerializeToString(pretty),
            "xml" => throw new NotSupportedException("wired in Task 3"),
            _ => toSerialize.SerializeToString(pretty),
        };
    }

    public static OperationOutcomeJsonNode BuildOutcomeForRequest(
        HttpStatusCode sc, string message,
        OperationOutcomeJsonNode.IssueType issueType = OperationOutcomeJsonNode.IssueType.Processing)
    {
        var oo = new OperationOutcomeJsonNode { Id = Guid.NewGuid().ToString() };
        var issue = new OperationOutcomeJsonNode.IssueComponent
        {
            Severity = ((int)sc >= 400)
                ? OperationOutcomeJsonNode.IssueSeverity.Error
                : OperationOutcomeJsonNode.IssueSeverity.Information,
            Code = issueType,
            Diagnostics = $"{message} (HTTP {(int)sc}: {sc})",
        };
        oo.Issue.Add(issue);
        return oo;
    }

    private static HttpStatusCode Fail(out ResourceJsonNode? resource, out string msg, string message)
    {
        resource = null;
        msg = message;
        return HttpStatusCode.UnsupportedMediaType;
    }

    private static string SniffFormat(string format, string content)
    {
        string f = format.Trim().ToLowerInvariant();
        if (f.Contains("json")) return "json";
        if (f.Contains("xml")) return "xml";
        return content.TrimStart().StartsWith('<') ? "xml" : "json";
    }
}
