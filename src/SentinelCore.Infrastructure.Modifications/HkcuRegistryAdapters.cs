using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Modifications;

/// <summary>
/// Leitor do estado real de valores HKCU permitidos. Informação inacessível vira estado desconhecido com
/// limitação; nunca tenta contornar permissões.
/// </summary>
public sealed class HkcuRegistryTargetReader : ITargetStateReader
{
    private readonly IHkcuRegistryAccess access;
    private readonly TimeProvider time;

    public HkcuRegistryTargetReader(IHkcuRegistryAccess access, TimeProvider? time = null)
    {
        this.access = access;
        this.time = time ?? TimeProvider.System;
    }

    public Task<ObservedTargetState> ReadAsync(OperationTarget target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = Read(target);
        return Task.FromResult(observed);
    }

    public ObservedTargetState Read(OperationTarget target)
    {
        if (!HkcuTargetPolicy.IsAllowed(target))
            return ObservedTargetState.Unknown(target.Identity, time.GetUtcNow(),
                HkcuTargetPolicy.RejectionReason(target));

        var read = access.Read(target);
        var now = time.GetUtcNow();
        return read.Status switch
        {
            HkcuReadStatus.Success => new ObservedTargetState(target.Identity, true, true,
                new TargetValueState(true, read.Kind, read.Data), now, read.Data is null
                    ? "Valor presente sem conteúdo legível; não será alterado."
                    : null),
            HkcuReadStatus.MissingKey => new ObservedTargetState(target.Identity, true, false, null, now,
                "Chave HKCU ausente; o adaptador não cria chaves."),
            HkcuReadStatus.MissingValue => new ObservedTargetState(target.Identity, true, true,
                new TargetValueState(false, RegistryValueKinds.DWord, null), now, null),
            HkcuReadStatus.AccessDenied => ObservedTargetState.Unknown(target.Identity, now,
                "Acesso negado à chave HKCU: " + read.Error),
            HkcuReadStatus.InvalidTarget => ObservedTargetState.Unknown(target.Identity, now,
                HkcuTargetPolicy.RejectionReason(target)),
            HkcuReadStatus.Unsupported => ObservedTargetState.Unknown(target.Identity, now,
                read.Error ?? "Registro indisponível."),
            _ => ObservedTargetState.Unknown(target.Identity, now,
                "Leitura do Registro indisponível: " + read.Error)
        };
    }
}

/// <summary>
/// Executor real da T2A. Aceita somente SetValue/DeleteValue sobre alvos exatos de HKCU, relê o estado
/// imediatamente antes da escrita e recusa divergência. Não cria/remove chaves, não executa scripts, não
/// solicita elevação e não toca HKLM para escrita.
/// </summary>
public sealed class HkcuRegistryOperationExecutor : IOperationExecutor
{
    private readonly IHkcuRegistryAccess access;

    public HkcuRegistryOperationExecutor(IHkcuRegistryAccess access) => this.access = access;

    public Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Apply(request));
    }

    private ExecutionOutcome Apply(ExecutionRequest request)
    {
        if (request is null) return ExecutionOutcome.Failed("Requisição ausente.");
        if (request.Target.Root != OperationTargetRoots.CurrentUser)
            return ExecutionOutcome.Failed("O executor real opera exclusivamente em HKCU.");
        if (!HkcuTargetPolicy.IsAllowed(request.Target))
            return ExecutionOutcome.Failed(HkcuTargetPolicy.RejectionReason(request.Target));
        if (request.Kind is not (ChangeKind.SetValue or ChangeKind.DeleteValue))
            return ExecutionOutcome.Failed("Tipo de alteração não suportado pelo adaptador HKCU.");

        var read = access.Read(request.Target);
        if (read.Status is HkcuReadStatus.AccessDenied or HkcuReadStatus.Unsupported
            or HkcuReadStatus.InvalidTarget or HkcuReadStatus.Error)
            return ExecutionOutcome.Failed(read.Error ?? "Estado atual indisponível; escrita recusada.");
        if (read.Status == HkcuReadStatus.MissingKey)
            return ExecutionOutcome.Failed("Chave HKCU ausente; o adaptador não cria chaves.");

        var current = read.Status == HkcuReadStatus.MissingValue
            ? new TargetValueState(false, RegistryValueKinds.DWord, null)
            : new TargetValueState(true, read.Kind, read.Data);
        if (!ValueEquals(current, request.Before))
            return ExecutionOutcome.Failed("Estado mudou antes do efeito; escrita recusada.");

        if (request.Kind == ChangeKind.DeleteValue)
        {
            if (!current.Exists) return ExecutionOutcome.Success();
            return MapWrite(access.DeleteValue(request.Target));
        }

        if (!request.After.Exists)
            return ExecutionOutcome.Failed("SetValue sem valor definido.");
        if (!string.Equals(request.After.Kind, RegistryValueKinds.DWord, StringComparison.Ordinal))
            return ExecutionOutcome.Failed("O adaptador HKCU aceita somente REG_DWORD nesta fase.");
        if (!int.TryParse(request.After.Data, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return ExecutionOutcome.Failed("Conteúdo DWORD inválido.");
        return MapWrite(access.SetDWord(request.Target, value));
    }

    private static ExecutionOutcome MapWrite(HkcuWriteResult result) => result.Status switch
    {
        HkcuWriteStatus.Success => ExecutionOutcome.Success(),
        HkcuWriteStatus.AccessDenied => ExecutionOutcome.Failed(
            result.Error ?? "Acesso negado à escrita HKCU."),
        HkcuWriteStatus.MissingKey => ExecutionOutcome.Failed("Chave HKCU ausente."),
        HkcuWriteStatus.InvalidTarget => ExecutionOutcome.Failed(
            result.Error ?? "Alvo fora do catálogo HKCU."),
        HkcuWriteStatus.Unsupported => ExecutionOutcome.Failed(
            result.Error ?? "Registro indisponível."),
        _ => ExecutionOutcome.Unknown(result.Error ?? "Falha de escrita com efeito indeterminado.")
    };

    private static bool ValueEquals(TargetValueState current, TargetValueState expected)
    {
        if (current.Exists != expected.Exists) return false;
        if (!current.Exists) return true;
        return string.Equals(current.Kind, expected.Kind, StringComparison.Ordinal) &&
               string.Equals(current.Data, expected.Data, StringComparison.Ordinal);
    }
}
