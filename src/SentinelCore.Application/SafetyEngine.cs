using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class SafetyEngine
{
    private readonly IOperationCatalog catalog;
    private readonly ISystemProfileReader profileReader;
    private readonly ITargetStateReader reader;
    private readonly IProtectionProvider protection;
    private readonly ITransactionJournalStore journal;
    private readonly IOperationExecutor executor;
    private readonly IExclusiveLockFactory locks;
    private readonly TimeProvider time;
    private readonly PreflightEngine preflight;
    private readonly AuthorizationService authorization;
    private readonly BackupService backups;
    private readonly VerificationEngine verification;
    private readonly IAuthorizationConfirmationPolicy? confirmationPolicy;
    private readonly bool completeStartedExecution;
    private readonly Func<string?>? hkcuContext;

    public SafetyEngine(IOperationCatalog catalog, ISystemProfileReader profileReader,
        ITargetStateReader reader, IProtectionProvider protection, ITransactionJournalStore journal,
        IOperationExecutor executor, IExclusiveLockFactory locks, TimeProvider? time = null,
        TimeSpan? authorizationLifetime = null,
        IAuthorizationConfirmationPolicy? confirmationPolicy = null,
        bool completeStartedExecution = false, Func<string?>? hkcuContext = null)
    {
        this.catalog = catalog;
        this.profileReader = profileReader;
        this.reader = reader;
        this.protection = protection;
        this.journal = journal;
        this.executor = executor;
        this.locks = locks;
        this.time = time ?? TimeProvider.System;
        this.confirmationPolicy = confirmationPolicy;
        this.completeStartedExecution = completeStartedExecution;
        this.hkcuContext = hkcuContext;
        preflight = new PreflightEngine(catalog, profileReader, reader, protection);
        authorization = new AuthorizationService(this.time, authorizationLifetime);
        backups = new BackupService(journal, this.time);
        verification = new VerificationEngine(reader);
    }

    public IOperationCatalog Catalog => catalog;
    public TimeSpan AuthorizationLifetime => authorization.Lifetime;

    public async Task<SimulationResult> SimulateAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(operationId, out var operation) || operation is null)
            throw new InvalidOperationException($"Operação desconhecida no catálogo fechado: {operationId}.");
        return await SimulateCoreAsync(operation, cancellationToken);
    }

    public AuthorizationToken Authorize(SimulationResult simulation, string userConfirmationId)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (!simulation.CanExecute)
            throw new InvalidOperationException("Plano bloqueado não pode ser autorizado.");
        if (SafetyGates.RiskGate(simulation.Operation) is { } blocker)
            throw new InvalidOperationException($"Operação bloqueada no backend: {blocker}.");
        EnsureAuthorizable(simulation.Operation);
        var plan = simulation.Plan;
        if (!SafetyPlanValidation.MatchesCatalog(catalog, simulation.Operation, plan, out var mismatch))
            throw new InvalidOperationException("Plano não corresponde ao catálogo fechado: " + mismatch);
        if (plan.PlanHash != SafetyHashing.PlanHash(plan) || !MatchesHkcuContext(plan))
            throw new InvalidOperationException("Plano inconsistente; nova simulação é necessária.");
        if (confirmationPolicy is not null &&
            !confirmationPolicy.IsValid(plan, userConfirmationId))
            throw new InvalidOperationException(
                "A confirmação digitada não corresponde à operação e ao plano apresentado.");
        return authorization.Issue(plan, userConfirmationId);
    }

    /// <summary>
    /// Reconfere no backend as condições globais de composição antes de emitir autorização. Não substitui o
    /// preflight assíncrono da execução, mas impede que um objeto de simulação adulterado obtenha token em
    /// distribuição desabilitada ou sob política restritiva.
    /// </summary>
    private void EnsureAuthorizable(OperationDescriptor operation)
    {
        var profile = profileReader.Read();
        if (!profile.IsWindows11)
            throw new InvalidOperationException("Windows 11 é obrigatório para autorizar a operação.");
        if (operation.Scope != OperationScope.CurrentUser)
            throw new InvalidOperationException("Somente operações do usuário atual podem ser autorizadas.");
        if (operation.Availability != OperationAvailability.Enabled)
            throw new InvalidOperationException("Operação desabilitada ou bloqueada no catálogo.");
        if (profile.BlockedOperationIds.Contains(operation.Id, StringComparer.OrdinalIgnoreCase) ||
            profile.BlockedCategories.Contains(operation.Category, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Política restritiva bloqueia a operação; autorização recusada.");
    }

    public async Task<TransactionExecutionResult> ExecuteAsync(OperationPlan plan,
        AuthorizationToken token, CancellationToken cancellationToken = default)
    {
        if (plan is null || token is null)
            return Blocked(BlockerCode.AuthorizationMissing, "Plano ou autorização ausente.");
        if (!catalog.TryGet(plan.OperationId, out var operation) || operation is null)
            return Blocked(BlockerCode.TargetOutsideCatalog,
                "Operação não existe mais no catálogo fechado.");
        if (SafetyGates.RiskGate(operation) is { } riskGate)
            return Blocked(riskGate, "Operação bloqueada no backend no momento da execução.");
        if (!SafetyPlanValidation.MatchesCatalog(catalog, operation, plan, out var catalogMismatch))
            return Blocked(BlockerCode.PlanMismatch, catalogMismatch);
        if (plan.PlanHash != SafetyHashing.PlanHash(plan) || !MatchesHkcuContext(plan))
            return Blocked(BlockerCode.PlanMismatch,
                "Hash do plano não corresponde ao conteúdo apresentado.");

        var targetKeys = plan.Changes.Select(item => item.TargetIdentity)
            .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToList();
        if (targetKeys.Count == 0)
            return Blocked(BlockerCode.PlanMismatch, "Plano sem alvos.");

        var handles = new List<IDisposable>();
        try
        {
            foreach (var key in targetKeys) handles.Add(locks.Acquire(key));
        }
        catch (TargetLockedException error)
        {
            foreach (var handle in handles) handle.Dispose();
            return Blocked(BlockerCode.TargetLocked, error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            foreach (var handle in handles) handle.Dispose();
            return Blocked(BlockerCode.TargetLocked, error.Message);
        }

        JournalSession? session = null;
        var effectStarted = false;
        try
        {
            var existing = await journal.FindTransactionAsync(plan.TransactionId, cancellationToken);
            if (existing is not null)
                return Blocked(BlockerCode.AuthorizationReused,
                    "Transação já registrada; autorização e plano não podem ser repetidos.");

            var snapshot = await journal.LoadTransactionsAsync(cancellationToken);
            if (snapshot.Warnings.Count > 0)
                return Blocked(BlockerCode.JournalUnavailable,
                    "Journal de transações contém registros inválidos: " + string.Join("; ", snapshot.Warnings));
            var indeterminate = snapshot.Transactions.FirstOrDefault(item => item.IsIndeterminate &&
                item.Plan is not null &&
                item.Plan.Changes.Any(change => targetKeys.Contains(change.TargetIdentity)));
            if (indeterminate is not null)
                return Blocked(BlockerCode.IndeterminateState,
                    $"Transação {indeterminate.TransactionId} permanece indeterminada no mesmo alvo.");

            var preflightResult = await preflight.EvaluateAsync(operation, cancellationToken);
            session = new JournalSession(journal, plan, time);
            await session.AppendAsync(TransactionState.Planned, cancellationToken,
                plan: plan, detail: "Intenção registrada antes de qualquer efeito.");
            if (!preflightResult.Passed)
            {
                await session.AppendAsync(TransactionState.Blocked, cancellationToken,
                    error: string.Join("; ", preflightResult.Details));
                return Failure(session, preflightResult.Blockers.FirstOrDefault(), "Preflight recusou o plano.");
            }
            await session.AppendAsync(TransactionState.PreflightPassed, cancellationToken,
                detail: $"Preflight aprovado para o catálogo {catalog.Version}.");

            var expectedChanges = BuildExpectedChanges(operation, preflightResult);
            var validation = authorization.Validate(token, plan, preflightResult.ObservedStateHash);
            if (!validation.Valid)
            {
                await session.AppendAsync(TransactionState.Blocked, cancellationToken,
                    error: validation.Reason);
                return Failure(session, validation.Blocker, validation.Reason ?? "Autorização inválida.");
            }
            if (!PlanMatches(plan, expectedChanges) || !MatchesHkcuContext(plan))
            {
                await session.AppendAsync(TransactionState.Blocked, cancellationToken,
                    error: "Plano não corresponde às alterações declaradas no catálogo e ao estado observado.");
                return Failure(session, BlockerCode.PlanMismatch,
                    "Plano não corresponde ao catálogo fechado; execução recusada.");
            }

            if (!plan.Protection.RequiresBackup)
            {
                await session.AppendAsync(TransactionState.Blocked, cancellationToken,
                    error: "Plano sem backup obrigatório.");
                return Failure(session, BlockerCode.ProtectionUnavailable, "Backup obrigatório ausente.");
            }

            var backupDraft = backups.Create(plan, preflightResult.ObservedStates, 1);
            if (!backupDraft.IsComplete)
            {
                var reason = "Backup incompleto: " + string.Join("; ", backupDraft.Limitations);
                await session.AppendAsync(TransactionState.Blocked, cancellationToken, error: reason);
                return Failure(session, BlockerCode.BackupFailed, reason);
            }

            string backupPath;
            try
            {
                backupPath = await journal.WriteBackupAsync(backupDraft, cancellationToken);
                await session.AppendAsync(TransactionState.BackupPrepared, cancellationToken,
                    backup: backupDraft, backupPath: backupPath, detail: backupPath);
                var persisted = await journal.ReadBackupAsync(backupPath, cancellationToken);
                var backupValidation = BackupService.ValidateBackup(persisted, plan);
                if (!backupValidation.Valid)
                    throw new InvalidDataException(backupValidation.Reason ?? "Backup inválido.");
                await session.AppendAsync(TransactionState.BackupVerified, cancellationToken,
                    backup: persisted, backupPath: backupPath, detail: "Backup relido e conferido por hash.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                await session.AppendAsync(TransactionState.Blocked, cancellationToken,
                    error: "Falha de backup: " + error.Message);
                return Failure(session, BlockerCode.BackupFailed, error.Message);
            }

            if (!authorization.Consume(token))
            {
                await session.AppendAsync(TransactionState.Blocked, cancellationToken,
                    error: "Autorização já consumida.");
                return Failure(session, BlockerCode.AuthorizationReused, "Autorização já consumida.");
            }
            await session.AppendAsync(TransactionState.Authorized, cancellationToken,
                authorization: token with { Consumed = true },
                detail: $"Autorização {token.AuthorizationId:D} consumida.");

            var recheck = await ReadStatesAsync(plan.Changes.Select(item => item.Target), cancellationToken);
            var recheckHash = SafetyHashing.ObservedStateHash(recheck);
            if (recheckHash != plan.ObservedStateHash || !MatchesHkcuContext(plan))
            {
                await session.AppendAsync(TransactionState.Blocked, cancellationToken,
                    error: "Estado do alvo divergiu entre o preflight e a execução.");
                return Failure(session, BlockerCode.StateChanged,
                    "Estado alterado após o preflight; execução recusada.");
            }

            effectStarted = true;
            var realEffect = plan.Changes.Any(change =>
                !string.Equals(change.Target.Root, OperationTargetRoots.Synthetic, StringComparison.Ordinal));
            await session.AppendAsync(TransactionState.ExecutionStarted, cancellationToken,
                detail: realEffect
                    ? "Efeito controlado em HKCU autorizado e iniciado."
                    : "Efeito sintético autorizado e iniciado.");
            var executionToken = completeStartedExecution || hkcuContext is not null
                ? CancellationToken.None : cancellationToken;
            var results = new List<StepResult>();
            DebloatRemovalReceipt? removalReceipt = null;
            foreach (var change in plan.Changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
            {
                var request = new ExecutionRequest(plan.TransactionId, plan.PlanId, plan.OperationId,
                    plan.OperationVersion, change.Kind, change.Target, change.Before, change.After);
                ExecutionOutcome outcome;
                try
                {
                    // Package removal observes cancellation until its own initiation boundary.
                    outcome = await executor.ApplyAsync(request, hkcuContext is null ? cancellationToken : executionToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    outcome = ExecutionOutcome.Unknown(error.Message);
                }

                removalReceipt = outcome.RemovalReceipt;

                if (!outcome.Applied)
                {
                    results.Add(new StepResult(change.TargetIdentity, false, false, false, outcome.Error,
                        ["Efeito não confirmado."]));
                    var state = outcome.Indeterminate
                        ? TransactionState.ExecutionIndeterminate
                        : TransactionState.ExecutionFailed;
                    await session.AppendAsync(state, executionToken, results: results,
                        error: outcome.Error, detail: "Execução interrompida.", removalReceipt: removalReceipt);
                    return Failure(session,
                        outcome.Indeterminate ? BlockerCode.IndeterminateState : BlockerCode.ExecutionFailed,
                        outcome.Error ?? "Falha de execução.");
                }

                results.Add(new StepResult(change.TargetIdentity,
                    !VerificationEngine.StateEquals(change.Before, change.After), true, false, null,
                    [realEffect ? "Efeito aplicado." : "Efeito simulado aplicado."]));
            }

            await session.AppendAsync(TransactionState.ExecutionCompleted, executionToken,
                results: results,
                detail: realEffect
                    ? "Efeitos controlados aplicados; verificação pendente."
                    : "Efeitos simulados concluídos; verificação pendente.", removalReceipt: removalReceipt);

            var verify = await verification.VerifyAsync(plan, executionToken);
            var verifyState = verify.Passed
                ? TransactionState.VerificationPassed
                : TransactionState.VerificationFailed;
            await session.AppendAsync(verifyState, executionToken, verification: verify,
                results: verify.Steps,
                detail: verify.Passed ? "Estado posterior confere com o plano."
                    : "Estado posterior diverge do plano.");

            var record = TransactionProjection.Build(session.Events);
            return verify.Passed
                ? new TransactionExecutionResult(record, true, [], "Operação aplicada e verificada.")
                : new TransactionExecutionResult(record, false, [BlockerCode.VerificationFailed],
                    "Verificação posterior falhou; sucesso não é declarado.");
        }
        catch (OperationCanceledException)
        {
            if (session is { Events.Count: > 0 })
            {
                try
                {
                    await session.AppendAsync(
                        effectStarted ? TransactionState.Interrupted : TransactionState.Cancelled,
                        CancellationToken.None, error: "Operação cancelada.");
                }
                catch { }
            }
            throw;
        }
        catch (Exception error)
        {
            TransactionRecord? record = null;
            if (session is { Events.Count: > 0 })
            {
                try
                {
                    await session.AppendAsync(TransactionState.Interrupted, CancellationToken.None,
                        error: error.Message, detail: "Interrupção ou falha de armazenamento.");
                }
                catch { }
                record = TransactionProjection.Build(session.Events, [error.Message]);
            }
            var blocker = effectStarted ? BlockerCode.IndeterminateState : BlockerCode.JournalUnavailable;
            return new TransactionExecutionResult(record, false, [blocker], error.Message);
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    private async Task<SimulationResult> SimulateCoreAsync(OperationDescriptor operation,
        CancellationToken cancellationToken)
    {
        var preflightResult = await preflight.EvaluateAsync(operation, cancellationToken);
        var blockers = new List<BlockerCode>(preflightResult.Blockers);
        var contextHash = hkcuContext?.Invoke();
        if (hkcuContext is not null && contextHash is null)
            blockers.Add(BlockerCode.AccessDenied);
        if (SafetyGates.RiskGate(operation) is { } riskGate) blockers.Add(riskGate);

        var observedByIdentity = preflightResult.ObservedStates
            .ToDictionary(item => item.TargetIdentity, StringComparer.Ordinal);
        var changes = new List<PlannedChange>();
        foreach (var planned in operation.Changes.OrderBy(item => item.Target.Identity, StringComparer.Ordinal))
        {
            TargetValueState before;
            if (observedByIdentity.TryGetValue(planned.Target.Identity, out var observed) && observed.IsKnown)
                before = observed.Value ?? new TargetValueState(false, "Unknown", null);
            else
            {
                blockers.Add(BlockerCode.AccessDenied);
                before = new TargetValueState(false, "Unknown", null);
            }
            changes.Add(new PlannedChange(planned.Target.Identity, planned.Target, operation.Kind,
                observedByIdentity.TryGetValue(planned.Target.Identity, out var observedTarget) &&
                observedTarget.IsKnown && observedTarget.TargetExists,
                operation.Kind != ChangeKind.DeleteKey,
                before, planned.DesiredValue));
        }

        var planId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var plan = new OperationPlan(planId, transactionId, operation.Id, operation.Version,
            catalog.Version, catalog.ContentHash, operation.Risk, operation.Scope, operation.Protection, changes,
            preflightResult.ObservedStateHash,
            SafetyHashing.PlanHash(planId, transactionId, operation.Id, operation.Version, catalog.Version,
                operation.Risk, operation.Scope, operation.Protection, preflightResult.ObservedStateHash,
                changes, contextHash),
            time.GetUtcNow(),
            preflightResult.Limitations.Concat(operation.Limitations).Distinct().ToList())
        {
            HkcuContextHash = contextHash
        };

        var protections = new List<string>
        {
            "Backup imediatamente anterior do estado observado",
            "Verificação posterior obrigatória",
            "Journal durável append-only",
            "Rollback restrito ao estado observado"
        };
        if (operation.Protection.RequiresRestorePoint)
            protections.Add("Ponto de restauração verificável");
        if (operation.Protection.RequiresFullTarget)
            protections.Add("Backup de chave inteira (indisponível na T1)");

        var expected = changes.Count == 1
            ? $"Esperado: {VerificationEngine.Describe(changes[0].After)}."
            : $"Esperado: {changes.Count} alvos no estado do plano.";

        return new SimulationResult(blockers.Count == 0, operation, plan,
            blockers.Distinct().ToList(), operation.Consequences, operation.KnownRisks,
            operation.Dependencies, protections, plan.Limitations, expected);
    }

    private static List<PlannedChange> BuildExpectedChanges(OperationDescriptor operation,
        PreflightResult preflightResult)
    {
        var observedByIdentity = preflightResult.ObservedStates
            .ToDictionary(item => item.TargetIdentity, StringComparer.Ordinal);
        var changes = new List<PlannedChange>();
        foreach (var planned in operation.Changes.OrderBy(item => item.Target.Identity, StringComparer.Ordinal))
        {
            var known = observedByIdentity.TryGetValue(planned.Target.Identity, out var observed) &&
                        observed.IsKnown;
            var before = known
                ? observed!.Value ?? new TargetValueState(false, "Unknown", null)
                : new TargetValueState(false, "Unknown", null);
            changes.Add(new PlannedChange(planned.Target.Identity, planned.Target, operation.Kind,
                known && observed!.TargetExists, operation.Kind != ChangeKind.DeleteKey, before,
                planned.DesiredValue));
        }
        return changes;
    }

    private static bool PlanMatches(OperationPlan plan, IReadOnlyList<PlannedChange> expected)
    {
        if (plan.Changes.Count != expected.Count) return false;
        var ordered = plan.Changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal).ToList();
        for (var index = 0; index < expected.Count; index++)
        {
            var actual = ordered[index];
            var item = expected[index];
            if (actual.TargetIdentity != item.TargetIdentity || actual.Kind != item.Kind ||
                !string.Equals(actual.Target.Root, item.Target.Root, StringComparison.Ordinal) ||
                !string.Equals(actual.Target.Path, item.Target.Path, StringComparison.Ordinal) ||
                !string.Equals(actual.Target.ValueName, item.Target.ValueName, StringComparison.Ordinal) ||
                actual.BeforeTargetExists != item.BeforeTargetExists ||
                actual.AfterTargetExists != item.AfterTargetExists ||
                actual.Before != item.Before || actual.After != item.After)
                return false;
        }
        return true;
    }

    private async Task<List<ObservedTargetState>> ReadStatesAsync(IEnumerable<OperationTarget> targets,
        CancellationToken cancellationToken)
    {
        var states = new List<ObservedTargetState>();
        foreach (var target in targets.DistinctBy(item => item.Identity)
                     .OrderBy(item => item.Identity, StringComparer.Ordinal))
        {
            try
            {
                states.Add(await reader.ReadAsync(target, cancellationToken));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                states.Add(ObservedTargetState.Unknown(target.Identity, time.GetUtcNow(), error.Message));
            }
        }
        return states;
    }

    private static TransactionExecutionResult Blocked(BlockerCode blocker, string message) =>
        new(null, false, [blocker], message);

    private static TransactionExecutionResult Failure(JournalSession session, BlockerCode blocker,
        string message)
    {
        var record = TransactionProjection.Build(session.Events, [message]);
        return new TransactionExecutionResult(record, false, [blocker], message);
    }

    private bool MatchesHkcuContext(OperationPlan plan) => hkcuContext is null
        ? plan.HkcuContextHash is null
        : plan.HkcuContextHash is not null && plan.HkcuContextHash == hkcuContext();

    internal async Task<string?> CheckHkcuRecoveryAsync(OperationPlan plan, CancellationToken token)
    {
        if (hkcuContext is null || !MatchesHkcuContext(plan) || plan.PlanHash != SafetyHashing.PlanHash(plan))
            return "Contexto HKCU indisponível ou divergente. Registros sem vínculo permanecem somente para consulta.";
        if (!catalog.TryGet(plan.OperationId, out var operation) || operation is null || !plan.Protection.RequiresBackup ||
            !SafetyPlanValidation.MatchesCatalog(catalog, operation, plan, out _))
            return "Operação sem recuperação no catálogo atual.";
        var check = await preflight.EvaluateAsync(operation, token);
        // Recovery classifies unreadable, missing and differently typed values per target.
        return check.Blockers.All(blocker => blocker is BlockerCode.AccessDenied or BlockerCode.Unrecoverable)
            ? null : string.Join(" ", check.Details);
    }

    private sealed class JournalSession
    {
        private readonly ITransactionJournalStore store;
        private readonly OperationPlan plan;
        private readonly TimeProvider time;

        public JournalSession(ITransactionJournalStore store, OperationPlan plan, TimeProvider time)
        {
            this.store = store;
            this.plan = plan;
            this.time = time;
        }

        public List<TransactionEventRecord> Events { get; } = [];
        public int NextSequence => Events.Count + 1;

        public async Task AppendAsync(TransactionState state, CancellationToken cancellationToken,
            string? detail = null, string? error = null, OperationPlan? plan = null,
            BackupRecord? backup = null, string? backupPath = null,
            AuthorizationToken? authorization = null,
            IReadOnlyList<StepResult>? results = null,
            VerificationResult? verification = null, RecoveryResult? recovery = null,
            DebloatRemovalReceipt? removalReceipt = null)
        {
            var entry = new TransactionEventRecord(1, this.plan.TransactionId, NextSequence, state,
                time.GetUtcNow(), this.plan.OperationId, this.plan.OperationVersion,
                this.plan.CatalogVersion, this.plan.PlanHash, detail, error, plan, backup, backupPath,
                authorization, results, verification, recovery, [], removalReceipt);
            var stored = await store.AppendEventAsync(entry, cancellationToken);
            Events.Add(stored);
        }
    }
}
