using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class RecoveryService
{
    private readonly ITransactionJournalStore store;
    private readonly ITargetStateReader reader;
    private readonly IOperationExecutor executor;
    private readonly IExclusiveLockFactory locks;
    private readonly TimeProvider time;
    private readonly IOperationCatalog? catalog;
    private readonly IRecoveryConfirmationPolicy? confirmationPolicy;

    public RecoveryService(ITransactionJournalStore store, ITargetStateReader reader,
        IOperationExecutor executor, IExclusiveLockFactory locks, TimeProvider? time = null,
        IOperationCatalog? catalog = null, IRecoveryConfirmationPolicy? confirmationPolicy = null)
    {
        this.store = store;
        this.reader = reader;
        this.executor = executor;
        this.locks = locks;
        this.time = time ?? TimeProvider.System;
        this.catalog = catalog;
        this.confirmationPolicy = confirmationPolicy;
    }

    public async Task<RecoveryPreview> PreviewAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        TransactionRecord? transaction;
        try
        {
            transaction = await store.FindTransactionAsync(transactionId, cancellationToken);
        }
        catch (IOException error)
        {
            return NotRecoverable(transactionId, "Journal ocupado ou indisponível: " + error.Message, []);
        }
        if (transaction is null)
            return NotRecoverable(transactionId, "Transação não encontrada.", []);
        if (transaction.Plan is null)
            return NotRecoverable(transactionId, "Transação sem plano recuperável.", transaction.Limitations);

        var plan = transaction.Plan;
        if (!MatchesCatalog(plan, out var catalogReason))
            return NotRecoverable(transactionId, catalogReason, transaction.Limitations);
        var limitations = transaction.Limitations.ToList();
        if (!HasEffectStarted(transaction))
            return await PreviewWithoutEffectAsync(transaction, plan, limitations, cancellationToken);
        if (transaction.BackupPath is null)
            return NotRecoverable(transactionId, "Transação sem backup persistido.", transaction.Limitations);

        BackupValidation validation;
        try
        {
            var persisted = await store.ReadBackupAsync(transaction.BackupPath, cancellationToken);
            validation = BackupService.ValidateBackup(persisted, plan);
        }
        catch (FileNotFoundException error)
        {
            return NotRecoverable(transactionId, "Backup ausente: " + error.Message, transaction.Limitations);
        }
        catch (DirectoryNotFoundException error)
        {
            return NotRecoverable(transactionId, "Backup ausente: " + error.Message, transaction.Limitations);
        }
        catch (InvalidDataException error)
        {
            return NotRecoverable(transactionId, "Backup corrompido: " + error.Message, transaction.Limitations);
        }
        catch (IOException error)
        {
            return NotRecoverable(transactionId, "Backup ilegível: " + error.Message, transaction.Limitations);
        }

        if (!validation.Valid || validation.Backup is null)
            return NotRecoverable(transactionId, validation.Reason ?? "Backup inválido.", transaction.Limitations);
        var backup = validation.Backup;

        limitations = transaction.Limitations.Concat(backup.Limitations).Distinct().ToList();
        if (transaction.IsIndeterminate)
            limitations.Add("Transação indeterminada: o efeito pode ter ocorrido sem verificação.");

        var conflicts = new List<RecoveryConflict>();
        var steps = new List<StepResult>();
        var allRestored = true;

        foreach (var change in plan.Changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
        {
            var entry = backup.Entries.First(item => item.TargetIdentity == change.TargetIdentity);
            ObservedTargetState current;
            try
            {
                current = await reader.ReadAsync(change.Target, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                current = ObservedTargetState.Unknown(change.TargetIdentity, time.GetUtcNow(), error.Message);
            }

            if (MatchesBefore(current, entry))
            {
                steps.Add(new StepResult(change.TargetIdentity, false, false, true, null,
                    ["Já está no estado observado."]));
                continue;
            }

            allRestored = false;
            if (VerificationEngine.Matches(current, change))
            {
                steps.Add(new StepResult(change.TargetIdentity, false, false, false, null,
                    ["Estado posterior confirmado; recuperável."]));
                continue;
            }

            var kind = !current.IsKnown
                ? RecoveryConflictKind.UnknownState
                : !entry.TargetExists && current.TargetExists
                    ? RecoveryConflictKind.StateChangedAfterTransaction
                    : entry.TargetExists && !current.TargetExists
                        ? RecoveryConflictKind.TargetMissing
                        : current.Value is { Exists: true } observedValue &&
                          entry.Value is { Exists: true } expectedValue &&
                          !string.Equals(observedValue.Kind, expectedValue.Kind, StringComparison.Ordinal)
                            ? RecoveryConflictKind.KindChanged
                            : RecoveryConflictKind.StateChangedAfterTransaction;

            conflicts.Add(new RecoveryConflict(change.TargetIdentity, kind, entry.Value, change.After,
                current.Value,
                "O estado atual não corresponde ao estado posterior nem ao estado observado."));
            steps.Add(new StepResult(change.TargetIdentity, false, false, false,
                "Conflito de recuperação.", ["Recuperação não sobrescreve o estado atual."]));
        }

        if (conflicts.Count > 0)
        {
            var recoverable = steps.Count(item => !item.Verified && item.Error is null);
            var canRecover = recoverable > 0;
            return new RecoveryPreview(transactionId, canRecover, RecoveryOutcome.Conflict, conflicts,
                canRecover
                    ? "Conflito parcial: somente os alvos recuperáveis serão restaurados; " +
                      "o conflito não será sobrescrito."
                    : "Estado alterado após a transação; recuperação bloqueada.",
                steps, limitations);
        }

        var outcome = allRestored ? RecoveryOutcome.AlreadyRestored
            : transaction.IsIndeterminate ? RecoveryOutcome.Interrupted
            : RecoveryOutcome.NotAttempted;
        return new RecoveryPreview(transactionId, true, outcome, [], null, steps, limitations);
    }

    private async Task<RecoveryPreview> PreviewWithoutEffectAsync(TransactionRecord transaction,
        OperationPlan plan, List<string> limitations, CancellationToken cancellationToken)
    {
        var steps = new List<StepResult>();
        var conflicts = new List<RecoveryConflict>();
        foreach (var change in plan.Changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
        {
            ObservedTargetState current;
            try
            {
                current = await reader.ReadAsync(change.Target, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                current = ObservedTargetState.Unknown(change.TargetIdentity, time.GetUtcNow(), error.Message);
            }

            if (MatchesState(current, change.BeforeTargetExists, change.Before))
            {
                steps.Add(new StepResult(change.TargetIdentity, false, false, true, null,
                    ["Nenhum efeito registrado; estado observado confere."]));
                continue;
            }

            var kind = current.IsKnown
                ? RecoveryConflictKind.StateChangedAfterTransaction
                : RecoveryConflictKind.UnknownState;
            conflicts.Add(new RecoveryConflict(change.TargetIdentity, kind, change.Before, change.After,
                current.Value,
                "Nenhum efeito foi registrado; o estado atual difere do observado e não será sobrescrito."));
            steps.Add(new StepResult(change.TargetIdentity, false, false, false,
                "Conflito sem efeito registrado.", ["Recuperação não sobrescreve o estado atual."]));
        }

        var allObserved = conflicts.Count == 0;
        return new RecoveryPreview(transaction.TransactionId, allObserved,
            allObserved ? RecoveryOutcome.AlreadyRestored : RecoveryOutcome.Conflict, conflicts,
            allObserved
                ? "Nenhum efeito foi iniciado; o estado observado confere."
                : "Nenhum efeito foi iniciado, mas o estado atual difere do observado; nada será sobrescrito.",
            steps, limitations.Concat(["Execução não iniciada."]).Distinct().ToList());
    }

    public async Task<RecoveryResult> RollbackAsync(Guid transactionId, RecoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = time.GetUtcNow();
        var confirmationValid = confirmationPolicy is null
            ? SafetyText.IsValidConfirmation(options.UserConfirmationId)
            : confirmationPolicy.IsValid(transactionId, options.UserConfirmationId);
        if (!confirmationValid)
            return Result(transactionId, RecoveryOutcome.Blocked, false, false, [], [],
                "Recuperação exige confirmação explícita válida e vinculada à transação.", started, []);
        TransactionRecord? transaction;
        try
        {
            transaction = await store.FindTransactionAsync(transactionId, cancellationToken);
        }
        catch (IOException error)
        {
            return Result(transactionId, RecoveryOutcome.Blocked, false, false, [], [],
                "Journal ocupado ou indisponível: " + error.Message, started, []);
        }
        if (transaction is null)
            return Result(transactionId, RecoveryOutcome.NotRecoverable, false, false, [], [],
                "Transação não encontrada.", started, []);

        var plan = transaction.Plan;
        if (plan is null)
            return await FailWithoutEffectAsync(transaction, RecoveryOutcome.NotRecoverable,
                "Transação sem plano recuperável.", started, cancellationToken);
        if (!MatchesCatalog(plan, out var catalogReason))
            return await FailWithoutEffectAsync(transaction, RecoveryOutcome.NotRecoverable,
                catalogReason, started, cancellationToken);

        var steps = new List<StepResult>();
        var conflicts = new List<RecoveryConflict>();
        var effectOccurred = false;
        var handles = new List<IDisposable>();
        try
        {
            foreach (var key in plan.Changes.Select(item => item.TargetIdentity)
                         .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
                handles.Add(locks.Acquire(key));

            var preview = await PreviewAsync(transactionId, cancellationToken);
            if (preview.Outcome == RecoveryOutcome.NotRecoverable ||
                (preview.Outcome == RecoveryOutcome.Conflict && !preview.CanRecover))
                return await FailWithoutEffectAsync(transaction, preview.Outcome,
                    preview.Reason ?? "Recuperação bloqueada.", started, cancellationToken,
                    preview.CurrentStates, preview.Conflicts);

            if (preview.Outcome == RecoveryOutcome.AlreadyRestored)
            {
                var noopSequence = NextSequence(transaction);
                await AppendAsync(transaction, noopSequence, TransactionState.RecoveryRequested,
                    cancellationToken, detail: $"Confirmação {options.UserConfirmationId}.");
                await AppendAsync(transaction, noopSequence + 1, TransactionState.RecoveryStarted,
                    cancellationToken, detail: "Recuperação iniciada.");
                var noop = Result(transactionId, RecoveryOutcome.AlreadyRestored, true, false,
                    preview.CurrentStates, preview.Conflicts, null, started, transaction.Limitations);
                await AppendAsync(transaction, noopSequence + 2, TransactionState.RecoveryCompleted,
                    cancellationToken, recovery: noop, detail: "Estado observado já estava restaurado.");
                return noop;
            }

            if (transaction.BackupPath is null)
                return await FailWithoutEffectAsync(transaction, RecoveryOutcome.NotRecoverable,
                    "Transação sem backup persistido.", started, cancellationToken, steps, conflicts,
                    effectOccurred);

            BackupRecord persisted;
            try
            {
                persisted = await store.ReadBackupAsync(transaction.BackupPath, cancellationToken);
            }
            catch (Exception error) when (error is InvalidDataException or IOException
                or UnauthorizedAccessException)
            {
                return await FailWithoutEffectAsync(transaction, RecoveryOutcome.NotRecoverable,
                    "Backup inválido no momento do uso: " + error.Message, started,
                    cancellationToken, steps, conflicts, effectOccurred);
            }
            var revalidation = BackupService.ValidateBackup(persisted, plan);
            if (!revalidation.Valid || revalidation.Backup is null)
                return await FailWithoutEffectAsync(transaction, RecoveryOutcome.NotRecoverable,
                    revalidation.Reason ?? "Backup inválido no momento do uso.", started,
                    cancellationToken, steps, conflicts, effectOccurred);
            var backup = revalidation.Backup;

            var sequence = NextSequence(transaction);
            sequence = await AppendAsync(transaction, sequence, TransactionState.RecoveryRequested,
                cancellationToken, detail: $"Confirmação {options.UserConfirmationId}.");
            sequence = await AppendAsync(transaction, sequence, TransactionState.RecoveryStarted,
                cancellationToken, detail: "Recuperação iniciada.");

            string? failure = null;

            foreach (var change in plan.Changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
            {
                var entry = backup.Entries.First(item => item.TargetIdentity == change.TargetIdentity);
                ObservedTargetState current;
                try
                {
                    current = await reader.ReadAsync(change.Target, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    failure = $"Leitura indisponível: {error.Message}";
                    steps.Add(new StepResult(change.TargetIdentity, false, false, false, failure,
                        ["Estado atual não observável."]));
                    break;
                }

                if (MatchesBefore(current, entry))
                {
                    steps.Add(new StepResult(change.TargetIdentity, false, false, true, null,
                        ["Já está no estado observado."]));
                    continue;
                }

                if (!VerificationEngine.Matches(current, change))
                {
                    var conflict = new RecoveryConflict(change.TargetIdentity,
                        RecoveryConflictKind.StateChangedAfterTransaction, entry.Value, change.After,
                        current.Value, "Estado alterado após a prévia; nenhuma sobrescrita foi feita.");
                    conflicts.Add(conflict);
                    failure = conflict.Description;
                    steps.Add(new StepResult(change.TargetIdentity, false, false, false,
                        "Conflito de recuperação.", ["Escrita recusada para este alvo; os demais continuam."]));
                    continue;
                }

                var kind = !entry.TargetExists ? ChangeKind.DeleteKey
                    : entry.Value is { Exists: true } ? ChangeKind.SetValue
                    : ChangeKind.DeleteValue;
                var after = entry.Value ?? new TargetValueState(false, "String", null);
                var request = new ExecutionRequest(transaction.TransactionId, plan.PlanId,
                    plan.OperationId, plan.OperationVersion, kind, change.Target,
                    current.Value ?? new TargetValueState(false, "Unknown", null), after);

                ExecutionOutcome outcome;
                try
                {
                    outcome = await executor.ApplyAsync(request, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    outcome = ExecutionOutcome.Unknown(error.Message);
                }

                if (!outcome.Applied)
                {
                    if (outcome.Indeterminate) effectOccurred = true;
                    failure = outcome.Error ?? "Falha ao restaurar.";
                    steps.Add(new StepResult(change.TargetIdentity, false, false, false, failure,
                        ["Rollback não aplicado."]));
                    break;
                }
                effectOccurred = true;

                var verified = false;
                try
                {
                    var reread = await reader.ReadAsync(change.Target, cancellationToken);
                    verified = MatchesBefore(reread, entry);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    failure = $"Verificação de rollback indisponível: {error.Message}";
                }

                steps.Add(new StepResult(change.TargetIdentity, true, true, verified,
                    verified ? null : failure ?? "Estado restaurado diverge do observado.",
                    [verified ? "Estado observado restaurado." : "Restauração não confirmada."]));
                if (!verified) break;
            }

            var fully = plan.Changes.All(change =>
                steps.Any(step => step.TargetIdentity == change.TargetIdentity && step.Verified));
            var outcomeKind = fully ? RecoveryOutcome.Completed
                : conflicts.Count > 0
                    ? steps.Any(item => item.Applied) ? RecoveryOutcome.PartiallyCompleted
                        : RecoveryOutcome.Conflict
                : steps.Any(item => item.Applied) ? RecoveryOutcome.PartiallyCompleted
                : RecoveryOutcome.Failed;
            var result = Result(transactionId, outcomeKind, fully, effectOccurred, steps, conflicts,
                fully ? null : failure ?? "Recuperação parcial.", started, transaction.Limitations);

            await AppendAsync(transaction, sequence,
                fully ? TransactionState.RecoveryCompleted : TransactionState.RecoveryFailed,
                cancellationToken, recovery: result,
                detail: fully ? "Estado observado restaurado e verificado." : "Recuperação não integral.");
            return result;
        }
        catch (TargetLockedException error)
        {
            return await FailWithoutEffectAsync(transaction, RecoveryOutcome.Blocked, error.Message,
                started, cancellationToken, steps, conflicts, effectOccurred);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return await FailWithoutEffectAsync(transaction, RecoveryOutcome.Failed, error.Message,
                started, cancellationToken, steps, conflicts, effectOccurred);
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    private async Task<RecoveryResult> FailWithoutEffectAsync(TransactionRecord transaction,
        RecoveryOutcome outcome, string reason, DateTimeOffset started, CancellationToken cancellationToken,
        IReadOnlyList<StepResult>? steps = null, IReadOnlyList<RecoveryConflict>? conflicts = null,
        bool effectMayHaveOccurred = false)
    {
        var result = Result(transaction.TransactionId, outcome, false, effectMayHaveOccurred, steps ?? [],
            conflicts ?? [], reason, started, transaction.Limitations);
        try
        {
            var sequence = NextSequence(transaction);
            sequence = await AppendAsync(transaction, sequence, TransactionState.RecoveryRequested,
                cancellationToken, detail: "Solicitação de recuperação registrada.");
            await AppendAsync(transaction, sequence, TransactionState.RecoveryFailed,
                cancellationToken, recovery: result, error: reason,
                detail: "Recuperação recusada ou não concluída.");
        }
        catch { }
        return result;
    }

    private static bool HasEffectStarted(TransactionRecord transaction) =>
        transaction.Events.Any(item => item.State is TransactionState.ExecutionStarted
            or TransactionState.ExecutionCompleted or TransactionState.ExecutionIndeterminate
            or TransactionState.VerificationPassed or TransactionState.VerificationFailed);

    private static bool MatchesBefore(ObservedTargetState current, BackupEntry entry)
    {
        if (!current.IsKnown) return false;
        if (!entry.TargetExists) return !current.TargetExists;
        if (!current.TargetExists) return false;
        var expected = entry.Value ?? new TargetValueState(false, "Unknown", null);
        return VerificationEngine.ValueEquals(current.Value, expected);
    }

    private static bool MatchesState(ObservedTargetState current, bool targetExists,
        TargetValueState expectedValue)
    {
        if (!current.IsKnown) return false;
        if (current.TargetExists != targetExists) return false;
        if (!targetExists) return true;
        return VerificationEngine.ValueEquals(current.Value, expectedValue);
    }

    private bool MatchesCatalog(OperationPlan plan, out string reason)
    {
        if (catalog is null)
        {
            reason = string.Empty;
            return true;
        }
        if (!catalog.TryGet(plan.OperationId, out var descriptor) || descriptor is null)
        {
            reason = "Operação não existe no catálogo fechado; recuperação recusada.";
            return false;
        }
        if (!SafetyPlanValidation.MatchesCatalog(catalog, descriptor, plan, out var mismatch))
        {
            reason = "Plano não corresponde ao catálogo fechado: " + mismatch;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private RecoveryPreview NotRecoverable(Guid transactionId, string reason,
        IReadOnlyList<string> limitations) =>
        new(transactionId, false, RecoveryOutcome.NotRecoverable, [], reason, [], limitations);

    private static int NextSequence(TransactionRecord transaction) =>
        transaction.Events.Count == 0 ? 1 : transaction.Events.Max(item => item.Sequence) + 1;

    private async Task<int> AppendAsync(TransactionRecord transaction, int sequence,
        TransactionState state, CancellationToken cancellationToken, string? detail = null,
        string? error = null, RecoveryResult? recovery = null)
    {
        var entry = new TransactionEventRecord(1, transaction.TransactionId, sequence, state,
            time.GetUtcNow(), transaction.OperationId, transaction.OperationVersion,
            transaction.CatalogVersion, transaction.PlanHash, detail, error, null, null, null, null,
            null, null, recovery, []);
        await store.AppendEventAsync(entry, cancellationToken);
        return sequence + 1;
    }

    private RecoveryResult Result(Guid transactionId, RecoveryOutcome outcome, bool fully,
        bool effectMayHaveOccurred, IReadOnlyList<StepResult> steps,
        IReadOnlyList<RecoveryConflict> conflicts, string? error, DateTimeOffset started,
        IReadOnlyList<string> limitations) =>
        new(Guid.NewGuid(), transactionId, outcome, fully, effectMayHaveOccurred, steps, conflicts,
            error, started, time.GetUtcNow(), limitations.Distinct().ToList());
}
