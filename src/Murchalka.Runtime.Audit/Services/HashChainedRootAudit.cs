using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murchalka.Runtime.Audit.Models;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Common;

namespace Murchalka.Runtime.Audit.Services;

/// <summary>Implements a durable, redacted, hash-chained Root Trust audit.</summary>
public sealed class HashChainedRootAudit : IRootAudit, IAsyncDisposable
{
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;
    private string _lastHash = new('0', 64);

    /// <summary>Initializes the audit and verifies any existing hash chain.</summary>
    /// <param name="paths">The Runtime paths.</param>
    /// <param name="timeProvider">The optional trusted time provider.</param>
    public HashChainedRootAudit(RuntimePaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();
        _path = Path.Combine(paths.Audit, "root-audit.jsonl");
        _timeProvider = timeProvider ?? TimeProvider.System;
        LoadTail();
    }

    /// <inheritdoc/>
    public async ValueTask AppendAsync(string eventType, string subject, string outcome, string reasonCode, IReadOnlyDictionary<string, string?>? details = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var safeDetails = (details ?? new Dictionary<string, string?>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => Redact(pair.Key, pair.Value), StringComparer.Ordinal);
            var sequence = checked(_sequence + 1);
            var timestamp = _timeProvider.GetUtcNow();
            var hashMaterial = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sequence,
                timestamp,
                eventType,
                subject,
                outcome,
                reasonCode,
                details = safeDetails,
                previousHash = _lastHash
            });
            var hash = Convert.ToHexStringLower(SHA256.HashData(hashMaterial));
            var record = new RootAuditRecord(sequence, timestamp, eventType, subject, outcome, reasonCode, safeDetails, _lastHash, hash);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
            _sequence = sequence;
            _lastHash = hash;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Verifies every sequence and digest link in an audit file.</summary>
    /// <param name="path">The audit JSONL path.</param>
    /// <returns>Machine-readable integrity failures, or an empty collection.</returns>
    public static IReadOnlyList<string> Verify(string path)
    {
        if (!File.Exists(path)) return [];
        var failures = new List<string>();
        var expectedSequence = 1L;
        var previous = new string('0', 64);
        foreach (var line in File.ReadLines(path))
        {
            RootAuditRecord? record;
            try { record = JsonSerializer.Deserialize<RootAuditRecord>(line); }
            catch (JsonException) { failures.Add($"invalid-json:{expectedSequence}"); break; }
            if (record is null) { failures.Add($"empty-record:{expectedSequence}"); break; }
            if (record.Sequence != expectedSequence) failures.Add($"sequence:{record.Sequence}");
            if (!string.Equals(record.PreviousHash, previous, StringComparison.Ordinal)) failures.Add($"previous-hash:{record.Sequence}");
            var material = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sequence = record.Sequence,
                timestamp = record.Timestamp,
                eventType = record.EventType,
                subject = record.Subject,
                outcome = record.Outcome,
                reasonCode = record.ReasonCode,
                details = record.Details,
                previousHash = record.PreviousHash
            });
            var computed = Convert.ToHexStringLower(SHA256.HashData(material));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(record.RecordHash))) failures.Add($"record-hash:{record.Sequence}");
            previous = record.RecordHash;
            expectedSequence++;
        }
        return failures;
    }

    private void LoadTail()
    {
        if (!File.Exists(_path)) return;
        var failures = Verify(_path);
        if (failures.Count > 0) throw new InvalidDataException($"Root audit integrity validation failed: {string.Join(',', failures)}.");
        var last = File.ReadLines(_path).LastOrDefault();
        if (last is null) return;
        var record = JsonSerializer.Deserialize<RootAuditRecord>(last) ?? throw new InvalidDataException("Root audit tail is invalid.");
        _sequence = record.Sequence;
        _lastHash = record.RecordHash;
    }

    private static string? Redact(string key, string? value)
    {
        if (value is null) return null;
        if (key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("payload", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase))
            return "[redacted]";
        return value.Length <= 512 ? value : value[..512];
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
