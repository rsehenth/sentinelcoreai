using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Domain;

public static class SafetyHashing
{
    public static string PlanHash(Guid planId, Guid transactionId, string operationId, int operationVersion,
        string catalogVersion, RiskLevel risk, OperationScope scope, ProtectionPolicy protection,
        string observedStateHash, IEnumerable<PlannedChange> changes)
    {
        var builder = new StringBuilder();
        Append(builder, "plan-v2");
        Append(builder, planId.ToString("D"));
        Append(builder, transactionId.ToString("D"));
        Append(builder, operationId);
        Append(builder, operationVersion.ToString());
        Append(builder, catalogVersion);
        Append(builder, risk.ToString());
        Append(builder, scope.ToString());
        Append(builder, protection.RequiresBackup ? "1" : "0");
        Append(builder, protection.RequiresFullTarget ? "1" : "0");
        Append(builder, protection.RequiresRestorePoint ? "1" : "0");
        Append(builder, observedStateHash);
        foreach (var change in changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
        {
            Append(builder, change.TargetIdentity);
            Append(builder, change.Target.Root);
            Append(builder, change.Target.Path);
            Append(builder, change.Target.ValueName);
            Append(builder, change.Kind.ToString());
            Append(builder, change.BeforeTargetExists ? "1" : "0");
            Append(builder, change.AfterTargetExists ? "1" : "0");
            AppendState(builder, change.Before);
            AppendState(builder, change.After);
        }
        return Sha256(builder.ToString());
    }

    public static string ObservedStateHash(IEnumerable<ObservedTargetState> states)
    {
        var builder = new StringBuilder();
        Append(builder, "observed-v1");
        foreach (var state in states.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
        {
            Append(builder, state.TargetIdentity);
            Append(builder, state.IsKnown ? "1" : "0");
            Append(builder, state.TargetExists ? "1" : "0");
            if (!state.TargetExists) Append(builder, "absent");
            else if (state.Value is null) Append(builder, "unknown-value");
            else
            {
                Append(builder, state.Value.Exists ? "1" : "0");
                Append(builder, state.Value.Kind);
                Append(builder, state.Value.Data);
            }
        }
        return Sha256(builder.ToString());
    }

    public static string EntryChecksum(BackupEntry entry)
    {
        var builder = new StringBuilder();
        Append(builder, "backup-entry-v1");
        Append(builder, entry.TargetIdentity);
        Append(builder, entry.TargetExists ? "1" : "0");
        if (entry.Value is null) Append(builder, null);
        else
        {
            Append(builder, entry.Value.Exists ? "1" : "0");
            Append(builder, entry.Value.Kind);
            Append(builder, entry.Value.Data);
        }
        return Sha256(builder.ToString());
    }

    public static string BackupContentHash(IEnumerable<BackupEntry> entries)
    {
        var builder = new StringBuilder();
        Append(builder, "backup-v1");
        foreach (var entry in entries.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
        {
            Append(builder, entry.TargetIdentity);
            Append(builder, entry.Checksum);
        }
        return Sha256(builder.ToString());
    }

    public static string CatalogContentHash(IEnumerable<OperationDescriptor> operations)
    {
        var builder = new StringBuilder();
        Append(builder, "catalog-v1");
        foreach (var operation in operations.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Append(builder, operation.Id);
            Append(builder, operation.Version.ToString());
            Append(builder, operation.Category);
            Append(builder, operation.DisplayName);
            Append(builder, operation.Scope.ToString());
            Append(builder, operation.Kind.ToString());
            Append(builder, operation.Risk.ToString());
            Append(builder, operation.Availability.ToString());
            Append(builder, operation.Compatibility.MinimumBuild);
            Append(builder, operation.Compatibility.MaximumBuild);
            Append(builder, operation.Compatibility.Windows11Only ? "1" : "0");
            foreach (var target in operation.Targets.OrderBy(item => item.Identity, StringComparer.Ordinal))
            {
                Append(builder, target.Root);
                Append(builder, target.Path);
                Append(builder, target.ValueName);
            }
            foreach (var change in operation.Changes.OrderBy(item => item.Target.Identity, StringComparer.Ordinal))
            {
                Append(builder, change.Target.Identity);
                AppendState(builder, change.DesiredValue);
            }
            Append(builder, operation.Protection.RequiresBackup ? "1" : "0");
            Append(builder, operation.Protection.RequiresFullTarget ? "1" : "0");
            Append(builder, operation.Protection.RequiresRestorePoint ? "1" : "0");
            foreach (var dependency in operation.Dependencies.OrderBy(item => item, StringComparer.Ordinal))
                Append(builder, dependency);
            foreach (var consequence in operation.Consequences)
                Append(builder, consequence);
            foreach (var risk in operation.KnownRisks)
                Append(builder, risk);
            foreach (var limitation in operation.Limitations)
                Append(builder, limitation);
            Append(builder, operation.ReuseAttribution);
        }
        return Sha256(builder.ToString());
    }

    private static void AppendState(StringBuilder builder, TargetValueState state)
    {
        Append(builder, state.Exists ? "1" : "0");
        Append(builder, state.Kind);
        Append(builder, state.Data);
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
