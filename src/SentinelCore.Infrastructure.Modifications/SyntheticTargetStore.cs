using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Modifications;

public sealed class SyntheticTargetStore : ITargetStateReader
{
    private readonly object gate = new();
    private readonly Dictionary<string, StoredTarget> targets = new(StringComparer.Ordinal);

    public Task<ObservedTargetState> ReadAsync(OperationTarget target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(target.Root, "Synthetic", StringComparison.Ordinal))
            return Task.FromResult(ObservedTargetState.Unknown(target.Identity,
                DateTimeOffset.UtcNow, "Raiz não suportada pelo armazenamento sintético."));
        return Task.FromResult(Read(target));
    }

    public ObservedTargetState Read(OperationTarget target)
    {
        lock (gate)
        {
            return targets.TryGetValue(target.Identity, out var stored)
                ? new ObservedTargetState(target.Identity, true, true, stored.Value, DateTimeOffset.UtcNow, null)
                : new ObservedTargetState(target.Identity, true, false, null, DateTimeOffset.UtcNow, null);
        }
    }

    public void Set(OperationTarget target, TargetValueState value)
    {
        lock (gate)
        {
            targets[target.Identity] = new StoredTarget(true, value);
        }
    }

    public void Remove(OperationTarget target)
    {
        lock (gate)
        {
            targets.Remove(target.Identity);
        }
    }

    public void SetTargetWithoutValue(OperationTarget target, string kind = "String")
    {
        lock (gate)
        {
            targets[target.Identity] = new StoredTarget(true, new TargetValueState(false, kind, null));
        }
    }

    public IReadOnlyDictionary<string, ObservedTargetState> Snapshot()
    {
        lock (gate)
        {
            return targets.ToDictionary(item => item.Key,
                item => new ObservedTargetState(item.Key, true, true, item.Value.Value,
                    DateTimeOffset.UtcNow, null), StringComparer.Ordinal);
        }
    }

    private sealed record StoredTarget(bool TargetExists, TargetValueState? Value);
}
