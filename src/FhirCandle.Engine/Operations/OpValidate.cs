// <copyright file="OpValidate.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Serialization;
using Ignixa.Abstractions;
using Ignixa.Models;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Validation;
using Ignixa.Validation.Abstractions;
using Ignixa.Validation.Schema;

namespace FhirCandle.Operations;

/// <summary>
/// <c>$validate</c> operation implementation, backed by Ignixa's profile-aware structural validator
/// (<see cref="ProfileAwareValidationSchemaResolver"/> over <see cref="StructureDefinitionSchemaResolver"/>),
/// replacing the previous port's POCO attribute validator.
/// </summary>
public sealed class OpValidate : IFhirOperation
{
    /// <inheritdoc/>
    public string OperationName => "$validate";

    /// <inheritdoc/>
    public string OperationVersion => "0.0.1";

    /// <inheritdoc/>
    public Dictionary<FhirCandle.Utils.FhirReleases.FhirSequenceCodes, string> CanonicalByFhirVersion => new()
    {
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4, "http://hl7.org/fhir/OperationDefinition/Resource-validate" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B, "http://hl7.org/fhir/OperationDefinition/Resource-validate" },
        { FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5, "http://hl7.org/fhir/OperationDefinition/Resource-validate" },
    };

    /// <inheritdoc/>
    public bool IsNamedQuery => false;

    /// <inheritdoc/>
    public bool AffectsState => false;

    /// <inheritdoc/>
    public bool AllowGet => false;

    /// <inheritdoc/>
    public bool AllowPost => true;

    /// <inheritdoc/>
    public bool AllowSystemLevel => true;

    /// <inheritdoc/>
    public bool AllowResourceLevel => true;

    /// <inheritdoc/>
    public bool AllowInstanceLevel => true;

    /// <inheritdoc/>
    public bool AcceptsNonFhir => false;

    /// <inheritdoc/>
    public bool ReturnsNonFhir => false;

    /// <inheritdoc/>
    public string RequiresPackage => string.Empty;

    /// <inheritdoc/>
    public HashSet<string> SupportedResources => [];

    /// <inheritdoc/>
    public bool DoOperation(
        FhirRequestContext ctx,
        Storage.VersionedFhirStore store,
        Storage.IVersionedResourceStore? resourceStore,
        ResourceJsonNode? focusResource,
        ResourceJsonNode? bodyResource,
        out FhirResponseContext opResponse)
    {
        // Target resolution: a 'resource' parameter inside a Parameters body wins, then a direct
        // (non-Parameters) resource body, then the instance focus (URL), matching the old file's
        // resolution order.
        ResourceJsonNode? target = ExtractTarget(bodyResource) ?? focusResource;

        if (target is null)
        {
            OperationOutcome shapeOutcome = SerializationUtils.BuildOutcomeForRequest(
                HttpStatusCode.UnprocessableEntity,
                "$validate requires a target resource: either an instance focus (URL), a 'resource' parameter inside a Parameters body, or a resource as the request body.",
                OperationOutcomeIssue.IssueTypeCommon.Invalid);

            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Resource = shapeOutcome,
                Outcome = shapeOutcome,
            };
            return false;
        }

        IElement element = target.ToElement(store.Schema);

        var resolver = new ProfileAwareValidationSchemaResolver(
            new CachedValidationSchemaResolver(
                new StructureDefinitionSchemaResolver(store.Schema, terminologyService: null)));

        ValidationSchema? schema = resolver.ResolveForElement(element);

        if (schema is null)
        {
            OperationOutcome noSchemaOutcome = SerializationUtils.BuildOutcomeForRequest(
                HttpStatusCode.UnprocessableEntity,
                $"Unable to resolve a validation schema for resource type {target.ResourceType}.",
                OperationOutcomeIssue.IssueTypeCommon.NotSupported);

            opResponse = new()
            {
                StatusCode = HttpStatusCode.UnprocessableEntity,
                Resource = noSchemaOutcome,
                Outcome = noSchemaOutcome,
            };
            return false;
        }

        ValidationResult result = schema.Validate(element, new ValidationSettings { SkipTerminologyValidation = true });
        OperationOutcome outcome = result.ToOperationOutcome();

        // Per FHIR convention, validation issues are conveyed via OperationOutcome issues, not via 4xx
        // status - always 200 once we have a parseable target. Surface an explicit informational issue
        // when the validator found nothing to report, mirroring the old file's "All OK" behavior.
        if (outcome.Issue.Count == 0)
        {
            outcome.Issue.Add(new OperationOutcomeIssue
            {
                SeverityCode = OperationOutcomeIssue.IssueSeverityCode.Information,
                IssueTypeCode = OperationOutcomeIssue.IssueTypeCommon.Informational,
                Diagnostics = "All OK",
            });
        }

        AppendIgnoredParameterIssues(bodyResource, outcome);

        opResponse = new()
        {
            StatusCode = HttpStatusCode.OK,
            Resource = outcome,
            Outcome = outcome,
            ResourceType = outcome.ResourceType,
            Id = outcome.Id,
        };
        return true;
    }

    /// <summary>Surfaces 'mode' and 'profile' Parameters-body parameters as Information issues -
    /// both are accepted by the operation definition but ignored by this implementation, and callers
    /// should be able to see that without reading the OperationDefinition.</summary>
    private static void AppendIgnoredParameterIssues(ResourceJsonNode? bodyResource, OperationOutcome outcome)
    {
        if (bodyResource is null || bodyResource.ResourceType != "Parameters")
        {
            return;
        }

        Parameters parameters = bodyResource is Parameters typed
            ? typed
            : new Parameters(bodyResource.MutableNode, bodyResource.FhirVersion);

        foreach (string name in new[] { "mode", "profile" })
        {
            if (parameters.FindParameter(name) is not null)
            {
                outcome.Issue.Add(new OperationOutcomeIssue
                {
                    SeverityCode = OperationOutcomeIssue.IssueSeverityCode.Information,
                    IssueTypeCode = OperationOutcomeIssue.IssueTypeCommon.Informational,
                    Diagnostics = $"Parameter '{name}' is currently ignored by this $validate implementation.",
                });
            }
        }
    }

    /// <summary>Extracts the embedded resource from the 'resource' parameter inside a Parameters wrapper,
    /// or the body itself if it isn't a Parameters wrapper.</summary>
    private static ResourceJsonNode? ExtractTarget(ResourceJsonNode? bodyResource)
    {
        if (bodyResource is null)
        {
            return null;
        }

        if (bodyResource.ResourceType != "Parameters")
        {
            return bodyResource;
        }

        Parameters parameters = bodyResource is Parameters typed
            ? typed
            : new Parameters(bodyResource.MutableNode, bodyResource.FhirVersion);

        return parameters.FindParameter("resource")?.Resource;
    }

    /// <inheritdoc/>
    public ResourceJsonNode? GetDefinition(FhirCandle.Utils.FhirReleases.FhirSequenceCodes fhirVersion)
    {
        var parameters = new JsonArray(
            OperationDefinitionBuilder.Param("resource", "in", 0, "1", "Resource", "The resource to validate. May be omitted at instance level (the focus is used instead)."),
            OperationDefinitionBuilder.Param("mode", "in", 0, "1", "code", "Validation mode hint (create, update, delete, ...). Currently ignored by this implementation."),
            OperationDefinitionBuilder.Param("profile", "in", 0, "*", "canonical", "Optional profile canonical URLs the resource should be validated against."),
            OperationDefinitionBuilder.Param("return", "out", 1, "1", "OperationOutcome", "The OperationOutcome describing validation issues. An issue with severity 'information' indicates a successful validation."));

        return OperationDefinitionBuilder.Build(this, fhirVersion, parameters);
    }
}
