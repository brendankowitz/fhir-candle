// <copyright file="SubscriptionTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Net;
using System.Text.Json.Nodes;
using FhirCandle.Models;
using FhirCandle.Storage;
using FhirCandle.Utils;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class SubscriptionTests
{
    private const string TopicUrl = "http://example.org/FHIR/SubscriptionTopic/encounter-complete";

    private static VersionedFhirStore CreateStore(FhirReleases.FhirSequenceCodes version)
    {
        var store = new VersionedFhirStore();
        store.Init(new TenantConfiguration
        {
            FhirVersion = version,
            ControllerName = "test",
            BaseUrl = "http://localhost/fhir/test",
        });
        return store;
    }

    private static FhirRequestContext Ctx(
        VersionedFhirStore store,
        string httpMethod,
        string url,
        string? sourceContent = null) => new()
    {
        TenantName = "test",
        Store = store,
        HttpMethod = httpMethod,
        Url = url,
        Authorization = null,
        SourceContent = sourceContent ?? string.Empty,
        SourceFormat = sourceContent is null ? string.Empty : "application/fhir+json",
    };

    private static void Store(VersionedFhirStore store, string resourceType, string json)
    {
        bool ok = store.InstanceUpdate(Ctx(store, "PUT", ExtractTypeAndId(resourceType, json), json), out FhirResponseContext response);
        ok.ShouldBeTrue($"storing {resourceType} failed: {response.SerializedOutcome}");
    }

    private static string ExtractTypeAndId(string resourceType, string json)
    {
        JsonNode node = JsonNode.Parse(json)!;
        return $"{resourceType}/{node["id"]!.GetValue<string>()}";
    }

    #region sample resources

    private const string R4BasicTopicJson = """
        {
          "resourceType": "Basic",
          "id": "encounter-complete",
          "modifierExtension": [{
            "url": "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.status",
            "valueCode": "draft"
          }],
          "extension": [
            {
              "url": "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.url",
              "valueUri": "http://example.org/FHIR/SubscriptionTopic/encounter-complete"
            },
            {
              "url": "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.version",
              "valueString": "1.0.0-fhir.r4"
            },
            {
              "url": "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.title",
              "valueString": "encounter-complete"
            },
            {
              "extension": [
                { "url": "description", "valueMarkdown": "An Encounter has been completed" },
                { "url": "resource", "valueUri": "http://hl7.org/fhir/StructureDefinition/Encounter" },
                { "url": "supportedInteraction", "valueCode": "create" },
                { "url": "supportedInteraction", "valueCode": "update" },
                {
                  "extension": [
                    { "url": "previous", "valueString": "status:not=finished" },
                    { "url": "resultForCreate", "valueCode": "test-passes" },
                    { "url": "current", "valueString": "status=finished" },
                    { "url": "resultForDelete", "valueCode": "test-fails" },
                    { "url": "requireBoth", "valueBoolean": true }
                  ],
                  "url": "queryCriteria"
                },
                {
                  "url": "fhirPathCriteria",
                  "valueString": "(%previous.id.empty() or (%previous.status != 'finished')) and (%current.status = 'finished')"
                }
              ],
              "url": "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.resourceTrigger"
            },
            {
              "extension": [
                { "url": "description", "valueMarkdown": "Filter based on the subject of an encounter." },
                { "url": "resource", "valueUri": "Encounter" },
                { "url": "filterParameter", "valueString": "patient" }
              ],
              "url": "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.canFilterBy"
            },
            {
              "extension": [
                { "url": "resource", "valueUri": "Encounter" },
                { "url": "include", "valueString": "Encounter:patient&iterate=Patient.link" },
                { "url": "revInclude", "valueString": "Encounter:subject" }
              ],
              "url": "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.notificationShape"
            }
          ],
          "code": {
            "coding": [{ "system": "http://hl7.org/fhir/fhir-types", "code": "SubscriptionTopic" }]
          }
        }
        """;

    private const string R5TopicJson = """
        {
          "resourceType": "SubscriptionTopic",
          "id": "encounter-complete",
          "url": "http://example.org/FHIR/SubscriptionTopic/encounter-complete",
          "version": "1.0.0-fhir.r5",
          "title": "encounter-complete",
          "status": "draft",
          "date": "2019-01-01",
          "description": "Example topic for completed encounters",
          "resourceTrigger": [
            {
              "description": "An Encounter has been completed",
              "resource": "http://hl7.org/fhir/StructureDefinition/Encounter",
              "supportedInteraction": ["create", "update"],
              "queryCriteria": {
                "previous": "status:not=completed",
                "resultForCreate": "test-passes",
                "current": "status=completed",
                "resultForDelete": "test-fails",
                "requireBoth": true
              },
              "fhirPathCriteria": "(%previous.id.empty() or (%previous.status != 'completed')) and (%current.status = 'completed')"
            }
          ],
          "canFilterBy": [
            {
              "description": "Filter based on the subject of an encounter.",
              "resource": "Encounter",
              "filterParameter": "patient"
            }
          ],
          "notificationShape": [
            {
              "resource": "Encounter",
              "include": ["Encounter:patient&iterate=Patient.link"]
            }
          ]
        }
        """;

    private const string R4SubscriptionJson = """
        {
          "resourceType": "Subscription",
          "id": "sub-r4-encounter",
          "status": "requested",
          "reason": "Test subscription",
          "criteria": "http://example.org/FHIR/SubscriptionTopic/encounter-complete",
          "_criteria": {
            "extension": [
              {
                "url": "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-filter-criteria",
                "valueString": "Encounter?patient=Patient/example"
              }
            ]
          },
          "channel": {
            "extension": [
              {
                "url": "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-heartbeat-period",
                "valueInteger": 120
              }
            ],
            "type": "rest-hook",
            "endpoint": "https://subscriptions.argo.run/fhir/r4/$subscription-hook",
            "payload": "application/fhir+json",
            "_payload": {
              "extension": [
                {
                  "url": "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-payload-content",
                  "valueCode": "id-only"
                }
              ]
            }
          }
        }
        """;

    private const string R5SubscriptionJson = """
        {
          "resourceType": "Subscription",
          "id": "sub-r5-encounter",
          "status": "active",
          "reason": "Test subscription",
          "topic": "http://example.org/FHIR/SubscriptionTopic/encounter-complete",
          "filterBy": [
            {
              "resourceType": "Encounter",
              "filterParameter": "patient",
              "value": "Patient/example"
            }
          ],
          "channelType": {
            "system": "http://terminology.hl7.org/CodeSystem/subscription-channel-type",
            "code": "rest-hook"
          },
          "endpoint": "https://subscriptions.argo.run/fhir/r5/$subscription-hook",
          "heartbeatPeriod": 120,
          "timeout": 0,
          "contentType": "application/fhir+json",
          "content": "full-resource",
          "maxCount": 10
        }
        """;

    private const string PatientJson = """
        {"resourceType":"Patient","id":"example","birthDate":"1980-01-01"}
        """;

    private static string EncounterJson(FhirReleases.FhirSequenceCodes version, string status) =>
        (version == FhirReleases.FhirSequenceCodes.R5
            ? """
              {"resourceType":"Encounter","id":"enc-under-test","status":"STATUS","subject":{"reference":"Patient/example"}}
              """
            : """
              {"resourceType":"Encounter","id":"enc-under-test","status":"STATUS","class":{"system":"http://terminology.hl7.org/CodeSystem/v3-ActCode","code":"AMB"},"subject":{"reference":"Patient/example"}}
              """).Replace("STATUS", status);

    #endregion

    [Fact]
    public void TopicCreate_R4Basic_AppearsInCurrentTopics()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R4);

        Store(store, "Basic", R4BasicTopicJson);

        ParsedSubscriptionTopic topic = store.CurrentTopics.ShouldHaveSingleItem();
        topic.Url.ShouldBe(TopicUrl);
        topic.Id.ShouldBe("encounter-complete");
        topic.ResourceTriggers.Keys.ShouldBe(["Encounter"]);

        ParsedSubscriptionTopic.ResourceTrigger trigger = topic.ResourceTriggers["Encounter"].ShouldHaveSingleItem();
        trigger.OnCreate.ShouldBeTrue();
        trigger.OnUpdate.ShouldBeTrue();
        trigger.OnDelete.ShouldBeFalse();
        trigger.FhirPathCriteria.ShouldContain("%current.status = 'finished'");
        trigger.QueryPrevious.ShouldBe("status:not=finished");
        trigger.QueryCurrent.ShouldBe("status=finished");
        trigger.CreateAutoPass.ShouldBeTrue();
        trigger.DeleteAutoFail.ShouldBeTrue();
        trigger.RequireBothQueries.ShouldBeTrue();

        topic.AllowedFilters.ShouldContainKey("Encounter");
        topic.NotificationShapes.ShouldContainKey("Encounter");
    }

    [Fact]
    public void TopicCreate_R5Native_AppearsInCurrentTopics()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R5);

        Store(store, "SubscriptionTopic", R5TopicJson);

        ParsedSubscriptionTopic topic = store.CurrentTopics.ShouldHaveSingleItem();
        topic.Url.ShouldBe(TopicUrl);
        topic.ResourceTriggers.Keys.ShouldBe(["Encounter"]);

        ParsedSubscriptionTopic.ResourceTrigger trigger = topic.ResourceTriggers["Encounter"].ShouldHaveSingleItem();
        trigger.OnCreate.ShouldBeTrue();
        trigger.OnUpdate.ShouldBeTrue();
        trigger.OnDelete.ShouldBeFalse();
        trigger.FhirPathCriteria.ShouldContain("%current.status = 'completed'");
        trigger.RequireBothQueries.ShouldBeTrue();

        topic.AllowedFilters.ShouldContainKey("Encounter");
        topic.NotificationShapes.ShouldContainKey("Encounter");
    }

    [Fact]
    public void SubscriptionCreate_R4BackportExtensions_ParsesChannelAndFilters()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R4);

        Store(store, "Basic", R4BasicTopicJson);
        Store(store, "Subscription", R4SubscriptionJson);

        ParsedSubscription subscription = store.CurrentSubscriptions.ShouldHaveSingleItem();
        subscription.Id.ShouldBe("sub-r4-encounter");
        subscription.TopicUrl.ShouldBe(TopicUrl);
        subscription.ChannelCode.ShouldBe("rest-hook");
        subscription.Endpoint.ShouldBe("https://subscriptions.argo.run/fhir/r4/$subscription-hook");
        subscription.HeartbeatSeconds.ShouldBe(120);
        subscription.ContentType.ShouldBe("application/fhir+json");
        subscription.ContentLevel.ShouldBe("id-only");

        subscription.Filters.ShouldContainKey("Encounter");
        ParsedSubscription.SubscriptionFilter filter = subscription.Filters["Encounter"].ShouldHaveSingleItem();
        filter.Name.ShouldBe("patient");
        filter.Value.ShouldBe("Patient/example");
    }

    [Theory]
    [InlineData(FhirReleases.FhirSequenceCodes.R4)]
    [InlineData(FhirReleases.FhirSequenceCodes.R5)]
    public void EncounterStatusChange_MatchingSubscription_RaisesOneEvent(FhirReleases.FhirSequenceCodes version)
    {
        VersionedFhirStore store = CreateStore(version);
        bool isR5 = version == FhirReleases.FhirSequenceCodes.R5;
        string completedStatus = isR5 ? "completed" : "finished";

        Store(store, "Patient", PatientJson);

        if (isR5)
        {
            Store(store, "SubscriptionTopic", R5TopicJson);
            Store(store, "Subscription", R5SubscriptionJson);
        }
        else
        {
            Store(store, "Basic", R4BasicTopicJson);
            Store(store, "Subscription", R4SubscriptionJson);
        }

        var sendEvents = new List<SubscriptionSendEventArgs>();
        store.OnSubscriptionSendEvent += (_, e) => sendEvents.Add(e);

        // create in a non-matching state: FHIRPath trigger requires %current.status = completed/finished
        Store(store, "Encounter", EncounterJson(version, "planned"));
        sendEvents.ShouldBeEmpty("planned Encounter must not trigger the completed-encounter topic");

        // update into the matching state: exactly one event
        Store(store, "Encounter", EncounterJson(version, completedStatus));

        SubscriptionSendEventArgs sendEvent = sendEvents.ShouldHaveSingleItem();
        sendEvent.NotificationType.ShouldBe(ParsedSubscription.NotificationTypeCodes.EventNotification);
        sendEvent.Subscription.Id.ShouldBe(isR5 ? "sub-r5-encounter" : "sub-r4-encounter");

        SubscriptionEvent notification = sendEvent.NotificationEvents.ShouldHaveSingleItem();
        notification.EventNumber.ShouldBe(1L);
        notification.TopicUrl.ShouldBe(TopicUrl);
        notification.Focus.ShouldBeOfType<ResourceJsonNode>().Id.ShouldBe("enc-under-test");

        ParsedSubscription subscription = store.CurrentSubscriptions.ShouldHaveSingleItem();
        subscription.CurrentEventCount.ShouldBe(1);
        subscription.GeneratedEvents.ShouldHaveSingleItem();
        subscription.NotificationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void BundleForSubscriptionEvents_R4_HistoryBundleWithParametersStatus()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R4);
        Store(store, "Patient", PatientJson);
        Store(store, "Basic", R4BasicTopicJson);
        Store(store, "Subscription", R4SubscriptionJson);
        Store(store, "Encounter", EncounterJson(FhirReleases.FhirSequenceCodes.R4, "finished"));

        BundleJsonNode? bundle = store.BundleForSubscriptionEvents("sub-r4-encounter", [], "event-notification");

        bundle.ShouldNotBeNull();
        bundle!.MutableNode["type"]!.GetValue<string>().ShouldBe("history");

        JsonArray entries = bundle.MutableNode["entry"].ShouldBeOfType<JsonArray>();
        JsonObject status = entries[0]!["resource"].ShouldBeOfType<JsonObject>();
        status["resourceType"]!.GetValue<string>().ShouldBe("Parameters");

        JsonArray parameters = status["parameter"].ShouldBeOfType<JsonArray>();
        parameters.Count(p => p!["name"]!.GetValue<string>() == "notification-event").ShouldBe(1);
        parameters.First(p => p!["name"]!.GetValue<string>() == "topic")!["valueCanonical"]!.GetValue<string>().ShouldBe(TopicUrl);
        parameters.First(p => p!["name"]!.GetValue<string>() == "type")!["valueCode"]!.GetValue<string>().ShouldBe("event-notification");
        parameters.First(p => p!["name"]!.GetValue<string>() == "events-since-subscription-start")!["valueString"]!.GetValue<string>().ShouldBe("1");
    }

    [Fact]
    public void BundleForSubscriptionEvents_R5_NotificationBundleWithSubscriptionStatus()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R5);
        Store(store, "Patient", PatientJson);
        Store(store, "SubscriptionTopic", R5TopicJson);
        Store(store, "Subscription", R5SubscriptionJson);
        Store(store, "Encounter", EncounterJson(FhirReleases.FhirSequenceCodes.R5, "completed"));

        BundleJsonNode? bundle = store.BundleForSubscriptionEvents("sub-r5-encounter", [], "event-notification");

        bundle.ShouldNotBeNull();
        bundle!.MutableNode["type"]!.GetValue<string>().ShouldBe("subscription-notification");

        JsonArray entries = bundle.MutableNode["entry"].ShouldBeOfType<JsonArray>();
        JsonObject status = entries[0]!["resource"].ShouldBeOfType<JsonObject>();
        status["resourceType"]!.GetValue<string>().ShouldBe("SubscriptionStatus");
        status["type"]!.GetValue<string>().ShouldBe("event-notification");
        status["eventsSinceSubscriptionStart"]!.GetValue<string>().ShouldBe("1");

        JsonArray notificationEvents = status["notificationEvent"].ShouldBeOfType<JsonArray>();
        notificationEvents.Count.ShouldBe(1);
        notificationEvents[0]!["focus"]!["reference"]!.GetValue<string>()
            .ShouldBe("http://localhost/fhir/test/Encounter/enc-under-test");

        // full-resource content level: the focus resource rides along as a bundle entry, and the
        // topic's notification shape (_include=Encounter:patient) adds the patient as context
        entries.Count.ShouldBe(3);
        entries[1]!["resource"]!["resourceType"]!.GetValue<string>().ShouldBe("Encounter");
        entries[2]!["resource"]!["resourceType"]!.GetValue<string>().ShouldBe("Patient");
        notificationEvents[0]!["additionalContext"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void TopicDelete_RemovesFromCurrentTopics()
    {
        VersionedFhirStore store = CreateStore(FhirReleases.FhirSequenceCodes.R5);
        Store(store, "SubscriptionTopic", R5TopicJson);
        store.CurrentTopics.ShouldHaveSingleItem();

        bool ok = store.InstanceDelete(Ctx(store, "DELETE", "SubscriptionTopic/encounter-complete"), out _);

        ok.ShouldBeTrue();
        store.CurrentTopics.ShouldBeEmpty();
    }
}
