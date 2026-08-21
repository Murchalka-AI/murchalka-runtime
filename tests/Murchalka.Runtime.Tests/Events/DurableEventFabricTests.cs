using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Events;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.Events.Fabric;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Events;

/// <summary>Verifies durable event publication, delivery, deduplication, and replay.</summary>
public sealed class DurableEventFabricTests
{
    /// <summary>Verifies durable at-least-once delivery and handler receipt deduplication.</summary>
    [Fact]
    public async Task EventIsDeliveredAtLeastOnceAndDeduplicatedByHandlerReceipt()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        paths.EnsureCreated();
        WriteSchema(directory.Path, "event.schema.json", requireName: true);
        var sink = new RecordingEventSink();
        await using var fabric = new DurableEventFabric(paths, sink, new NoopRootAudit());
        var publisher = Phase3TestModuleFactory.Create(
            "dev.murchalka.publisher",
            eventPublications: [new EventPublication("interaction.completed", "event.schema.json")]);
        var subscriber = Phase3TestModuleFactory.Create(
            "dev.murchalka.relationships",
            eventSubscriptions: [new EventSubscription("interaction.completed", "event.schema.json", "relationship-update")]);
        var publisherInstance = new InstanceId("publisher-1");
        var subscriberInstance = new InstanceId("relationships-1");
        fabric.RegisterModule(publisher, publisherInstance, directory.Path, EmptyGrant());
        fabric.RegisterModule(subscriber, subscriberInstance, directory.Path, EmptyGrant());
        var eventId = Guid.NewGuid();

        var envelope = await fabric.PublishAsync(Request(eventId, publisher.Id, publisherInstance), CancellationToken.None);
        Assert.Equal("sha256:", envelope.PayloadSchema[..7]);
        Assert.Equal(1, await fabric.DispatchPendingAsync(CancellationToken.None));
        Assert.Single(sink.Deliveries);
        Assert.Equal("relationship-update", sink.Deliveries.Single().HandlerId);

        Assert.Equal(0, await fabric.DispatchPendingAsync(CancellationToken.None));
        Assert.Single(sink.Deliveries);
        Assert.Empty(Directory.EnumerateFiles(paths.EventOutbox, "*.json"));
    }

    /// <summary>Verifies that a disabled subscriber detaches and resumes pending delivery after reattachment.</summary>
    [Fact]
    public async Task DisabledSubscriberDetachesLiveAndPendingDeliveryResumesAfterReattach()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        paths.EnsureCreated();
        WriteSchema(directory.Path, "event.schema.json", requireName: true);
        var sink = new RecordingEventSink();
        await using var fabric = new DurableEventFabric(paths, sink, new NoopRootAudit());
        var publisher = Phase3TestModuleFactory.Create("dev.murchalka.publisher", eventPublications: [new EventPublication("interaction.completed", "event.schema.json")]);
        var subscriber = Phase3TestModuleFactory.Create("dev.murchalka.relationships", eventSubscriptions: [new EventSubscription("interaction.completed", "event.schema.json", "relationship-update")]);
        var publisherInstance = new InstanceId("publisher-1");
        var firstSubscriber = new InstanceId("relationships-1");
        fabric.RegisterModule(publisher, publisherInstance, directory.Path, EmptyGrant());
        fabric.RegisterModule(subscriber, firstSubscriber, directory.Path, EmptyGrant());
        await fabric.PublishAsync(Request(Guid.NewGuid(), publisher.Id, publisherInstance), CancellationToken.None);

        fabric.UnregisterModule(subscriber.Id, firstSubscriber);
        Assert.Equal(0, await fabric.DispatchPendingAsync(CancellationToken.None));
        Assert.Empty(sink.Deliveries);
        Assert.Single(Directory.EnumerateFiles(paths.EventOutbox, "*.json"));

        var replacement = new InstanceId("relationships-2");
        fabric.RegisterModule(subscriber, replacement, directory.Path, EmptyGrant());
        Assert.Equal(1, await fabric.DispatchPendingAsync(CancellationToken.None));
        Assert.Equal(replacement, sink.Deliveries.Single().ConsumerInstance);
    }

    /// <summary>Verifies incompatible schema quarantine and explicit replay after repair.</summary>
    [Fact]
    public async Task IncompatibleSubscriptionIsQuarantinedAndCanBeReplayedAfterSchemaRepair()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        paths.EnsureCreated();
        WriteSchema(directory.Path, "event.schema.json", requireName: true);
        WriteSchema(directory.Path, "incompatible.schema.json", requireName: false);
        var sink = new RecordingEventSink();
        await using var fabric = new DurableEventFabric(paths, sink, new NoopRootAudit());
        var publisher = Phase3TestModuleFactory.Create("dev.murchalka.publisher", eventPublications: [new EventPublication("interaction.completed", "event.schema.json")]);
        var incompatible = Phase3TestModuleFactory.Create("dev.murchalka.relationships", eventSubscriptions: [new EventSubscription("interaction.completed", "incompatible.schema.json", "relationship-update")]);
        var publisherInstance = new InstanceId("publisher-1");
        var subscriberInstance = new InstanceId("relationships-1");
        fabric.RegisterModule(publisher, publisherInstance, directory.Path, EmptyGrant());
        fabric.RegisterModule(incompatible, subscriberInstance, directory.Path, EmptyGrant());
        await fabric.PublishAsync(Request(Guid.NewGuid(), publisher.Id, publisherInstance), CancellationToken.None);

        Assert.Equal(1, await fabric.DispatchPendingAsync(CancellationToken.None));
        var quarantined = Assert.Single(await fabric.GetQuarantineAsync(CancellationToken.None));
        Assert.Equal("event-schema-incompatible", quarantined.ReasonCode);
        fabric.UnregisterModule(incompatible.Id, subscriberInstance);
        var repaired = Phase3TestModuleFactory.Create("dev.murchalka.relationships", eventSubscriptions: [new EventSubscription("interaction.completed", "event.schema.json", "relationship-update")]);
        fabric.RegisterModule(repaired, new InstanceId("relationships-2"), directory.Path, EmptyGrant());

        Assert.True(await fabric.ReplayAsync(quarantined.Id, CancellationToken.None));
        Assert.Equal(1, await fabric.DispatchPendingAsync(CancellationToken.None));
        Assert.Single(sink.Deliveries);
        Assert.Empty(await fabric.GetQuarantineAsync(CancellationToken.None));
    }

    /// <summary>Verifies bounded exponential retry and quarantine after the declared attempt limit.</summary>
    [Fact]
    public async Task DeliveryFailuresUseBoundedRetriesBeforeQuarantine()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        paths.EnsureCreated();
        WriteSchema(directory.Path, "event.schema.json", requireName: true);
        var sink = new RecordingEventSink { FailuresRemaining = 5 };
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));
        await using var fabric = new DurableEventFabric(paths, sink, new NoopRootAudit(), clock);
        var publisher = Phase3TestModuleFactory.Create("dev.murchalka.publisher", eventPublications: [new EventPublication("interaction.completed", "event.schema.json")]);
        var subscriber = Phase3TestModuleFactory.Create("dev.murchalka.relationships", eventSubscriptions: [new EventSubscription("interaction.completed", "event.schema.json", "relationship-update")]);
        var publisherInstance = new InstanceId("publisher-1");
        fabric.RegisterModule(publisher, publisherInstance, directory.Path, EmptyGrant());
        fabric.RegisterModule(subscriber, new InstanceId("relationships-1"), directory.Path, EmptyGrant());
        await fabric.PublishAsync(Request(Guid.NewGuid(), publisher.Id, publisherInstance), CancellationToken.None);

        Assert.Equal(0, await fabric.DispatchPendingAsync(CancellationToken.None));
        foreach (var delay in new[] { 1, 2, 4, 8 })
        {
            clock.Advance(TimeSpan.FromSeconds(delay));
            _ = await fabric.DispatchPendingAsync(CancellationToken.None);
        }

        Assert.Equal(5, sink.Deliveries.Count);
        var quarantined = Assert.Single(await fabric.GetQuarantineAsync(CancellationToken.None));
        Assert.Equal("event-delivery-attempts-exhausted", quarantined.ReasonCode);
        Assert.Empty(Directory.EnumerateFiles(paths.EventOutbox, "*.json"));
    }

    private static PermissionDecision EmptyGrant() => new(
        true,
        "implicit-empty-grant",
        "implicit-empty",
        0,
        JsonSerializer.SerializeToElement(new { }),
        null);

    private static EventPublishRequest Request(Guid eventId, ModuleId publisher, InstanceId instance) => new(
        eventId,
        "interaction.completed",
        1,
        publisher,
        instance,
        DateTimeOffset.UtcNow,
        "home",
        "person:owner",
        Guid.NewGuid().ToString("D"),
        null,
        "person:owner",
        DataClassification.Public,
        "conversation",
        JsonSerializer.SerializeToElement(new { name = "test" }));

    private static void WriteSchema(string directory, string name, bool requireName)
    {
        var required = requireName ? "\"required\":[\"name\"]," : string.Empty;
        File.WriteAllText(Path.Combine(directory, name), $"{{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",{required}\"properties\":{{\"name\":{{\"type\":\"string\"}}}}}}");
    }

    private sealed class RecordingEventSink : IEventDeliverySink
    {
        public List<EventDelivery> Deliveries { get; } = [];
        public int FailuresRemaining { get; set; }

        public Task DeliverAsync(EventDelivery delivery, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Synthetic event delivery failure.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
