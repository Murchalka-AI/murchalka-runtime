using Murchalka.Runtime.Audit.Services;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies the integrity and redaction behavior of the root audit trail.</summary>
public sealed class AuditTests
{
    /// <summary>Verifies that audit record tampering is detected and sensitive values are redacted.</summary>
    [Fact]
    public async Task AuditHashChainDetectsModificationAndRedactsSecrets()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        await using var audit = new HashChainedRootAudit(paths);
        await audit.AppendAsync(
            "bundle.verified",
            "dev.murchalka.hello",
            "success",
            "valid",
            new Dictionary<string, string?> { ["secretToken"] = "must-not-appear" },
            TestContext.Current.CancellationToken);
        var path = Path.Combine(paths.Audit, "root-audit.jsonl");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("must-not-appear", text, StringComparison.Ordinal);
        Assert.Empty(HashChainedRootAudit.Verify(path));

        await File.WriteAllTextAsync(
            path,
            text.Replace("bundle.verified", "bundle.rejected", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        Assert.Contains("record-hash:1", HashChainedRootAudit.Verify(path));
    }
}
