using System.Net;
using FhirCandle.Strict;
using FhirCandle.Utils;
using Ignixa.Abstractions;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Serialization;

public static class SerializationUtils
{
    public static HttpStatusCode TryDeserializeFhir(
        string content, string format, out ResourceJsonNode? resource, out string exMessage,
        IFhirSchemaProvider? schema = null)
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
                ArgumentNullException.ThrowIfNull(schema);
                try
                {
                    resource = FhirXml.Parse(content, schema);
                    return string.IsNullOrEmpty(resource.ResourceType)
                        ? Fail(out resource, out exMessage, "Missing resourceType")
                        : HttpStatusCode.OK;
                }
                catch (Exception ex) { return Fail(out resource, out exMessage, ex.Message); }
            default:
                return Fail(out resource, out exMessage, $"Unsupported format: {format}");
        }
    }

    public static string SerializeFhir(
        ResourceJsonNode instance, IFhirSchemaProvider schema, string format, bool pretty, string summaryFlag = "")
    {
        ResourceJsonNode toSerialize = string.IsNullOrEmpty(summaryFlag) || summaryFlag == "count"
            ? instance
            : SummaryFilter.Apply(instance, schema, summaryFlag);
        return SniffFormat(format, "{") switch
        {
            "json" => toSerialize.SerializeToString(pretty),
            "xml" => FhirXml.Serialize(toSerialize, schema, pretty),
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

    public static OperationOutcomeJsonNode BuildOutcomeForStrictRule(
        HttpStatusCode sc,
        string message,
        StrictRuleCode rule,
        FhirReleases.FhirSequenceCodes version,
        OperationOutcomeJsonNode.IssueType? issueType = null)
    {
        string url = StrictRule.GetSpecUrl(rule, version);
        string diagnostics = string.IsNullOrEmpty(url)
            ? message
            : $"{message} (see {url})";
        return BuildOutcomeForRequest(sc, diagnostics, issueType ?? OperationOutcomeJsonNode.IssueType.Processing);
    }

    public static OperationOutcomeJsonNode BuildOutcomeForStrictRules(
        HttpStatusCode sc,
        IEnumerable<(StrictRuleCode Rule, string Message, OperationOutcomeJsonNode.IssueType IssueType)> issues,
        FhirReleases.FhirSequenceCodes version)
    {
        var components = new List<OperationOutcomeJsonNode.IssueComponent>();
        var severity = ((int)sc < 400)
            ? OperationOutcomeJsonNode.IssueSeverity.Information
            : OperationOutcomeJsonNode.IssueSeverity.Error;

        foreach ((StrictRuleCode rule, string message, OperationOutcomeJsonNode.IssueType issueType) in issues)
        {
            string url = StrictRule.GetSpecUrl(rule, version);
            string diagnostics = string.IsNullOrEmpty(url)
                ? message
                : $"{message} (see {url})";

            components.Add(new OperationOutcomeJsonNode.IssueComponent
            {
                Severity = severity,
                Code = issueType,
                Diagnostics = diagnostics,
            });
        }

        if (components.Count == 0)
        {
            return BuildOutcomeForRequest(sc, "No issues");
        }

        var oo = new OperationOutcomeJsonNode { Id = Guid.NewGuid().ToString() };
        foreach (var component in components)
        {
            oo.Issue.Add(component);
        }
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
