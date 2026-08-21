using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Events;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.Events.Internal;

namespace Murchalka.Runtime.Events.Fabric;

/// <summary>Provides a durable at-least-once event outbox with inbox deduplication and quarantine replay.</summary>
public sealed class DurableEventFabric : IEventFabric
{
    private const int MaximumDeliveryAttempts = 5;
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);
    private readonly RuntimePaths _paths;
    private readonly IEventDeliverySink _delivery;
    private readonly IRootAudit _audit;
    private readonly TimeProvider _timeProvider;
    private readonly object _registrationGate = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IReadOnlyList<EventPublicationRegistration> _publications = [];
    private IReadOnlyList<EventSubscriptionRegistration> _subscriptions = [];
    private Task? _processing;
    private long _lastOutboxTicks;
    private bool _sequenceInitialized;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a durable local event fabric.</summary>
    /// <param name="paths">The Runtime filesystem paths.</param>
    /// <param name="delivery">The authenticated event delivery sink.</param>
    /// <param name="audit">The non-disableable Root audit.</param>
    /// <param name="timeProvider">The optional trusted time provider.</param>
    public DurableEventFabric(RuntimePaths paths, IEventDeliverySink delivery, IRootAudit audit, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths.EnsureCreated();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_started) throw new InvalidOperationException("Event fabric is already started.");
        _paths.EnsureCreated();
        _started = true;
        _processing = ProcessAsync(_shutdown.Token);
        Signal();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void RegisterModule(ModuleManifest manifest, InstanceId instanceId, string contentPath, PermissionDecision permission)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(permission);
        var publications = manifest.EventPublications.Select(value =>
        {
            var (digest, schema) = LoadSchema(contentPath, value.SchemaPath);
            return new EventPublicationRegistration(manifest.Id, instanceId, value.Topic, digest, schema, permission);
        }).ToArray();
        var subscriptions = manifest.EventSubscriptions.Select(value =>
        {
            var (digest, _) = LoadSchema(contentPath, value.SchemaPath);
            return new EventSubscriptionRegistration(manifest.Id, instanceId, value.Topic, value.HandlerId, digest, permission);
        }).ToArray();
        RejectDuplicates(publications, subscriptions, manifest.Id);
        lock (_registrationGate)
        {
            if (_publications.Any(value => value.ModuleId == manifest.Id && value.InstanceId == instanceId) ||
                _subscriptions.Any(value => value.ModuleId == manifest.Id && value.InstanceId == instanceId))
                throw new InvalidOperationException($"Event contributions for instance '{instanceId}' are already registered.");
            _publications = [.. _publications, .. publications];
            _subscriptions = [.. _subscriptions, .. subscriptions];
        }
        Signal();
    }

    /// <inheritdoc />
    public void UnregisterModule(ModuleId moduleId, InstanceId instanceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_registrationGate)
        {
            _publications = _publications.Where(value => value.ModuleId != moduleId || value.InstanceId != instanceId).ToArray();
            _subscriptions = _subscriptions.Where(value => value.ModuleId != moduleId || value.InstanceId != instanceId).ToArray();
        }
    }

    /// <inheritdoc />
    public async Task<EventEnvelope> PublishAsync(EventPublishRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.EventId == Guid.Empty) throw new ArgumentException("Event id cannot be empty.", nameof(request));
        if (request.SchemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(request), "Event schema version must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PartitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        EventPublicationRegistration publication;
        IReadOnlyList<EventSubscriptionRegistration> subscriptions;
        lock (_registrationGate)
        {
            publication = _publications.SingleOrDefault(value => value.ModuleId == request.ProducerModule && value.InstanceId == request.ProducerInstance && value.Topic == request.Topic)
                ?? throw new UnauthorizedAccessException("The producer did not declare this event topic.");
            subscriptions = _subscriptions.Where(value => value.Topic == request.Topic).OrderBy(value => value.ModuleId.Value, StringComparer.Ordinal).ThenBy(value => value.HandlerId, StringComparer.Ordinal).ToArray();
        }
        if (!CanAccess(publication.Permission, "write", request.Topic, request.DataClassification))
            throw new UnauthorizedAccessException("The producer grant does not authorize this event classification or topic.");
        ValidatePayload(publication.Schema, request.Payload);
        var publishedAt = _timeProvider.GetUtcNow();
        var envelope = new EventEnvelope(
            request.EventId,
            request.Topic,
            request.SchemaVersion,
            request.ProducerModule,
            request.ProducerInstance,
            request.OccurredAt,
            publishedAt,
            request.TenantId,
            request.ActorReference,
            request.CorrelationId,
            request.CausationId,
            request.PartitionKey,
            request.DataClassification,
            request.Purpose,
            publication.SchemaDigest,
            request.Payload.Clone());
        var targets = subscriptions.Select(value => new OutboxTarget(value.ModuleId, value.HandlerId, 0, null)).ToArray();
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = new OutboxRecord(envelope, targets);
            var existing = FindOutbox(request.EventId);
            if (existing is not null)
            {
                var stored = await ReadAsync<OutboxRecord>(existing, cancellationToken).ConfigureAwait(false);
                if (stored.Event.ProducerModule != envelope.ProducerModule || stored.Event.ProducerInstance != envelope.ProducerInstance || stored.Event.Topic != envelope.Topic || stored.Event.Payload.GetRawText() != envelope.Payload.GetRawText())
                    throw new InvalidOperationException("Event id was reused with different content.");
                return stored.Event;
            }
            var ticks = NextOutboxTicks(publishedAt.UtcTicks);
            var path = Path.Combine(_paths.EventOutbox, $"{ticks:D19}-{request.EventId:D}.json");
            await WriteAtomicAsync(path, record, cancellationToken).ConfigureAwait(false);
        }
        finally { _publishGate.Release(); }
        await _audit.AppendAsync("event.published", request.ProducerModule.Value, "success", "outbox-appended", new Dictionary<string, string?>
        {
            ["eventId"] = request.EventId.ToString("D"),
            ["topic"] = request.Topic,
            ["classification"] = Classification(request.DataClassification),
            ["subscriberCount"] = targets.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        Signal();
        return envelope;
    }

    /// <inheritdoc />
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completed = 0;
            var blockedPartitions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(_paths.EventOutbox, "*.json").Order(StringComparer.Ordinal))
            {
                var record = await ReadAsync<OutboxRecord>(path, cancellationToken).ConfigureAwait(false);
                if (blockedPartitions.Contains(record.Event.PartitionKey)) continue;
                var result = await DispatchRecordAsync(path, record, cancellationToken).ConfigureAwait(false);
                completed += result.Completed;
                if (result.Pending) blockedPartitions.Add(record.Event.PartitionKey);
            }
            return completed;
        }
        finally { _dispatchGate.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventQuarantineItem>> GetQuarantineAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<EventQuarantineItem>();
            foreach (var path in Directory.EnumerateFiles(_paths.EventQuarantine, "*.json").Order(StringComparer.Ordinal))
            {
                var value = await ReadAsync<QuarantineRecord>(path, cancellationToken).ConfigureAwait(false);
                result.Add(new EventQuarantineItem(value.Id, value.Event.EventId, value.Event.Topic, value.Target.ConsumerModule, value.Target.HandlerId, value.ReasonCode, value.QuarantinedAt));
            }
            return result.OrderBy(value => value.QuarantinedAt).ThenBy(value => value.Id, StringComparer.Ordinal).ToArray();
        }
        finally { _dispatchGate.Release(); }
    }

    /// <inheritdoc />
    public async Task<bool> ReplayAsync(string quarantineId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineId);
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_paths.EventQuarantine, quarantineId + ".json");
            if (!File.Exists(path)) return false;
            var value = await ReadAsync<QuarantineRecord>(path, cancellationToken).ConfigureAwait(false);
            var target = value.Target with { Attempts = 0, NextAttemptAt = null };
            var ticks = NextOutboxTicks(_timeProvider.GetUtcNow().UtcTicks);
            var destination = Path.Combine(_paths.EventOutbox, $"{ticks:D19}-{value.Event.EventId:D}-{quarantineId}.json");
            await WriteAtomicAsync(destination, new OutboxRecord(value.Event, [target]), cancellationToken).ConfigureAwait(false);
            File.Delete(path);
            await _audit.AppendAsync("event.replayed", target.ConsumerModule.Value, "success", "quarantine-requeued", new Dictionary<string, string?>
            {
                ["eventId"] = value.Event.EventId.ToString("D"),
                ["handler"] = target.HandlerId,
                ["quarantineId"] = quarantineId
            }, cancellationToken).ConfigureAwait(false);
            Signal();
            return true;
        }
        finally { _dispatchGate.Release(); }
    }

    private async Task<(int Completed, bool Pending)> DispatchRecordAsync(string path, OutboxRecord record, CancellationToken cancellationToken)
    {
        var remaining = new List<OutboxTarget>(record.Targets.Count);
        var completed = 0;
        foreach (var target in record.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = ReceiptPath(target.ConsumerModule, target.HandlerId, record.Event.EventId);
            if (File.Exists(receipt)) { completed++; continue; }
            EventSubscriptionRegistration? subscription;
            lock (_registrationGate)
                subscription = _subscriptions.SingleOrDefault(value => value.ModuleId == target.ConsumerModule && value.HandlerId == target.HandlerId && value.Topic == record.Event.Topic);
            if (subscription is null || target.NextAttemptAt is { } next && next > _timeProvider.GetUtcNow())
            {
                remaining.Add(target);
                continue;
            }
            if (!string.Equals(subscription.SchemaDigest, record.Event.PayloadSchema, StringComparison.Ordinal))
            {
                await QuarantineAsync(record.Event, target, "event-schema-incompatible", cancellationToken).ConfigureAwait(false);
                completed++;
                continue;
            }
            if (!CanAccess(subscription.Permission, "read", record.Event.Topic, record.Event.DataClassification))
            {
                await QuarantineAsync(record.Event, target, "event-permission-denied", cancellationToken).ConfigureAwait(false);
                completed++;
                continue;
            }
            try
            {
                var delivery = new EventDelivery(record.Event, subscription.ModuleId, subscription.InstanceId, subscription.HandlerId, subscription.Permission.GrantId);
                await _delivery.DeliverAsync(delivery, _timeProvider.GetUtcNow().Add(DeliveryTimeout), cancellationToken).ConfigureAwait(false);
                await WriteAtomicAsync(receipt, new InboxReceipt(record.Event.EventId, target.ConsumerModule, target.HandlerId, _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                await _audit.AppendAsync("event.delivered", target.ConsumerModule.Value, "success", "handler-acknowledged", new Dictionary<string, string?>
                {
                    ["eventId"] = record.Event.EventId.ToString("D"),
                    ["topic"] = record.Event.Topic,
                    ["handler"] = target.HandlerId
                }, cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = checked(target.Attempts + 1);
                if (attempts >= MaximumDeliveryAttempts)
                {
                    await QuarantineAsync(record.Event, target with { Attempts = attempts }, "event-delivery-attempts-exhausted", cancellationToken).ConfigureAwait(false);
                    completed++;
                }
                else
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempts - 1));
                    remaining.Add(target with { Attempts = attempts, NextAttemptAt = _timeProvider.GetUtcNow().Add(delay) });
                    await _audit.AppendAsync("event.delivery", target.ConsumerModule.Value, "retry", exception.GetType().Name, new Dictionary<string, string?>
                    {
                        ["eventId"] = record.Event.EventId.ToString("D"),
                        ["handler"] = target.HandlerId,
                        ["attempt"] = attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        if (remaining.Count == 0) File.Delete(path);
        else await WriteAtomicAsync(path, record with { Targets = remaining }, cancellationToken).ConfigureAwait(false);
        return (completed, remaining.Count > 0);
    }

    private async Task QuarantineAsync(EventEnvelope envelope, OutboxTarget target, string reasonCode, CancellationToken cancellationToken)
    {
        var id = $"{envelope.EventId:N}-{HandlerKey(target.ConsumerModule, target.HandlerId)[..16]}-{Guid.NewGuid():N}";
        var value = new QuarantineRecord(id, envelope, target, reasonCode, _timeProvider.GetUtcNow());
        await WriteAtomicAsync(Path.Combine(_paths.EventQuarantine, id + ".json"), value, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync("event.quarantined", target.ConsumerModule.Value, "quarantined", reasonCode, new Dictionary<string, string?>
        {
            ["eventId"] = envelope.EventId.ToString("D"),
            ["topic"] = envelope.Topic,
            ["handler"] = target.HandlerId,
            ["quarantineId"] = id
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                await DispatchPendingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                await _audit.AppendAsync("event.dispatch", "event-fabric", "failure", exception.GetType().Name, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private string? FindOutbox(Guid eventId) => Directory.EnumerateFiles(_paths.EventOutbox, $"*-{eventId:D}*.json").Order(StringComparer.Ordinal).FirstOrDefault();

    private long NextOutboxTicks(long proposed)
    {
        lock (_registrationGate)
        {
            if (!_sequenceInitialized)
            {
                _lastOutboxTicks = Directory.EnumerateFiles(_paths.EventOutbox, "*.json")
                    .Select(path => Path.GetFileName(path).Split('-', 2)[0])
                    .Select(value => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks) ? ticks : 0)
                    .DefaultIfEmpty(0)
                    .Max();
                _sequenceInitialized = true;
            }
            _lastOutboxTicks = Math.Max(proposed, checked(_lastOutboxTicks + 1));
            return _lastOutboxTicks;
        }
    }

    private string ReceiptPath(ModuleId moduleId, string handlerId, Guid eventId)
    {
        var directory = Path.Combine(_paths.EventInbox, HandlerKey(moduleId, handlerId));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, eventId.ToString("D") + ".json");
    }

    private static string HandlerKey(ModuleId moduleId, string handlerId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(moduleId.Value + "\n" + handlerId)));

    private static (string Digest, JsonSchema Schema) LoadSchema(string contentPath, string relativePath)
    {
        var path = ResolveInside(contentPath, relativePath);
        if (!File.Exists(path)) throw new InvalidDataException($"Event schema '{relativePath}' is missing.");
        var bytes = File.ReadAllBytes(path);
        return ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), JsonSchema.FromText(Encoding.UTF8.GetString(bytes)));
    }

    private static string ResolveInside(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Event schema path '{relative}' escapes bundle content.");
        return path;
    }

    private static void RejectDuplicates(IReadOnlyList<EventPublicationRegistration> publications, IReadOnlyList<EventSubscriptionRegistration> subscriptions, ModuleId moduleId)
    {
        var publication = publications.GroupBy(value => value.Topic, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (publication is not null) throw new InvalidDataException($"Module '{moduleId}' declares event publication '{publication.Key}' more than once.");
        var subscription = subscriptions.GroupBy(value => (value.Topic, value.HandlerId)).FirstOrDefault(group => group.Count() > 1);
        if (subscription is not null) throw new InvalidDataException($"Module '{moduleId}' declares event handler '{subscription.Key.HandlerId}' more than once for topic '{subscription.Key.Topic}'.");
    }

    private static bool CanAccess(PermissionDecision permission, string operation, string topic, DataClassification classification)
    {
        if (classification == DataClassification.Public && permission.Granted) return true;
        if (!permission.Granted || permission.Grant.ValueKind != JsonValueKind.Object ||
            !permission.Grant.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(operation, out var values) || values.ValueKind != JsonValueKind.Array)
            return false;
        var allowed = values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        return allowed.Contains("*") || allowed.Contains("event:*") || allowed.Contains(topic) || allowed.Contains("event:" + topic) || allowed.Contains(Classification(classification));
    }

    private static string Classification(DataClassification classification) => classification switch
    {
        DataClassification.Public => "public",
        DataClassification.Internal => "internal",
        DataClassification.Personal => "personal",
        DataClassification.Sensitive => "sensitive",
        DataClassification.Restricted => "restricted",
        _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, "Unknown data classification.")
    };

    private static void ValidatePayload(JsonSchema schema, JsonElement payload)
    {
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > RuntimeConstants.MaximumProtocolFrameBytes)
            throw new InvalidDataException("Event payload exceeds the protocol frame limit; use an artifact reference.");
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.Flag, RequireFormatValidation = true });
        if (!result.IsValid) throw new InvalidDataException("Event payload does not satisfy its declared schema.");
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, ProtocolJson.Options, cancellationToken).ConfigureAwait(false) ?? throw new JsonException($"Event record '{path}' is empty.");
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, ProtocolJson.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Signal()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_processing is not null) await _processing.ConfigureAwait(false);
        _dispatchGate.Dispose();
        _publishGate.Dispose();
        _signal.Dispose();
        _shutdown.Dispose();
    }
}
