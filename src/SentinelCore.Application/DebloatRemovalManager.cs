using System.Collections.Concurrent;
using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>T3C synthetic orchestration through the existing Safety Engine. No native adapter is composed.</summary>
public sealed class DebloatRemovalManager
{
    private readonly DebloatCatalog catalog;
    private readonly IDebloatRemovalObservationSource source;
    private readonly IDebloatRemovalExecutor executor;
    private readonly ITransactionJournalStore journal;
    private readonly IExclusiveLockFactory locks;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<Guid, Prepared> plans = new();
    private readonly SemaphoreSlim executing = new(1, 1);

    public DebloatRemovalManager(DebloatCatalog catalog, IDebloatRemovalObservationSource source,
        IDebloatRemovalExecutor executor, ITransactionJournalStore journal,
        IExclusiveLockFactory locks, TimeProvider? time = null)
    {
        if (!source.SyntheticOnly || !executor.SyntheticOnly)
            throw new InvalidOperationException("T3C não compõe observador ou executor real.");
        this.catalog = catalog; this.source = source; this.executor = executor;
        this.journal = journal; this.locks = locks; this.time = time ?? TimeProvider.System;
    }

    public async Task<DebloatRemovalPlan> PrepareAsync(DebloatRemovalScenario scenario = DebloatRemovalScenario.Success,
        bool mediumReviewConfirmed = false, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(scenario) || plans.Count >= 64)
            throw new InvalidOperationException("Cenário inválido ou limite de planos atingido.");
        var observed = DebloatRemovalHashing.Copy(await source.ObserveAsync(cancellationToken));
        var preflight = DebloatRemovalPreflightEngine.Evaluate(catalog, observed, mediumReviewConfirmed);
        if (!preflight.CanSimulate || !executor.Available)
            throw new InvalidOperationException(string.Join(" ", preflight.Blockers.Append(executor.Available ? "" : "Executor indisponível.")));
        var entry = catalog.Find(observed.CatalogId)!;
        var snapshot = new DebloatRemovalSnapshot(1, observed, entry, scenario, mediumReviewConfirmed, false);
        var target = new OperationTarget(OperationTargetRoots.Synthetic, "Debloat." + entry.Id, "PackageMetadata");
        var operationId = "DBLT-RMV-" + entry.Id;
        var after = State(snapshot with { Removed = true });
        var operation = new OperationDescriptor(operationId, 1, "Debloat T3C sintético", entry.FriendlyName,
            OperationScope.CurrentUser, ChangeKind.SetValue, entry.PreliminaryRisk, OperationAvailability.Enabled,
            new("22000", null, true), [target], [new(target, after)], new(true, false, false), [],
            observed.Consequences.Concat([DebloatRemovalMessages.IndirectEffects]).ToArray(),
            [DebloatLimits.RecoveryWarning], false, false, DebloatRemovalMessages.Limitations,
            "Win11Debloat MIT " + entry.UpstreamCommit + "; orquestração própria T3C sintética.");
        var runtime = new SyntheticRemovalRuntime(catalog, source, executor, snapshot, target, operationId, time);
        var confirmation = new ConfirmationPolicy();
        var engine = new SafetyEngine(new OperationCatalog("t3c-removal-synthetic-1", [operation]),
            new Profile(observed, time), runtime, new Protection(), journal, runtime, locks, time,
            confirmationPolicy: confirmation, completeStartedExecution: true);
        var simulation = await engine.SimulateAsync(operationId, cancellationToken);
        if (!simulation.CanExecute) throw new InvalidOperationException(string.Join(" ", simulation.Blockers));
        runtime.AuthorizedPlanHash = simulation.Plan.PlanHash;
        // The shared engine's generic rollback label is not valid for package metadata transitions.
        simulation = simulation with { Protections = ["Snapshot de metadados íntegro", "Journal durável antes do efeito", "Verificação posterior; sem rollback de aplicativo"] };
        var draft = new DebloatRemovalPlan(simulation.Plan.PlanId, operationId, snapshot, preflight, simulation, "", "");
        var hash = DebloatRemovalHashing.PlanHash(draft);
        var plan = draft with { Hash = hash, Confirmation = $"SIMULAR REMOCAO T3C {entry.Id} {hash[..12]}" };
        confirmation.Expected = plan.Confirmation;
        plans[plan.Id] = new(DebloatRemovalHashing.Copy(plan), engine, runtime);
        return DebloatRemovalHashing.Copy(plan);
    }

    public AuthorizationToken Authorize(DebloatRemovalPlan plan, string confirmation)
    {
        var prepared = Resolve(plan);
        if (confirmation != prepared.Plan.Confirmation) throw new InvalidOperationException("Confirmação específica divergente.");
        return prepared.Engine.Authorize(prepared.Plan.Simulation, confirmation);
    }

    public async Task<DebloatRemovalResult> ExecuteAsync(DebloatRemovalPlan plan, AuthorizationToken authorization,
        DebloatExecutionMode mode = DebloatExecutionMode.Synthetic, IProgress<DebloatRemovalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (mode != DebloatExecutionMode.Synthetic || DebloatRemovalBuildGate.AllowsRealExecution())
            throw new InvalidOperationException(DebloatRemovalMessages.NativeBlocked);
        var prepared = Resolve(plan);
        if (cancellationToken.IsCancellationRequested)
            return new(DebloatRemovalState.CancelledBeforeStart, false, false, null, null,
                new(null, false, [], "Plano abandonado antes do início; nenhum efeito."), DebloatRemovalMessages.Limitations);
        await executing.WaitAsync(cancellationToken);
        try
        {
            prepared.Runtime.Progress = progress;
            TransactionExecutionResult transaction;
            try { transaction = await prepared.Engine.ExecuteAsync(prepared.Plan.Simulation.Plan, authorization, cancellationToken); }
            catch (OperationCanceledException)
            {
                var record = await journal.FindTransactionAsync(plan.Simulation.Plan.TransactionId, CancellationToken.None);
                transaction = new(record, false, [], "Cancelamento anterior ao início ou interrupção; consultar journal.");
            }
            var receipt = transaction.Transaction?.Events.LastOrDefault(e => e.RemovalReceipt is not null)?.RemovalReceipt;
            var state = Classify(transaction, receipt);
            if (state == DebloatRemovalState.Indeterminate && receipt is not null &&
                prepared.Plan.Snapshot.Scenario == DebloatRemovalScenario.Interrupted)
                state = DebloatRemovalState.Interrupted;
            return new(state, transaction.Succeeded && state == DebloatRemovalState.VerifiedSynthetic,
                state is DebloatRemovalState.PartialFailure or DebloatRemovalState.Interrupted or
                    DebloatRemovalState.Indeterminate or DebloatRemovalState.VerificationFailed,
                receipt, receipt?.PostVerification, transaction, DebloatRemovalMessages.Limitations);
        }
        finally { executing.Release(); }
    }

    public async Task<DebloatRemovalHistory> LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        var history = await journal.LoadTransactionsAsync(cancellationToken);
        var warnings = history.Warnings.ToList();
        var items = new List<DebloatRemovalHistoryItem>();
        foreach (var record in history.Transactions.Where(r => r.OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal)))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<DebloatRemovalSnapshot>(record.Plan!.Changes.Single().Before.Data!);
                if (snapshot is null || snapshot.SchemaVersion != 1 || snapshot.Removed ||
                    !Enum.IsDefined(snapshot.Scenario) || !snapshot.Observation.Synthetic ||
                    !DebloatRemovalPreflightEngine.Evaluate(catalog, snapshot.Observation, snapshot.MediumReviewConfirmed).CanSimulate ||
                    snapshot.CatalogEntry != catalog.Find(snapshot.Observation.CatalogId) ||
                    record.OperationId != "DBLT-RMV-" + snapshot.CatalogEntry.Id)
                    throw new InvalidDataException("Snapshot T3C incompatível.");
                items.Add(new(record.TransactionId, snapshot, record,
                    record.Events.LastOrDefault(e => e.RemovalReceipt is not null)?.RemovalReceipt,
                    journal.GetTransactionReportPath(record.TransactionId)));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or InvalidDataException or NullReferenceException or ArgumentException)
            { warnings.Add($"T3C {record.TransactionId:D}: histórico inválido preservado e excluído: {e.Message}"); }
        }
        return new(items, warnings);
    }

    private Prepared Resolve(DebloatRemovalPlan p)
    {
        if (!plans.TryGetValue(p.Id, out var known) || known.Plan.Hash != p.Hash ||
            DebloatRemovalHashing.PlanHash(p) != p.Hash || p.Confirmation != known.Plan.Confirmation)
            throw new InvalidOperationException("Plano adulterado, desconhecido ou de outra sessão.");
        return known;
    }

    private static DebloatRemovalState Classify(TransactionExecutionResult t, DebloatRemovalReceipt? r)
    {
        if (r?.CancelledBeforeStart == true) return DebloatRemovalState.CancelledBeforeStart;
        if (r?.PartialFailure == true) return DebloatRemovalState.PartialFailure;
        if (t.Transaction?.State == TransactionState.VerificationFailed) return DebloatRemovalState.VerificationFailed;
        if (r?.Phase == DebloatRemovalPhase.Indeterminate || t.Transaction?.IsIndeterminate == true)
            return DebloatRemovalState.Indeterminate;
        if (t.Succeeded && r is { Synthetic: true, Phase: DebloatRemovalPhase.Completed,
                PostVerification: { Complete: true, MainPackageAbsent: true, EffectsMatchPlan: true } })
            return DebloatRemovalState.VerifiedSynthetic;
        return t.Transaction?.State switch
        {
            TransactionState.VerificationFailed => DebloatRemovalState.VerificationFailed,
            TransactionState.Cancelled => DebloatRemovalState.CancelledBeforeStart,
            TransactionState.Interrupted => DebloatRemovalState.Interrupted,
            TransactionState.ExecutionFailed => DebloatRemovalState.Failed,
            _ => DebloatRemovalState.Blocked
        };
    }

    private static TargetValueState State(DebloatRemovalSnapshot snapshot) => new(true, "DebloatRemovalMetadata", JsonSerializer.Serialize(snapshot));
    private sealed record Prepared(DebloatRemovalPlan Plan, SafetyEngine Engine, SyntheticRemovalRuntime Runtime);
    private sealed class ConfirmationPolicy : IAuthorizationConfirmationPolicy
    {
        public string Expected { get; set; } = "";
        public bool IsValid(OperationPlan plan, string? text) => SafetyText.IsValidConfirmation(text) && text == Expected;
    }
    private sealed class Profile(DebloatRemovalObservation o, TimeProvider time) : ISystemProfileReader
    {
        public SystemProfile Read() => new("Windows 11 — fixture T3C", o.WindowsBuild, o.IsWindows11, false,
            [], [], [], "Fixture T3C; sem consulta Windows", time.GetUtcNow(), DebloatRemovalMessages.Limitations);
    }
    private sealed class Protection : IProtectionProvider
    {
        public Task<ProtectionStatus> ProbeAsync(ProtectionPolicy p, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProtectionStatus(false, "Snapshot de metadados; sem ponto de restauração.", DebloatRemovalMessages.Limitations));
    }

    private sealed class SyntheticRemovalRuntime(DebloatCatalog catalog, IDebloatRemovalObservationSource source,
        IDebloatRemovalExecutor executor, DebloatRemovalSnapshot snapshot, OperationTarget target,
        string operationId, TimeProvider time) : ITargetStateReader, IOperationExecutor
    {
        public IProgress<DebloatRemovalProgress>? Progress { get; set; }
        public string AuthorizedPlanHash { private get; set; } = "";
        private DebloatRemovalRequest? started;
        private bool completed;

        public async Task<ObservedTargetState> ReadAsync(OperationTarget requested, CancellationToken token = default)
        {
            if (requested != target) return ObservedTargetState.Unknown(requested.Identity, time.GetUtcNow(), "Alvo fora do plano.");
            if (started is not null)
            {
                var post = await source.VerifyAsync(started, token);
                if (!completed || !post.Complete || !post.MainPackageAbsent || !post.EffectsMatchPlan)
                    return ObservedTargetState.Unknown(target.Identity, time.GetUtcNow(), post.Information);
                return new(target.Identity, true, true, State(snapshot with { Removed = true }), time.GetUtcNow(), null);
            }
            var current = await source.ObserveAsync(token);
            var preflight = DebloatRemovalPreflightEngine.Evaluate(catalog, current, snapshot.MediumReviewConfirmed);
            if (!preflight.CanSimulate) return ObservedTargetState.Unknown(target.Identity, time.GetUtcNow(), string.Join(" ", preflight.Blockers));
            return new(target.Identity, true, true, State(snapshot with { Observation = current }), time.GetUtcNow(), null);
        }

        public async Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request, CancellationToken token = default)
        {
            if (started is not null || request.Target != target || request.OperationId != operationId ||
                request.Kind != ChangeKind.SetValue || request.Before != State(snapshot) || request.After != State(snapshot with { Removed = true }))
                return ExecutionOutcome.Failed("Requisição tipada não corresponde ao plano T3C.");
            var removalRequest = new DebloatRemovalRequest(request.TransactionId, snapshot.Observation.SelectedPackage,
                AuthorizedPlanHash, snapshot.Observation.InventoryRevision, true, snapshot.Scenario);
            DebloatRemovalReceipt receipt;
            try
            {
                var current = await source.ObserveAsync(token);
                if (!DebloatRemovalPreflightEngine.Evaluate(catalog, current, snapshot.MediumReviewConfirmed).CanSimulate ||
                    DebloatRemovalHashing.Hash(current) != DebloatRemovalHashing.Hash(snapshot.Observation) || !executor.Available)
                    return ExecutionOutcome.Failed("Revalidação imediatamente anterior falhou; nova simulação obrigatória.");
                token.ThrowIfCancellationRequested();
                started = removalRequest;
                receipt = await executor.ExecuteAsync(started, Progress, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Both allowed adapters check cancellation before initiation and never propagate it afterwards.
                started = null;
                receipt = new(removalRequest.OperationId, removalRequest.Package, true,
                    DebloatRemovalPhase.Failed, false, false, unchecked((int)0x800704C7), null,
                    "Plano abandonado antes do início do executor.", null, [], DebloatRemovalMessages.Limitations,
                    CancelledBeforeStart: true);
                return new(false, receipt.ErrorText, false, receipt);
            }
            if (!ValidReceipt(receipt, started)) return ExecutionOutcome.Unknown("Resultado divergente, malformado ou não verificável.");
            DebloatRemovalPostObservation post;
            try { post = await source.VerifyAsync(started, CancellationToken.None); }
            catch (Exception error)
            { post = new(false, false, false, "Pós-verificação indisponível: " + error.Message); }
            receipt = receipt with { PostVerification = post };
            completed = receipt.Phase == DebloatRemovalPhase.Completed && receipt.HResult == 0 &&
                receipt.ExtendedHResult is null or 0 && string.IsNullOrEmpty(receipt.ErrorText) && !receipt.PartialFailure;
            if (completed) return new(true, null, false, receipt);
            return new(false, receipt.ErrorText ?? "Resultado não concluído.", receipt.EffectMayHaveOccurred, receipt);
        }
    }

    public static bool ValidReceipt(DebloatRemovalReceipt r, DebloatRemovalRequest request) =>
        r.OperationId == request.OperationId && r.Package == request.Package && r.Synthetic == request.Synthetic &&
        Enum.IsDefined(r.Phase) && r.Progress is { Count: <= 256 } && r.Limitations is not null &&
        r.Progress.All(p => p.OperationId == r.OperationId && Enum.IsDefined(p.Phase) &&
            p.ReportedPercentage is null or <= 100) &&
        r.Phase is DebloatRemovalPhase.Completed or DebloatRemovalPhase.Failed or DebloatRemovalPhase.Indeterminate;
}
