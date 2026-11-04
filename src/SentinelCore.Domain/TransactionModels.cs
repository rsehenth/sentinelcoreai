namespace SentinelCore.Domain;

public enum TransactionState
{
    Planned,
    PreflightPassed,
    BackupPrepared,
    BackupVerified,
    Authorized,
    ExecutionStarted,
    ExecutionCompleted,
    ExecutionFailed,
    ExecutionIndeterminate,
    VerificationPassed,
    VerificationFailed,
    RecoveryRequested,
    RecoveryStarted,
    RecoveryCompleted,
    RecoveryFailed,
    Interrupted,
    Cancelled,
    Blocked
}

public sealed record TransactionEventRecord(
    int FormatVersion,
    Guid TransactionId,
    int Sequence,
    TransactionState State,
    DateTimeOffset AtUtc,
    string OperationId,
    int OperationVersion,
    string CatalogVersion,
    string PlanHash,
    string? Detail,
    string? Error,
    OperationPlan? Plan,
    BackupRecord? Backup,
    string? BackupPath,
    AuthorizationToken? Authorization,
    IReadOnlyList<StepResult>? Results,
    VerificationResult? Verification,
    RecoveryResult? Recovery,
    IReadOnlyList<string> Limitations);

public sealed record TransactionRecord(
    Guid TransactionId,
    Guid PlanId,
    string OperationId,
    int OperationVersion,
    string CatalogVersion,
    string PlanHash,
    TransactionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    OperationPlan? Plan,
    BackupRecord? Backup,
    string? BackupPath,
    AuthorizationToken? Authorization,
    IReadOnlyList<TransactionEventRecord> Events,
    IReadOnlyList<StepResult> Steps,
    VerificationResult? Verification,
    RecoveryResult? Recovery,
    bool IsIndeterminate,
    bool Succeeded,
    IReadOnlyList<string> Limitations);

public sealed record TransactionJournalSnapshot(
    IReadOnlyList<TransactionRecord> Transactions,
    IReadOnlyList<string> Warnings);

public sealed record TransactionExecutionResult(
    TransactionRecord? Transaction,
    bool Succeeded,
    IReadOnlyList<BlockerCode> Blockers,
    string Summary);

public interface ITransactionJournalStore
{
    string GetTransactionReportPath(Guid transactionId);
    Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry,
        CancellationToken cancellationToken = default);
    Task<string> WriteBackupAsync(BackupRecord backup, CancellationToken cancellationToken = default);
    Task<BackupRecord> ReadBackupAsync(string path, CancellationToken cancellationToken = default);
    Task<TransactionJournalSnapshot> LoadTransactionsAsync(CancellationToken cancellationToken = default);
    Task<TransactionRecord?> FindTransactionAsync(Guid transactionId,
        CancellationToken cancellationToken = default);
}

public static class TransactionProjection
{
    public static TransactionRecord Build(IReadOnlyList<TransactionEventRecord> events,
        IReadOnlyList<string>? warnings = null)
    {
        if (events.Count == 0) throw new InvalidDataException("Transação sem eventos.");
        var ordered = events.OrderBy(item => item.Sequence).ToList();
        var first = ordered[0];
        var last = ordered[^1];
        if (ordered.Any(item => item.TransactionId != first.TransactionId))
            throw new InvalidDataException("Eventos de transações distintas no mesmo journal.");
        if (ordered.Select(item => item.Sequence).Distinct().Count() != ordered.Count)
            throw new InvalidDataException("Sequência de journal duplicada.");

        if (ordered[0].State != TransactionState.Planned)
            throw new InvalidDataException("Transação não inicia em Planned.");
        for (var index = 1; index < ordered.Count; index++)
            if (!IsAllowedTransition(ordered[index - 1].State, ordered[index].State))
                throw new InvalidDataException(
                    $"Transição inválida no journal: {ordered[index - 1].State} → {ordered[index].State}.");
        var plan = ordered.Select(item => item.Plan).LastOrDefault(item => item is not null);
        var backup = ordered.Select(item => item.Backup).LastOrDefault(item => item is not null);
        var backupPath = ordered.Select(item => item.BackupPath).LastOrDefault(item => item is not null);
        var authorization = ordered.Select(item => item.Authorization).LastOrDefault(item => item is not null);
        var verification = ordered.Select(item => item.Verification).LastOrDefault(item => item is not null);
        var recovery = ordered.Select(item => item.Recovery).LastOrDefault(item => item is not null);
        var results = ordered.Where(item => item.Results is not null)
            .SelectMany(item => item.Results!).GroupBy(item => item.TargetIdentity)
            .Select(group => group.Last()).ToList();
        var limitations = ordered.SelectMany(item => item.Limitations).ToList();
        if (plan is not null) limitations.AddRange(plan.Limitations);
        if (verification is not null) limitations.AddRange(verification.Limitations);
        limitations = limitations.Distinct().ToList();
        if (warnings is not null) limitations.AddRange(warnings);

        var indeterminate = IsIndeterminate(ordered);
        var succeeded = last.State == TransactionState.VerificationPassed && !indeterminate;

        return new TransactionRecord(
            first.TransactionId,
            plan?.PlanId ?? Guid.Empty,
            plan?.OperationId ?? first.OperationId,
            plan?.OperationVersion ?? first.OperationVersion,
            plan?.CatalogVersion ?? first.CatalogVersion,
            plan?.PlanHash ?? first.PlanHash,
            last.State,
            first.AtUtc,
            last.AtUtc,
            plan,
            backup,
            backupPath,
            authorization,
            ordered,
            results,
            verification,
            recovery,
            indeterminate,
            succeeded,
            limitations);
    }

    public static bool IsIndeterminate(IReadOnlyList<TransactionEventRecord> ordered)
    {
        if (ordered.Count == 0) return false;
        var states = ordered.Select(item => item.State).ToList();
        var last = states[^1];
        if (last is TransactionState.Planned or TransactionState.PreflightPassed
            or TransactionState.BackupPrepared or TransactionState.BackupVerified
            or TransactionState.Authorized or TransactionState.RecoveryRequested
            or TransactionState.RecoveryStarted)
            return true;

        var successfulRecovery = -1;
        for (var index = ordered.Count - 1; index >= 0; index--)
            if (ordered[index].State == TransactionState.RecoveryCompleted &&
                ordered[index].Recovery is { FullyRecovered: true })
            {
                successfulRecovery = index;
                break;
            }
        if (successfulRecovery >= 0)
        {
            var afterRecovery = ordered.Skip(successfulRecovery + 1).ToList();
            if (!afterRecovery.Any(item => item.State is TransactionState.ExecutionStarted
                    or TransactionState.ExecutionIndeterminate or TransactionState.Interrupted
                    or TransactionState.RecoveryStarted or TransactionState.RecoveryFailed))
                return false;
        }

        var lastRecoveryStart = states.LastIndexOf(TransactionState.RecoveryStarted);
        if (lastRecoveryStart >= 0 && states.Skip(lastRecoveryStart).All(state =>
                state is not (TransactionState.RecoveryCompleted or TransactionState.RecoveryFailed)))
            return true;
        if (states.Contains(TransactionState.RecoveryFailed))
        {
            var failed = ordered.Last(item => item.State == TransactionState.RecoveryFailed);
            if (failed.Recovery is { EffectMayHaveOccurred: true, FullyRecovered: false }) return true;
        }

        var lastExecutionStart = states.LastIndexOf(TransactionState.ExecutionStarted);
        if (lastExecutionStart < 0) return false;
        var afterExecution = states.Skip(lastExecutionStart + 1).ToList();
        if (afterExecution.Contains(TransactionState.ExecutionFailed)) return false;
        return !afterExecution.Contains(TransactionState.VerificationPassed) &&
               !afterExecution.Contains(TransactionState.VerificationFailed);
    }

    public static bool IsAllowedTransition(TransactionState from, TransactionState to)
    {
        if (to is TransactionState.RecoveryRequested or TransactionState.Interrupted) return true;
        if (to == TransactionState.Cancelled)
            return from is TransactionState.Planned or TransactionState.PreflightPassed
                or TransactionState.BackupPrepared or TransactionState.BackupVerified
                or TransactionState.Authorized;
        if (to == TransactionState.Blocked)
            return from is TransactionState.Planned or TransactionState.PreflightPassed
                or TransactionState.BackupPrepared or TransactionState.BackupVerified
                or TransactionState.Authorized;
        return (from, to) switch
        {
            (TransactionState.Planned, TransactionState.PreflightPassed) => true,
            (TransactionState.PreflightPassed, TransactionState.BackupPrepared) => true,
            (TransactionState.BackupPrepared, TransactionState.BackupVerified) => true,
            (TransactionState.BackupVerified, TransactionState.Authorized) => true,
            (TransactionState.Authorized, TransactionState.ExecutionStarted) => true,
            (TransactionState.ExecutionStarted, TransactionState.ExecutionCompleted) => true,
            (TransactionState.ExecutionStarted, TransactionState.ExecutionFailed) => true,
            (TransactionState.ExecutionStarted, TransactionState.ExecutionIndeterminate) => true,
            (TransactionState.ExecutionCompleted, TransactionState.VerificationPassed) => true,
            (TransactionState.ExecutionCompleted, TransactionState.VerificationFailed) => true,
            (TransactionState.RecoveryRequested, TransactionState.RecoveryStarted) => true,
            (TransactionState.RecoveryRequested, TransactionState.RecoveryFailed) => true,
            (TransactionState.RecoveryStarted, TransactionState.RecoveryCompleted) => true,
            (TransactionState.RecoveryStarted, TransactionState.RecoveryFailed) => true,
            _ => false
        };
    }
}
