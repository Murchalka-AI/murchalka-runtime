namespace Murchalka.Runtime.Migrations.Internal;

internal sealed record MigrationLedger(string ModuleId, string Namespace, string Version, string LastOperation, DateTimeOffset UpdatedAt);
