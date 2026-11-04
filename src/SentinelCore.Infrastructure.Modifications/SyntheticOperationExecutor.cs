using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Modifications;

public sealed class SyntheticOperationExecutor : IOperationExecutor
{
    private readonly SyntheticTargetStore store;

    public SyntheticOperationExecutor(SyntheticTargetStore store) => this.store = store;

    public Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) return Task.FromResult(ExecutionOutcome.Failed("Requisição ausente."));
        if (!string.Equals(request.Target.Root, "Synthetic", StringComparison.Ordinal))
            return Task.FromResult(ExecutionOutcome.Failed("Raiz de alvo não suportada pelo adaptador sintético."));
        var current = store.Read(request.Target);
        if (!current.IsKnown)
            return Task.FromResult(ExecutionOutcome.Unknown("Estado atual indisponível."));
        var currentValue = current.TargetExists
            ? current.Value ?? new TargetValueState(false, "Unknown", null)
            : new TargetValueState(false, "Unknown", null);
        if (!ValueEquals(currentValue, request.Before))
            return Task.FromResult(ExecutionOutcome.Failed("Estado mudou antes do efeito; escrita recusada."));

        switch (request.Kind)
        {
            case ChangeKind.SetValue:
                if (!request.After.Exists)
                    return Task.FromResult(ExecutionOutcome.Failed("SetValue sem valor definido."));
                store.Set(request.Target, request.After);
                return Task.FromResult(ExecutionOutcome.Success());
            case ChangeKind.DeleteValue:
                if (!current.TargetExists)
                    return Task.FromResult(ExecutionOutcome.Failed("Alvo inexistente para DeleteValue."));
                store.Set(request.Target, new TargetValueState(false, request.After.Kind, null));
                return Task.FromResult(ExecutionOutcome.Success());
            case ChangeKind.DeleteKey:
                store.Remove(request.Target);
                return Task.FromResult(ExecutionOutcome.Success());
            default:
                return Task.FromResult(ExecutionOutcome.Failed("Tipo de alteração não suportado."));
        }
    }

    private static bool ValueEquals(TargetValueState current, TargetValueState expected)
    {
        if (current.Exists != expected.Exists) return false;
        if (!current.Exists) return true;
        return string.Equals(current.Kind, expected.Kind, StringComparison.Ordinal) &&
               string.Equals(current.Data, expected.Data, StringComparison.Ordinal);
    }
}
