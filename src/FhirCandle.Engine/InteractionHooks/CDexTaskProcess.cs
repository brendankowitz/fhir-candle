// <copyright file="CDexTaskProcess.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Storage;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.InteractionHooks;

/// <summary>
/// DaVinci CDex Task-based data request processing: when a Task in the <c>requested</c>/<c>order</c>
/// state carrying HRex <c>data-query</c> inputs is created or updated, executes each query against
/// this store, attaches the result bundles as contained resources referenced from <c>Task.output</c>,
/// and completes the task.
/// </summary>
public sealed class CDexTaskProcess : IFhirInteractionHook
{
    private const string _hrexTempSystem = "http://hl7.org/fhir/us/davinci-hrex/CodeSystem/hrex-temp";

    /// <inheritdoc/>
    public string Name => "DaVinci CDex Task Process Hook";

    /// <inheritdoc/>
    public string Id => "036a8204-4d4f-46fc-a715-900bc2790a16";

    /// <inheritdoc/>
    public HashSet<FhirCandle.Utils.FhirReleases.FhirSequenceCodes> SupportedFhirVersions => new()
    {
        FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4,
    };

    /// <inheritdoc/>
    public string RequiresPackage => "hl7.fhir.us.davinci-cdex";

    /// <inheritdoc/>
    public Dictionary<string, HashSet<Common.StoreInteractionCodes>> InteractionsByResource => new()
    {
        { "Task", new() {
            Common.StoreInteractionCodes.TypeCreate,
            Common.StoreInteractionCodes.TypeCreateConditional,
            Common.StoreInteractionCodes.InstanceUpdate,
            Common.StoreInteractionCodes.InstanceUpdateConditional
        } },
    };

    /// <inheritdoc/>
    public HashSet<Common.HookRequestStateCodes> HookRequestStates => new()
    {
        Common.HookRequestStateCodes.Post,
    };

    /// <inheritdoc/>
    public bool Enabled { get; set; } = true;

    /// <inheritdoc/>
    public bool DoInteractionHook(
        FhirRequestContext ctx,
        VersionedFhirStore store,
        IVersionedResourceStore? resourceStore,
        ResourceJsonNode? resource,
        out FhirResponseContext hookResponse)
    {
        // filter resources we don't care about
        if (resource is null || resource.ResourceType != "Task")
        {
            hookResponse = FailureResponse(
                OperationOutcomeJsonNode.IssueSeverity.Fatal,
                OperationOutcomeJsonNode.IssueType.Exception,
                $"Invalid resource type ({ctx.ResourceType}) for this hook (expecting Task).");
            return false;
        }

        JsonObject task = resource.MutableNode;

        // filter out tasks that are not: 'requested' & 'order'
        if (GetString(task, "status") != "requested" || GetString(task, "intent") != "order")
        {
            hookResponse = FailureResponse(
                OperationOutcomeJsonNode.IssueSeverity.Information,
                OperationOutcomeJsonNode.IssueType.Informational,
                "Task is not in the 'requested' & 'order' state.");
            return false;
        }

        List<JsonObject> dataQueryInputs = (task["input"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(IsDataQueryInput)
            .ToList();

        if (dataQueryInputs.Count == 0)
        {
            hookResponse = FailureResponse(
                OperationOutcomeJsonNode.IssueSeverity.Information,
                OperationOutcomeJsonNode.IssueType.Informational,
                "No 'data-query' inputs found.");
            return false;
        }

        foreach (JsonObject dataQuery in dataQueryInputs)
        {
            if (!dataQuery.ContainsKey("valueString"))
            {
                string valueKey = dataQuery
                    .Select(kvp => kvp.Key)
                    .FirstOrDefault(k => k.StartsWith("value", StringComparison.Ordinal)) ?? "missing";

                hookResponse = FailureResponse(
                    OperationOutcomeJsonNode.IssueSeverity.Fatal,
                    OperationOutcomeJsonNode.IssueType.Exception,
                    $"Invalid 'data-query' input value type ({valueKey}).");
                return false;
            }

            string? query = GetString(dataQuery, "valueString");

            if (string.IsNullOrEmpty(query))
            {
                hookResponse = FailureResponse(
                    OperationOutcomeJsonNode.IssueSeverity.Fatal,
                    OperationOutcomeJsonNode.IssueType.Exception,
                    "Invalid 'data-query' input value (empty).");
                return false;
            }

            FhirRequestContext queryRequest = new(store, "GET", query);

            if (!store.PerformInteraction(queryRequest, out FhirResponseContext queryResponse, serializeReturn: false) ||
                queryResponse.Resource is not ResourceJsonNode resultBundle ||
                resultBundle.ResourceType != "Bundle")
            {
                hookResponse = FailureResponse(
                    OperationOutcomeJsonNode.IssueSeverity.Fatal,
                    OperationOutcomeJsonNode.IssueType.Exception,
                    $"Error performing query ({query}).");
                return false;
            }

            // ensure this bundle has an ID
            if (string.IsNullOrEmpty(resultBundle.Id))
            {
                resultBundle.Id = Guid.NewGuid().ToString();
            }

            // add this bundle to be contained in our task; clone so it becomes part of the
            // task's own JSON tree rather than re-parenting the (transient) search bundle
            JsonArray contained = GetOrAddArray(task, "contained");
            contained.Add(resultBundle.MutableNode.DeepClone());

            // add results to the task output
            JsonArray output = GetOrAddArray(task, "output");
            output.Add(new JsonObject
            {
                ["type"] = new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject
                    {
                        ["system"] = _hrexTempSystem,
                        ["code"] = "data-query",
                    }),
                },
                ["valueReference"] = new JsonObject { ["reference"] = $"#{resultBundle.Id}" },
            });
        }

        // set the task status to completed
        task["status"] = "completed";

        // update our task
        if (store.InstanceUpdate(new FhirRequestContext(store, "PUT", $"Task/{resource.Id}", resource), out FhirResponseContext opResponse))
        {
            hookResponse = new()
            {
                StatusCode = HttpStatusCode.OK,
                Resource = opResponse.Resource ?? resource,
                Outcome = BuildOutcome(
                    OperationOutcomeJsonNode.IssueSeverity.Information,
                    OperationOutcomeJsonNode.IssueType.Informational,
                    $"Task/{resource.Id} updated."),
            };
            return true;
        }

        hookResponse = FailureResponse(
            OperationOutcomeJsonNode.IssueSeverity.Fatal,
            OperationOutcomeJsonNode.IssueType.Exception,
            $"Error processing Task/{resource.Id}.");
        return false;
    }

    private static bool IsDataQueryInput(JsonObject input) =>
        input["type"]?["coding"] is JsonArray codings &&
        codings.OfType<JsonObject>().Any(c =>
            GetString(c, "system") == _hrexTempSystem &&
            GetString(c, "code") == "data-query");

    private static string? GetString(JsonObject obj, string propertyName) =>
        obj.TryGetPropertyValue(propertyName, out JsonNode? node) && node is JsonValue value && value.TryGetValue(out string? s)
            ? s
            : null;

    private static JsonArray GetOrAddArray(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonArray existing)
        {
            return existing;
        }

        var array = new JsonArray();
        obj[propertyName] = array;
        return array;
    }

    /// <summary>Builds a hook response carrying only an outcome - deliberately no StatusCode, so the
    /// dispatching interaction proceeds normally instead of being short-circuited.</summary>
    private static FhirResponseContext FailureResponse(
        OperationOutcomeJsonNode.IssueSeverity severity,
        OperationOutcomeJsonNode.IssueType code,
        string diagnostics) => new()
        {
            Outcome = BuildOutcome(severity, code, diagnostics),
        };

    private static OperationOutcomeJsonNode BuildOutcome(
        OperationOutcomeJsonNode.IssueSeverity severity,
        OperationOutcomeJsonNode.IssueType code,
        string diagnostics)
    {
        var outcome = new OperationOutcomeJsonNode();
        outcome.Issue.Add(new OperationOutcomeJsonNode.IssueComponent
        {
            Severity = severity,
            Code = code,
            Diagnostics = diagnostics,
        });
        return outcome;
    }
}
