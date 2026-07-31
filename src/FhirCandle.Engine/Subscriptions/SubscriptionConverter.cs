// <copyright file="SubscriptionConverter.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Text.Json.Nodes;
using FhirCandle.Extensions;
using FhirCandle.Models;
using FhirCandle.Utils;
using Ignixa.Models;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using static FhirCandle.Subscriptions.ConverterUtils;

namespace FhirCandle.Subscriptions;

/// <summary>
/// Converts Subscription resources, notification status resources, and notification bundles between
/// their per-version JSON shapes and the common parsed models, merging the three old per-version
/// converters. R4 and R4B share one path (criteria string + subscriptions-backport extensions - their
/// JSON is identical); R5 uses the native Subscription shape. Notification status resources are
/// <c>Parameters</c> on R4 and <c>SubscriptionStatus</c> on R4B/R5 (whose JSON is identical, since
/// R5's integer64 fields serialize as JSON strings just like R4B's string fields).
/// </summary>
public sealed class SubscriptionConverter
{
    private const string FilterCriteriaSuffix = "backport-filter-criteria";
    private const string HeartbeatPeriodSuffix = "backport-heartbeat-period";
    private const string TimeoutSuffix = "backport-timeout";
    private const string MaxCountSuffix = "backport-max-count";
    private const string ChannelTypeSuffix = "backport-channel-type";
    private const string ContentSuffix = "backport-payload-content";

    private static readonly HashSet<string> _r4ChannelTypes = ["rest-hook", "websocket", "email", "sms", "message"];
    private static readonly HashSet<string> _r4StatusCodes = ["requested", "active", "error", "off"];
    private static readonly HashSet<string> _r5StatusCodes = ["requested", "active", "error", "off", "entered-in-error"];

    /// <summary>The literal for an active subscription.</summary>
    public const string ActiveCode = "active";

    /// <summary>The literal for a disabled subscription.</summary>
    public const string OffCode = "off";

    private readonly FhirReleases.FhirSequenceCodes _fhirVersion;
    private readonly bool _isR5;
    private readonly long _maxSubscriptionTicks;

    /// <summary>Initializes a new instance of the <see cref="SubscriptionConverter"/> class.</summary>
    /// <param name="fhirVersion">                    The FHIR version this converter handles.</param>
    /// <param name="maxSubscriptionExpirationMinutes">The maximum subscription lifetime in minutes.</param>
    public SubscriptionConverter(FhirReleases.FhirSequenceCodes fhirVersion, int maxSubscriptionExpirationMinutes)
    {
        _fhirVersion = fhirVersion;
        _isR5 = fhirVersion == FhirReleases.FhirSequenceCodes.R5;
        _maxSubscriptionTicks = TimeSpan.FromMinutes(maxSubscriptionExpirationMinutes).Ticks;
    }

    /// <summary>Gets the canonical URL of the payload-content value set for this FHIR version.</summary>
    public string PayloadContentVsUrl => _isR5
        ? "http://hl7.org/fhir/ValueSet/subscription-payload-content"
        : "http://hl7.org/fhir/uv/subscriptions-backport/ValueSet/backport-content-value-set";

    private HashSet<string> StatusCodes => _isR5 || _fhirVersion == FhirReleases.FhirSequenceCodes.R4B
        ? _r5StatusCodes
        : _r4StatusCodes;

    /// <summary>Attempts to parse a Subscription resource into common form.</summary>
    public bool TryParse(ResourceJsonNode? subscription, out ParsedSubscription parsed)
    {
        parsed = null!;

        if (subscription is null ||
            string.IsNullOrEmpty(subscription.Id) ||
            subscription.ResourceType != "Subscription")
        {
            return false;
        }

        ParsedSubscription? result = _isR5
            ? ParseR5(subscription.MutableNode, subscription.Id)
            : ParseBackport(subscription.MutableNode, subscription.Id);

        if (result is null)
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private ParsedSubscription? ParseBackport(JsonObject json, string id)
    {
        string criteria = GetStringProp(json, "criteria");

        if (!criteria.StartsWith("http", StringComparison.Ordinal))
        {
            return null;
        }

        JsonObject? channel = json["channel"] as JsonObject;

        var parsed = new ParsedSubscription
        {
            Id = id,
            TopicUrl = criteria,
            Tags = GetTags(json),
            ChannelSystem = string.Empty,
            ChannelCode = channel is null ? string.Empty : GetStringProp(channel, "type"),
            Endpoint = channel is null ? string.Empty : GetStringProp(channel, "endpoint"),
            ContentType = channel is null ? string.Empty : GetStringProp(channel, "payload"),
            ExpirationTicks = GetExpirationTicks(json),
        };

        if (channel is not null)
        {
            (Dictionary<string, List<JsonNode>> channelExts, _) = ParseExtensions(channel["extension"] as JsonArray);

            if (int.TryParse(GetString(channelExts, HeartbeatPeriodSuffix), out int heartbeat))
            {
                parsed.HeartbeatSeconds = heartbeat;
            }

            if (int.TryParse(GetString(channelExts, TimeoutSuffix), out int timeout))
            {
                parsed.TimeoutSeconds = timeout;
            }

            if (int.TryParse(GetString(channelExts, MaxCountSuffix), out int maxCount))
            {
                parsed.MaxEventsPerNotification = maxCount;
            }

            (Dictionary<string, List<JsonNode>> typeExts, _) = ParseExtensions(GetUnderscoreExtensions(channel, "type"));
            if (typeExts.TryGetValue(ChannelTypeSuffix, out List<JsonNode>? channelTypeValues) &&
                channelTypeValues.FirstOrDefault() is JsonObject coding)
            {
                parsed.ChannelSystem = GetStringProp(coding, "system");
                parsed.ChannelCode = GetStringProp(coding, "code");
            }

            (Dictionary<string, List<JsonNode>> payloadExts, _) = ParseExtensions(GetUnderscoreExtensions(channel, "payload"));
            string contentLevel = GetString(payloadExts, ContentSuffix);
            if (!string.IsNullOrEmpty(contentLevel))
            {
                parsed.ContentLevel = contentLevel;
            }

            foreach (string header in (channel["header"] as JsonArray ?? []).Select(AsString))
            {
                AddHeaderParameter(parsed, header);
            }
        }

        (Dictionary<string, List<JsonNode>> criteriaExts, _) = ParseExtensions(GetUnderscoreExtensions(json, "criteria"));
        foreach (string filterCriteria in GetStrings(criteriaExts, FilterCriteriaSuffix))
        {
            AddFilterCriteria(parsed, filterCriteria);
        }

        return parsed;
    }

    private ParsedSubscription? ParseR5(JsonObject json, string id)
    {
        string topic = GetStringProp(json, "topic");

        if (!topic.StartsWith("http", StringComparison.Ordinal))
        {
            return null;
        }

        JsonObject? channelType = json["channelType"] as JsonObject;

        var parsed = new ParsedSubscription
        {
            Id = id,
            TopicUrl = topic,
            Tags = GetTags(json),
            ChannelSystem = channelType is null ? string.Empty : GetStringProp(channelType, "system"),
            ChannelCode = channelType is null ? string.Empty : GetStringProp(channelType, "code"),
            Endpoint = GetStringProp(json, "endpoint"),
            HeartbeatSeconds = GetIntProp(json, "heartbeatPeriod") ?? 0,
            TimeoutSeconds = GetIntProp(json, "timeout") ?? 0,
            ContentType = GetStringProp(json, "contentType"),
            ContentLevel = GetStringProp(json, "content"),
            MaxEventsPerNotification = GetIntProp(json, "maxCount") ?? 0,
            ExpirationTicks = GetExpirationTicks(json),
        };

        foreach (JsonObject parameter in Objects(json, "parameter"))
        {
            string name = GetStringProp(parameter, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!parsed.Parameters.TryGetValue(name, out List<string>? values))
            {
                values = [];
                parsed.Parameters[name] = values;
            }

            values.Add(GetStringProp(parameter, "value"));
        }

        foreach (JsonObject filter in Objects(json, "filterBy"))
        {
            string resourceType = GetStringProp(filter, "resourceType");
            string key = string.IsNullOrEmpty(resourceType) ? "*" : resourceType;

            if (!parsed.Filters.TryGetValue(key, out List<ParsedSubscription.SubscriptionFilter>? filters))
            {
                filters = [];
                parsed.Filters[key] = filters;
            }

            filters.Add(new(
                resourceType,
                GetStringProp(filter, "filterParameter"),
                GetStringProp(filter, "comparator"),
                GetStringProp(filter, "modifier"),
                GetStringProp(filter, "value")));
        }

        return parsed;
    }

    private long GetExpirationTicks(JsonObject json)
    {
        string end = GetStringProp(json, "end");

        long ticks = !string.IsNullOrEmpty(end) && DateTimeOffset.TryParse(end, out DateTimeOffset endDate)
            ? endDate.Ticks
            : _maxSubscriptionTicks;

        return Math.Min(ticks, _maxSubscriptionTicks);
    }

    private static HashSet<string> GetTags(JsonObject json) =>
        [.. Objects(json["meta"], "tag").Select(t => $"{GetStringProp(t, "system")}|{GetStringProp(t, "code")}")];

    private static int? GetIntProp(JsonObject json, string propertyName) =>
        json[propertyName] is JsonValue value && value.TryGetValue(out int i) ? i : null;

    private static void AddHeaderParameter(ParsedSubscription parsed, string header)
    {
        int index = header.IndexOf(':');
        if (index == -1)
        {
            index = header.IndexOf('=');
        }

        if (index == -1)
        {
            return;
        }

        string key = header[..index].Trim();
        string value = header[(index + 1)..].Trim();

        if (!parsed.Parameters.TryGetValue(key, out List<string>? values))
        {
            values = [];
            parsed.Parameters[key] = values;
        }

        values.Add(value);
    }

    private static void AddFilterCriteria(ParsedSubscription parsed, string criteria)
    {
        if (string.IsNullOrEmpty(criteria))
        {
            return;
        }

        string key;
        string resourceType;
        string queryString;

        int index = criteria.IndexOf('?');
        if (index == -1)
        {
            key = "-";
            resourceType = string.Empty;
            queryString = criteria;
        }
        else
        {
            key = criteria[..index];
            resourceType = key;
            queryString = criteria[(index + 1)..];
        }

        if (!parsed.Filters.TryGetValue(key, out List<ParsedSubscription.SubscriptionFilter>? filters))
        {
            filters = [];
            parsed.Filters[key] = filters;
        }

        foreach (string queryParam in queryString.Split('&'))
        {
            string[] components = queryParam.Split('=');

            if (components.Length != 2)
            {
                continue;
            }

            string[] keyComponents = components[0].Split(':');

            filters.Add(new(
                resourceType,
                keyComponents[0],
                string.Empty,
                keyComponents.Length > 1 ? keyComponents[1] : string.Empty,
                components[1]));
        }
    }

    /// <summary>Builds a Subscription resource from a parsed subscription (the reverse direction,
    /// used to surface tracked subscriptions as FHIR resources).</summary>
    public bool TryParse(ParsedSubscription? parsed, out ResourceJsonNode subscription)
    {
        subscription = null!;

        if (parsed is null || string.IsNullOrEmpty(parsed.Id) || string.IsNullOrEmpty(parsed.TopicUrl))
        {
            return false;
        }

        string status = StatusCodes.Contains(parsed.CurrentStatus) ? parsed.CurrentStatus : "requested";
        string end = new DateTimeOffset(parsed.ExpirationTicks, TimeSpan.Zero).ToString("o");

        JsonObject json = _isR5
            ? BuildR5Subscription(parsed, status, end)
            : BuildBackportSubscription(parsed, status, end);

        if (parsed.Tags.Count != 0)
        {
            var tags = new JsonArray();
            foreach (string tag in parsed.Tags)
            {
                string[] parts = tag.Split('|');
                tags.Add(new JsonObject { ["system"] = parts[0], ["code"] = parts.Length > 1 ? parts[1] : string.Empty });
            }

            json["meta"] = new JsonObject { ["tag"] = tags };
        }

        subscription = JsonSourceNodeFactory.Parse((JsonNode)json);
        return true;
    }

    private static JsonObject BuildR5Subscription(ParsedSubscription parsed, string status, string end)
    {
        var json = new JsonObject
        {
            ["resourceType"] = "Subscription",
            ["id"] = parsed.Id,
            ["status"] = status,
            ["topic"] = parsed.TopicUrl,
            ["channelType"] = new JsonObject
            {
                ["system"] = parsed.ChannelSystem,
                ["code"] = parsed.ChannelCode,
            },
            ["endpoint"] = parsed.Endpoint,
            ["contentType"] = parsed.ContentType,
            ["end"] = end,
        };

        if (!string.IsNullOrEmpty(parsed.Reason))
        {
            json["reason"] = parsed.Reason;
        }

        if (!string.IsNullOrEmpty(parsed.ContentLevel))
        {
            json["content"] = parsed.ContentLevel;
        }

        if (parsed.HeartbeatSeconds is not null)
        {
            json["heartbeatPeriod"] = parsed.HeartbeatSeconds;
        }

        if (parsed.TimeoutSeconds is not null)
        {
            json["timeout"] = parsed.TimeoutSeconds;
        }

        if (parsed.MaxEventsPerNotification is not null)
        {
            json["maxCount"] = parsed.MaxEventsPerNotification;
        }

        if (parsed.Parameters.Count != 0)
        {
            var parameters = new JsonArray();
            foreach ((string key, List<string> values) in parsed.Parameters)
            {
                foreach (string value in values)
                {
                    parameters.Add(new JsonObject { ["name"] = key, ["value"] = value });
                }
            }

            json["parameter"] = parameters;
        }

        if (parsed.Filters.Count != 0)
        {
            var filterBy = new JsonArray();
            foreach (ParsedSubscription.SubscriptionFilter filter in parsed.Filters.Values.SelectMany(f => f))
            {
                var fb = new JsonObject
                {
                    ["filterParameter"] = filter.Name,
                    ["value"] = filter.Value,
                };

                if (!string.IsNullOrEmpty(filter.ResourceType))
                {
                    fb["resourceType"] = filter.ResourceType;
                }

                if (!string.IsNullOrEmpty(filter.Comparator))
                {
                    fb["comparator"] = filter.Comparator;
                }

                if (!string.IsNullOrEmpty(filter.Modifier))
                {
                    fb["modifier"] = filter.Modifier;
                }

                filterBy.Add(fb);
            }

            json["filterBy"] = filterBy;
        }

        return json;
    }

    private static JsonObject BuildBackportSubscription(ParsedSubscription parsed, string status, string end)
    {
        bool isStandardChannel = _r4ChannelTypes.Contains(parsed.ChannelCode);

        var channelExtensions = new JsonArray();

        if (parsed.HeartbeatSeconds is not null)
        {
            channelExtensions.Add(new JsonObject { ["url"] = UrlBackport + HeartbeatPeriodSuffix, ["valueInteger"] = parsed.HeartbeatSeconds });
        }

        if (parsed.TimeoutSeconds is not null)
        {
            channelExtensions.Add(new JsonObject { ["url"] = UrlBackport + TimeoutSuffix, ["valueInteger"] = parsed.TimeoutSeconds });
        }

        if (parsed.MaxEventsPerNotification is not null)
        {
            channelExtensions.Add(new JsonObject { ["url"] = UrlBackport + MaxCountSuffix, ["valueInteger"] = parsed.MaxEventsPerNotification });
        }

        if (!isStandardChannel && !string.IsNullOrEmpty(parsed.ChannelCode))
        {
            channelExtensions.Add(new JsonObject { ["url"] = UrlBackport + ChannelTypeSuffix, ["valueString"] = parsed.ChannelCode });
        }

        var channel = new JsonObject
        {
            ["type"] = isStandardChannel ? parsed.ChannelCode : "rest-hook",
            ["endpoint"] = parsed.Endpoint,
            ["payload"] = parsed.ContentType,
            ["_payload"] = new JsonObject
            {
                ["extension"] = new JsonArray(
                    new JsonObject { ["url"] = UrlBackport + ContentSuffix, ["valueCode"] = parsed.ContentLevel }),
            },
        };

        if (channelExtensions.Count != 0)
        {
            channel["extension"] = channelExtensions;
        }

        var json = new JsonObject
        {
            ["resourceType"] = "Subscription",
            ["id"] = parsed.Id,
            ["status"] = status,
            ["reason"] = string.IsNullOrEmpty(parsed.Reason) ? "Required" : parsed.Reason,
            ["criteria"] = parsed.TopicUrl,
            ["channel"] = channel,
            ["end"] = end,
        };

        if (parsed.Filters.Count != 0)
        {
            var filterExtensions = new JsonArray();
            foreach (ParsedSubscription.SubscriptionFilter filter in parsed.Filters.Values.SelectMany(f => f))
            {
                string modifier = string.IsNullOrEmpty(filter.Modifier) ? string.Empty : ":" + filter.Modifier;
                filterExtensions.Add(new JsonObject
                {
                    ["url"] = UrlBackport + FilterCriteriaSuffix,
                    ["valueString"] = $"{filter.ResourceType}?{filter.Name}{modifier}={filter.Comparator}{filter.Value}",
                });
            }

            json["_criteria"] = new JsonObject { ["extension"] = filterExtensions };
        }

        return json;
    }

    /// <summary>Updates the <c>status</c> element of a stored Subscription resource, if the literal is
    /// valid for this FHIR version.</summary>
    public void UpdateResourceStatus(ResourceJsonNode? subscription, string statusLiteral)
    {
        if (subscription is null || !StatusCodes.Contains(statusLiteral))
        {
            return;
        }

        subscription.MutableNode["status"] = statusLiteral;
        subscription.InvalidateCaches();
    }

    /// <summary>Attempts to parse a notification status resource (<c>Parameters</c> on R4,
    /// <c>SubscriptionStatus</c> on R4B/R5) into common form.</summary>
    public bool TryParse(ResourceJsonNode? statusResource, string bundleId, out ParsedSubscriptionStatus parsed)
    {
        parsed = null!;

        if (statusResource is null)
        {
            return false;
        }

        ParsedSubscriptionStatus? result = _fhirVersion == FhirReleases.FhirSequenceCodes.R4
            ? ParseR4Status(statusResource.MutableNode, bundleId)
            : ParseSubscriptionStatus(statusResource.MutableNode, bundleId);

        if (result is null)
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static ParsedSubscriptionStatus? ParseR4Status(JsonObject json, string bundleId)
    {
        if (GetStringProp(json, "resourceType") != "Parameters")
        {
            return null;
        }

        (Dictionary<string, List<JsonNode>> values, Dictionary<string, List<JsonArray>> nested) =
            ParseParameters(json["parameter"] as JsonArray);

        var notificationEvents = new List<ParsedSubscriptionStatus.ParsedNotificationEvent>();

        if (nested.TryGetValue("notification-event", out List<JsonArray>? eventParts))
        {
            foreach (JsonArray parts in eventParts)
            {
                (Dictionary<string, List<JsonNode>> eventValues, _) = ParseParameters(parts);

                notificationEvents.Add(new()
                {
                    Id = string.Empty,
                    EventNumber = long.TryParse(GetString(eventValues, "event-number"), out long eventNumber) ? eventNumber : null,
                    Timestamp = DateTimeOffset.TryParse(GetString(eventValues, "timestamp"), out DateTimeOffset timestamp) ? timestamp : null,
                    FocusReference = GetString(eventValues, "focus"),
                    AdditionalContextReferences = GetStrings(eventValues, "additional-context").ToArray(),
                });
            }
        }

        return new()
        {
            BundleId = bundleId,
            SubscriptionReference = GetString(values, "subscription"),
            SubscriptionTopicCanonical = GetString(values, "topic"),
            Status = GetString(values, "status"),
            NotificationType = GetString(values, "type").TryFhirEnum(out ParsedSubscription.NotificationTypeCodes r4Type) ? r4Type : null,
            EventsSinceSubscriptionStart = long.TryParse(GetString(values, "events-since-subscription-start"), out long count) ? count : null,
            NotificationEvents = notificationEvents,
        };
    }

    private static ParsedSubscriptionStatus? ParseSubscriptionStatus(JsonObject json, string bundleId)
    {
        if (GetStringProp(json, "resourceType") != "SubscriptionStatus")
        {
            return null;
        }

        List<string> errors = Objects(json, "error")
            .SelectMany(error => Objects(error, "coding"))
            .Select(coding => $"{GetStringProp(coding, "system")}|{GetStringProp(coding, "code")}")
            .ToList();

        var notificationEvents = new List<ParsedSubscriptionStatus.ParsedNotificationEvent>();

        foreach (JsonObject notificationEvent in Objects(json, "notificationEvent"))
        {
            notificationEvents.Add(new()
            {
                Id = GetStringProp(notificationEvent, "id"),
                EventNumber = long.TryParse(AsString(notificationEvent["eventNumber"]), out long eventNumber) ? eventNumber : null,
                Timestamp = DateTimeOffset.TryParse(GetStringProp(notificationEvent, "timestamp"), out DateTimeOffset timestamp) ? timestamp : null,
                FocusReference = notificationEvent["focus"] is JsonObject focus ? GetStringProp(focus, "reference") : string.Empty,
                AdditionalContextReferences = Objects(notificationEvent, "additionalContext")
                    .Select(ac => GetStringProp(ac, "reference"))
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToArray(),
            });
        }

        return new()
        {
            BundleId = bundleId,
            SubscriptionReference = json["subscription"] is JsonObject subscription ? GetStringProp(subscription, "reference") : string.Empty,
            SubscriptionTopicCanonical = GetStringProp(json, "topic"),
            Status = GetStringProp(json, "status"),
            NotificationType = GetStringProp(json, "type").TryFhirEnum(out ParsedSubscription.NotificationTypeCodes type) ? type : null,
            EventsSinceSubscriptionStart = long.TryParse(AsString(json["eventsSinceSubscriptionStart"]), out long count) ? count : null,
            NotificationEvents = notificationEvents,
            Errors = errors,
        };
    }

    private static (Dictionary<string, List<JsonNode>> Values, Dictionary<string, List<JsonArray>> Nested) ParseParameters(JsonArray? parameters)
    {
        Dictionary<string, List<JsonNode>> values = [];
        Dictionary<string, List<JsonArray>> nested = [];

        foreach (JsonObject parameter in (parameters ?? []).OfType<JsonObject>())
        {
            string name = GetStringProp(parameter, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (parameter["part"] is JsonArray parts)
            {
                if (!nested.TryGetValue(name, out List<JsonArray>? nestedList))
                {
                    nestedList = [];
                    nested[name] = nestedList;
                }

                nestedList.Add(parts);
            }

            if (!values.TryGetValue(name, out List<JsonNode>? valueList))
            {
                valueList = [];
                values[name] = valueList;
            }

            JsonNode? value = parameter.FirstOrDefault(kvp =>
                kvp.Key.StartsWith("value", StringComparison.Ordinal) && kvp.Value is not null).Value;

            if (value is not null)
            {
                valueList.Add(value);
            }
        }

        return (values, nested);
    }

    /// <summary>Builds the notification status resource for a subscription: <c>Parameters</c> on R4,
    /// <c>SubscriptionStatus</c> on R4B/R5.</summary>
    public ResourceJsonNode StatusForSubscription(ParsedSubscription subscription, string notificationType, string baseUrl) =>
        JsonSourceNodeFactory.Parse((JsonNode)BuildStatusObject(subscription, notificationType, baseUrl));

    private JsonObject BuildStatusObject(ParsedSubscription subscription, string notificationType, string baseUrl)
    {
        string subscriptionReference = baseUrl + "/Subscription/" + subscription.Id;

        if (_fhirVersion == FhirReleases.FhirSequenceCodes.R4)
        {
            return new JsonObject
            {
                ["resourceType"] = "Parameters",
                ["id"] = Guid.NewGuid().ToString(),
                ["parameter"] = new JsonArray(
                    new JsonObject { ["name"] = "subscription", ["valueReference"] = new JsonObject { ["reference"] = subscriptionReference } },
                    new JsonObject { ["name"] = "topic", ["valueCanonical"] = subscription.TopicUrl },
                    new JsonObject { ["name"] = "status", ["valueCode"] = subscription.CurrentStatus },
                    new JsonObject { ["name"] = "type", ["valueCode"] = notificationType },
                    new JsonObject { ["name"] = "events-since-subscription-start", ["valueString"] = subscription.CurrentEventCount.ToString() }),
            };
        }

        return new JsonObject
        {
            ["resourceType"] = "SubscriptionStatus",
            ["id"] = Guid.NewGuid().ToString(),
            ["status"] = StatusCodes.Contains(subscription.CurrentStatus) ? subscription.CurrentStatus : ActiveCode,
            ["type"] = notificationType,
            ["eventsSinceSubscriptionStart"] = subscription.CurrentEventCount.ToString(),
            ["subscription"] = new JsonObject { ["reference"] = subscriptionReference },
            ["topic"] = subscription.TopicUrl,
        };
    }

    private void AddNotificationEvent(
        JsonObject status,
        long eventNumber,
        DateTimeOffset timestamp,
        string? focusUrl,
        IEnumerable<string> additionalContextUrls)
    {
        if (_fhirVersion == FhirReleases.FhirSequenceCodes.R4)
        {
            var parts = new JsonArray(
                new JsonObject { ["name"] = "event-number", ["valueString"] = eventNumber.ToString() },
                new JsonObject { ["name"] = "timestamp", ["valueInstant"] = timestamp.ToString("o") });

            if (focusUrl is not null)
            {
                parts.Add(new JsonObject { ["name"] = "focus", ["valueReference"] = new JsonObject { ["reference"] = focusUrl } });
            }

            foreach (string contextUrl in additionalContextUrls)
            {
                parts.Add(new JsonObject { ["name"] = "additional-context", ["valueReference"] = new JsonObject { ["reference"] = contextUrl } });
            }

            var parameters = (JsonArray)status["parameter"]!;
            parameters.Add(new JsonObject { ["name"] = "notification-event", ["part"] = parts });
            return;
        }

        var notificationEvent = new JsonObject
        {
            ["eventNumber"] = eventNumber.ToString(),
            ["timestamp"] = timestamp.ToString("o"),
        };

        if (focusUrl is not null)
        {
            notificationEvent["focus"] = new JsonObject { ["reference"] = focusUrl };
        }

        var contextReferences = new JsonArray([.. additionalContextUrls.Select(u => (JsonNode)new JsonObject { ["reference"] = u })]);
        if (contextReferences.Count != 0)
        {
            notificationEvent["additionalContext"] = contextReferences;
        }

        if (status["notificationEvent"] is not JsonArray events)
        {
            events = [];
            status["notificationEvent"] = events;
        }

        events.Add(notificationEvent);
    }

    /// <summary>
    /// Builds the notification bundle for one or more subscription events: bundle type
    /// <c>history</c> on R4/R4B, <c>subscription-notification</c> on R5; first entry is the status
    /// resource; <c>full-resource</c> content level adds focus/additional-context resource entries.
    /// </summary>
    public Bundle? BundleForSubscriptionEvents(
        ParsedSubscription subscription,
        IEnumerable<long> eventNumbers,
        string notificationType,
        string baseUrl,
        string contentLevel = "")
    {
        if (string.IsNullOrEmpty(contentLevel))
        {
            contentLevel = subscription.ContentLevel;
        }

        JsonObject status = BuildStatusObject(subscription, notificationType, baseUrl);
        string statusId = GetStringProp(status, "id");

        var entries = new JsonArray(new JsonObject
        {
            ["fullUrl"] = $"urn:uuid:{statusId}",
            ["resource"] = status,
        });

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = _isR5 ? "subscription-notification" : "history",
            ["timestamp"] = DateTimeOffset.Now.ToString("o"),
            ["entry"] = entries,
        };

        bool isEmpty = contentLevel.Equals("empty", StringComparison.Ordinal);
        bool isFullResource = contentLevel.Equals("full-resource", StringComparison.Ordinal);

        List<long> numbers = eventNumbers.ToList();
        if (numbers.Count == 0)
        {
            if (notificationType.Equals("query-event", StringComparison.Ordinal))
            {
                numbers = [.. subscription.GeneratedEvents.Keys];
            }
            else if (subscription.GeneratedEvents.Count != 0)
            {
                numbers = [subscription.GeneratedEvents.Keys.Last()];
            }
        }

        var addedResources = new HashSet<string>();

        foreach (long eventNumber in numbers)
        {
            if (!subscription.GeneratedEvents.TryGetValue(eventNumber, out SubscriptionEvent? subscriptionEvent))
            {
                continue;
            }

            if (isEmpty)
            {
                AddNotificationEvent(status, eventNumber, subscriptionEvent.Timestamp, null, []);
                continue;
            }

            var focus = (ResourceJsonNode)subscriptionEvent.Focus;
            string focusRelative = $"{focus.ResourceType}/{focus.Id}";

            List<ResourceJsonNode> additionalContext = (subscriptionEvent.AdditionalContext ?? [])
                .OfType<ResourceJsonNode>()
                .ToList();

            AddNotificationEvent(
                status,
                eventNumber,
                subscriptionEvent.Timestamp,
                baseUrl + "/" + focusRelative,
                additionalContext.Select(ac => $"{baseUrl}/{ac.ResourceType}/{ac.Id}"));

            if (isFullResource && addedResources.Add(focusRelative))
            {
                entries.Add(new JsonObject
                {
                    ["fullUrl"] = baseUrl + "/" + focusRelative,
                    ["resource"] = focus.MutableNode.DeepClone(),
                });
            }

            foreach (ResourceJsonNode contextResource in additionalContext)
            {
                string contextRelative = $"{contextResource.ResourceType}/{contextResource.Id}";

                if (isFullResource && addedResources.Add(contextRelative))
                {
                    entries.Add(new JsonObject
                    {
                        ["fullUrl"] = baseUrl + "/" + contextRelative,
                        ["resource"] = contextResource.MutableNode.DeepClone(),
                    });
                }
            }
        }

        return new Bundle(bundle);
    }
}
