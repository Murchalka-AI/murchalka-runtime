using Murchalka.Runtime.Contracts.Abstractions;

namespace Murchalka.Runtime.Tests.Infrastructure;

internal sealed class NoopRootAudit : IRootAudit
{
    public ValueTask AppendAsync(string eventType, string subject, string outcome, string reasonCode, IReadOnlyDictionary<string, string?>? details = null, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
