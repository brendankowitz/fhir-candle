// <copyright file="ExecutableSubscriptionInfo.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using FhirCandle.Search;

namespace FhirCandle.Models;

/// <summary>
/// The executable trigger/filter state a <c>ResourceStore</c> holds for one SubscriptionTopic.
/// </summary>
/// <remarks>
/// Ignixa-model port of a previous type: the old <c>CompiledExpression</c> FHIRPath trigger
/// becomes a plain expression string (Ignixa's <c>IsTrue</c> extension has built-in AST/delegate
/// caching), and the old <c>ParsedSearchParameter</c> lists become <see cref="ParsedQuery"/> instances
/// evaluated through <c>CandleSearchService.TestForMatch</c>.
/// </remarks>
public sealed class ExecutableSubscriptionInfo
{
    /// <summary>Values that represent interaction types.</summary>
    public enum InteractionTypes
    {
        Create,
        Update,
        Delete,
    }

    /// <summary>A trigger that fires on the interaction alone, with no criteria.</summary>
    public sealed record InteractionOnlyTrigger(
        bool OnCreate,
        bool OnUpdate,
        bool OnDelete);

    /// <summary>A FHIRPath-criteria trigger (evaluated with %current/%previous bound).</summary>
    public sealed record FhirPathTrigger(
        bool OnCreate,
        bool OnUpdate,
        bool OnDelete,
        string Expression);

    /// <summary>A query-criteria trigger: previous/current search tests plus the topic's
    /// auto-pass/auto-fail behavior for interactions where one side has no resource.</summary>
    public sealed record QueryTrigger(
        bool OnCreate,
        bool OnUpdate,
        bool OnDelete,
        ParsedQuery? PreviousTest,
        bool CreateAutoFails,
        bool CreateAutoPasses,
        ParsedQuery? CurrentTest,
        bool DeleteAutoFails,
        bool DeleteAutoPasses,
        bool RequireBothTests);

    /// <summary>Gets or sets the canonical URL of the topic.</summary>
    public string TopicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the interaction-only triggers.</summary>
    public IReadOnlyList<InteractionOnlyTrigger> InteractionTriggers { get; set; } = [];

    /// <summary>Gets or sets the FHIRPath triggers.</summary>
    public IReadOnlyList<FhirPathTrigger> FhirPathTriggers { get; set; } = [];

    /// <summary>Gets or sets the query triggers.</summary>
    public IReadOnlyList<QueryTrigger> QueryTriggers { get; set; } = [];

    /// <summary>Per-subscription filters, keyed by subscription id; a null query means the
    /// subscription is unfiltered for this resource type.</summary>
    public Dictionary<string, ParsedQuery?> FiltersBySubscription { get; } = [];

    /// <summary>Pre-parsed notification-shape <c>_include</c>/<c>_revinclude</c> query used to
    /// resolve additional-context resources for matched events.</summary>
    public ParsedQuery? AdditionalContext { get; set; }
}
