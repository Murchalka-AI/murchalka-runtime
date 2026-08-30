using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Secrets;
using Murchalka.Runtime.ModuleGateway.Protocol;

namespace Murchalka.Runtime.ModuleGateway.Sessions;

/// <summary>Provides serialized request-response communication with an authenticated module process.</summary>
public sealed class ModuleGatewaySession : IModuleGatewaySession
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Channel<GatewayFrame> _responses = Channel.CreateBounded<GatewayFrame>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<GatewayFrame> _publications = Channel.CreateBounded<GatewayFrame>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<GatewayFrame> _secretRequests = Channel.CreateBounded<GatewayFrame>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<GatewayFrame> _dependencyInvocations = Channel.CreateBounded<GatewayFrame>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, byte> _abandonedInvocations = new();
    private readonly ConcurrentDictionary<Guid, byte> _queuedDependencyInvocations = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDependencyInvocations = new();
    private readonly ConcurrentDictionary<Guid, string> _dependencyCancellationReasons = new();
    private readonly ConcurrentDictionary<Guid, Task> _dependencyInvocationTasks = new();
    private readonly Task _reader;
    private readonly Task _publicationReader;
    private readonly Task _secretRequestReader;
    private readonly Task _dependencyInvocationReader;
    private Func<EventEnvelope, CancellationToken, Task<EventEnvelope>>? _eventPublisher;
    private Func<SecretLeaseRequest, CancellationToken, Task<SecretLease>>? _secretBroker;
    private Func<InvocationEnvelope, CancellationToken, Task<ResultEnvelope>>? _dependencyInvoker;
    private DependencyEndpointsSnapshot _dependencies;
    private bool _disposed;

    internal ModuleGatewaySession(Stream stream, ModuleHello hello, ModuleReady ready, DependencyEndpointsSnapshot dependencies)
    {
        _stream = stream;
        Hello = hello;
        Ready = ready;
        _dependencies = dependencies;
        _reader = ReadLoopAsync(_shutdown.Token);
        _publicationReader = ReadPublicationsAsync(_shutdown.Token);
        _secretRequestReader = ReadSecretRequestsAsync(_shutdown.Token);
        _dependencyInvocationReader = ReadDependencyInvocationsAsync(_shutdown.Token);
    }

    /// <summary>Gets the module hello message validated during the handshake.</summary>
    public ModuleHello Hello { get; }
    /// <summary>Gets the module readiness message validated during the handshake.</summary>
    public ModuleReady Ready { get; }
    /// <inheritdoc />
    public InstanceId InstanceId => Hello.InstanceId;

    /// <inheritdoc />
    public async Task<ModuleHealth> ProbeHealthAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var operation = CreateControl(ControlMessageKind.HealthProbe, timeout);
        var frame = await ExchangeAsync("control", operation, "health", cancellationToken).ConfigureAwait(false);
        return GatewayFrameCodec.PayloadAs<ModuleHealth>(frame);
    }

    /// <inheritdoc />
    public async Task<ControlResult> SendControlAsync(ControlMessageKind kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var operation = CreateControl(kind, timeout);
        return await SendControlAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ControlResult> UpdateDependenciesAsync(DependencyEndpointsSnapshot snapshot, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var data = JsonSerializer.SerializeToElement(snapshot, ProtocolJson.Options);
        var result = await SendControlAsync(new ControlMessage(Guid.NewGuid().ToString("N"), ControlMessageKind.UpdateBindings, DateTimeOffset.UtcNow.Add(timeout), data), cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) Volatile.Write(ref _dependencies, snapshot);
        return result;
    }

    /// <inheritdoc />
    public Task<ControlResult> UpdateConfigurationAsync(ConfigurationSnapshot snapshot, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var data = JsonSerializer.SerializeToElement(snapshot, ProtocolJson.Options);
        return SendControlAsync(new ControlMessage(Guid.NewGuid().ToString("N"), ControlMessageKind.ReloadConfiguration, DateTimeOffset.UtcNow.Add(timeout), data), cancellationToken);
    }

    /// <inheritdoc />
    public void SetEventPublisher(Func<EventEnvelope, CancellationToken, Task<EventEnvelope>> publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (Interlocked.CompareExchange(ref _eventPublisher, publisher, null) is not null)
            throw new InvalidOperationException("An event publisher is already registered for this session.");
    }

    /// <inheritdoc />
    public void SetSecretBroker(Func<SecretLeaseRequest, CancellationToken, Task<SecretLease>> broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (Interlocked.CompareExchange(ref _secretBroker, broker, null) is not null)
            throw new InvalidOperationException("A secret broker is already registered for this session.");
    }

    /// <inheritdoc />
    public void SetDependencyInvoker(Func<InvocationEnvelope, CancellationToken, Task<ResultEnvelope>> invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        if (Interlocked.CompareExchange(ref _dependencyInvoker, invoker, null) is not null)
            throw new InvalidOperationException("A dependency invoker is already registered for this session.");
    }

    private async Task<ControlResult> SendControlAsync(ControlMessage operation, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadlineToken(operation.Deadline, cancellationToken);
        GatewayFrame frame;
        try { frame = await ExchangeAsync("control", operation, "controlResult", deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Module control operation '{operation.Kind}' exceeded its deadline.");
        }
        var result = GatewayFrameCodec.PayloadAs<ControlResult>(frame);
        if (result.OperationId != operation.OperationId) throw new InvalidDataException("Control result operation id does not match the request.");
        return result;
    }

    /// <inheritdoc />
    public async Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Deadline <= DateTimeOffset.UtcNow) throw new TimeoutException("Invocation deadline has elapsed.");
        using var deadline = CreateDeadlineToken(invocation.Deadline, cancellationToken);
        GatewayFrame frame;
        try
        {
            await _exchangeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                await WriteAsync("invocation", invocation, deadline.Token).ConfigureAwait(false);
                try { frame = await ReadExpectedResponseAsync("result", deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    _abandonedInvocations.TryAdd(invocation.InvocationId, 0);
                    await TrySendCancellationAsync(invocation.InvocationId,
                        cancellationToken.IsCancellationRequested ? "caller-cancelled" : "deadline-exceeded").ConfigureAwait(false);
                    throw;
                }
            }
            finally { _exchangeGate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Module invocation exceeded its deadline.");
        }
        var result = GatewayFrameCodec.PayloadAs<ResultEnvelope>(frame);
        if (result.InvocationId != invocation.InvocationId) throw new InvalidDataException("Result invocation id does not match the request.");
        return result;
    }

    private async Task<GatewayFrame> ExchangeAsync<T>(string requestKind, T request, string responseKind, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(requestKind, request, cancellationToken).ConfigureAwait(false);
            return await ReadExpectedResponseAsync(responseKind, cancellationToken).ConfigureAwait(false);
        }
        finally { _exchangeGate.Release(); }
    }

    private async Task<GatewayFrame> ReadExpectedResponseAsync(string responseKind, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await _responses.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(frame.Kind, "result", StringComparison.Ordinal))
            {
                var result = GatewayFrameCodec.PayloadAs<ResultEnvelope>(frame);
                if (_abandonedInvocations.TryRemove(result.InvocationId, out _)) continue;
            }
            if (!string.Equals(frame.Kind, responseKind, StringComparison.Ordinal))
                throw new InvalidDataException($"Expected protocol frame '{responseKind}', received '{frame.Kind}'.");
            return frame;
        }
    }

    private async Task TrySendCancellationAsync(Guid invocationId, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await WriteAsync("invocationCancellation", new { invocationId, reason }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static CancellationTokenSource CreateDeadlineToken(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) result.Cancel();
        else result.CancelAfter(remaining);
        return result;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await GatewayFrameCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (string.Equals(frame.Kind, "eventPublication", StringComparison.Ordinal))
                {
                    await _publications.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(frame.Kind, "secretLeaseRequest", StringComparison.Ordinal))
                {
                    await _secretRequests.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(frame.Kind, "capabilityInvocation", StringComparison.Ordinal))
                {
                    var invocation = GatewayFrameCodec.PayloadAs<InvocationEnvelope>(frame);
                    if (!_queuedDependencyInvocations.TryAdd(invocation.InvocationId, 0))
                        throw new InvalidDataException("Duplicate dependency invocation identifier.");
                    await _dependencyInvocations.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(frame.Kind, "capabilityCancellation", StringComparison.Ordinal))
                {
                    var cancellation = GatewayFrameCodec.PayloadAs<JsonElement>(frame);
                    var invocationId = cancellation.GetProperty("invocationId").GetGuid();
                    var reason = cancellation.GetProperty("reason").GetString() ?? "caller-cancelled";
                    if (_activeDependencyInvocations.TryGetValue(invocationId, out var active))
                        active.Cancel();
                    else if (_queuedDependencyInvocations.ContainsKey(invocationId))
                        _dependencyCancellationReasons[invocationId] = reason;
                    continue;
                }
                await _responses.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            _responses.Writer.TryComplete(failure);
            _publications.Writer.TryComplete(failure);
            _secretRequests.Writer.TryComplete(failure);
            _dependencyInvocations.Writer.TryComplete(failure);
        }
    }

    private async Task ReadSecretRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _secretRequests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var request = GatewayFrameCodec.PayloadAs<SecretLeaseRequest>(frame);
                ControlResult result;
                if (_secretBroker is null)
                {
                    result = new ControlResult(request.OperationId, false, "secret-broker-unavailable", "Secret leases are unavailable before activation.", null);
                }
                else
                {
                    try
                    {
                        var lease = await _secretBroker(request, cancellationToken).ConfigureAwait(false);
                        result = new ControlResult(request.OperationId, true, null, null, JsonSerializer.SerializeToElement(lease, ProtocolJson.Options));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        result = new ControlResult(request.OperationId, false, SecretFailureCode(exception), "The secret lease request was rejected.", null);
                    }
                }
                await WriteAsync("secretLeaseResult", result, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static string SecretFailureCode(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "secret-not-granted",
        KeyNotFoundException => "secret-not-configured",
        TimeoutException => "secret-request-expired",
        ArgumentException => "secret-request-invalid",
        _ => "secret-broker-failed"
    };

    private async Task ReadDependencyInvocationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _dependencyInvocations.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var invocation = GatewayFrameCodec.PayloadAs<InvocationEnvelope>(frame);
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (!_activeDependencyInvocations.TryAdd(invocation.InvocationId, lifetime))
                {
                    lifetime.Dispose();
                    _queuedDependencyInvocations.TryRemove(invocation.InvocationId, out _);
                    continue;
                }
                _queuedDependencyInvocations.TryRemove(invocation.InvocationId, out _);
                if (_dependencyCancellationReasons.TryRemove(invocation.InvocationId, out _)) lifetime.Cancel();
                var task = TrackDependencyInvocationAsync(invocation, lifetime, cancellationToken);
                _dependencyInvocationTasks[invocation.InvocationId] = task;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task TrackDependencyInvocationAsync(
        InvocationEnvelope invocation,
        CancellationTokenSource lifetime,
        CancellationToken shutdownToken)
    {
        await Task.Yield();
        await HandleDependencyInvocationAsync(invocation, lifetime, shutdownToken).ConfigureAwait(false);
    }

    private async Task HandleDependencyInvocationAsync(
        InvocationEnvelope invocation,
        CancellationTokenSource lifetime,
        CancellationToken shutdownToken)
    {
        try
        {
            ResultEnvelope result;
            var endpoint = Volatile.Read(ref _dependencies).Endpoints.SingleOrDefault(value =>
                value.ProviderInstance == invocation.ProviderInstance &&
                value.Capability == invocation.CapabilityId &&
                value.CapabilityVersion == invocation.CapabilityVersion);
            if (invocation.ConsumerModuleId != Hello.ModuleId)
                result = DependencyFailure(invocation.InvocationId, "consumer-identity-mismatch", ErrorCategory.PermissionDenied, "Invocation consumer does not match the authenticated module session.");
            else if (endpoint is null || !string.Equals(endpoint.AuthorizationReference, invocation.AuthorizationGrantReference, StringComparison.Ordinal))
                result = DependencyFailure(invocation.InvocationId, "dependency-not-granted", ErrorCategory.PermissionDenied, "Invocation does not match a current granted dependency endpoint.");
            else if (_dependencyInvoker is null)
                result = DependencyFailure(invocation.InvocationId, "dependency-router-unavailable", ErrorCategory.Unavailable, "Dependency routing is unavailable before activation.");
            else
            {
                try { result = await _dependencyInvoker(invocation, lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    result = new ResultEnvelope(invocation.InvocationId, InvocationStatus.Cancelled, null,
                        new ProtocolError("invocation-cancelled", ErrorCategory.Cancelled, false, "Dependency invocation was cancelled by its caller.", null), null, [], [], null);
                }
                catch (Exception)
                {
                    result = DependencyFailure(invocation.InvocationId, "dependency-invocation-failed", ErrorCategory.Unavailable, "Granted dependency invocation failed.");
                }
            }
            await WriteAsync("capabilityResult", result, shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { }
        finally
        {
            _activeDependencyInvocations.TryRemove(invocation.InvocationId, out _);
            _dependencyInvocationTasks.TryRemove(invocation.InvocationId, out _);
            _dependencyCancellationReasons.TryRemove(invocation.InvocationId, out _);
            lifetime.Dispose();
        }
    }

    private static ResultEnvelope DependencyFailure(Guid invocationId, string code, ErrorCategory category, string message) =>
        new(invocationId, InvocationStatus.Rejected, null, new ProtocolError(code, category, false, message, null), null, [], [], null);

    private async Task ReadPublicationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _publications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await HandleEventPublicationAsync(frame, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var rejected = new ControlResult("unknown", false, "event-frame-invalid", exception.Message, null);
                    await WriteAsync("eventPublicationResult", rejected, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleEventPublicationAsync(GatewayFrame frame, CancellationToken cancellationToken)
    {
        var envelope = GatewayFrameCodec.PayloadAs<EventEnvelope>(frame);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ControlResult result;
            if (envelope.ProducerModule != Hello.ModuleId || envelope.ProducerInstance != Hello.InstanceId)
                result = new ControlResult(envelope.EventId.ToString("D"), false, "event-producer-identity-mismatch", "Event producer identity does not match the authenticated session.", null);
            else if (_eventPublisher is null)
                result = new ControlResult(envelope.EventId.ToString("D"), false, "event-publisher-unavailable", "Event publication is not available before activation.", null);
            else
            {
                try
                {
                    var published = await _eventPublisher(envelope, cancellationToken).ConfigureAwait(false);
                    result = new ControlResult(envelope.EventId.ToString("D"), true, null, null, JsonSerializer.SerializeToElement(published, ProtocolJson.Options));
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    result = new ControlResult(envelope.EventId.ToString("D"), false, "event-publication-rejected", exception.Message, null);
                }
            }
            await GatewayFrameCodec.WriteAsync(_stream, "eventPublicationResult", result, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private async Task WriteAsync<T>(string kind, T value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await GatewayFrameCodec.WriteAsync(_stream, kind, value, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private static ControlMessage CreateControl(ControlMessageKind kind, TimeSpan timeout)
    {
        using var empty = JsonDocument.Parse("{}");
        return new ControlMessage(Guid.NewGuid().ToString("N"), kind, DateTimeOffset.UtcNow.Add(timeout), empty.RootElement.Clone());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); }
        catch (Exception) when (_shutdown.IsCancellationRequested) { }
        try { await _publicationReader.ConfigureAwait(false); }
        catch (Exception) when (_shutdown.IsCancellationRequested) { }
        try { await _secretRequestReader.ConfigureAwait(false); }
        catch (Exception) when (_shutdown.IsCancellationRequested) { }
        try { await _dependencyInvocationReader.ConfigureAwait(false); }
        catch (Exception) when (_shutdown.IsCancellationRequested) { }
        foreach (var invocation in _activeDependencyInvocations.Values) invocation.Cancel();
        await Task.WhenAll(_dependencyInvocationTasks.Values).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _exchangeGate.Dispose();
        _writeGate.Dispose();
        _shutdown.Dispose();
    }
}
