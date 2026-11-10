using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed record RepairExecutionResult(
    RepairRecord? Record,
    bool Persisted,
    IReadOnlyList<RepairBlocker> Blockers,
    string Summary);

/// <summary>
/// Orquestra a autorização, a intenção durável e o registro final de ambos os transportes.
/// </summary>
public sealed class RepairCenter
{
    private readonly IRepairCatalog catalog;
    private readonly RepairPreflightEngine preflight;
    private readonly IRepairExecutor? executor;
    private readonly IRepairBrokerExecutor? broker;
    private readonly IRepairRecordStore store;
    private readonly Func<string> profilePath;
    private readonly RepairAuthorizationService authorization;
    private readonly TimeProvider time;
    private readonly bool isSynthetic;
    private readonly RepairExecutionMode mode;

    public RepairCenter(IRepairCatalog catalog, RepairPreflightEngine preflight, IRepairExecutor executor,
        IRepairRecordStore store, Func<string> profilePath, TimeProvider? time = null, TimeSpan? authorizationLifetime = null,
        RepairExecutionMode mode = RepairExecutionMode.Simulation)
        : this(catalog, preflight, executor, null, store, profilePath, time, authorizationLifetime, mode)
    {
    }

    private RepairCenter(IRepairCatalog catalog, RepairPreflightEngine preflight, IRepairExecutor? executor,
        IRepairBrokerExecutor? broker, IRepairRecordStore store, Func<string> profilePath, TimeProvider? time,
        TimeSpan? authorizationLifetime, RepairExecutionMode mode)
    {
        this.catalog = catalog;
        this.preflight = preflight;
        this.executor = executor;
        this.store = store;
        this.profilePath = profilePath ?? throw new ArgumentNullException(nameof(profilePath));
        this.time = time ?? TimeProvider.System;
        if (mode != RepairExecutionMode.Simulation &&
            (mode != RepairExecutionMode.Real || broker is null || !BrokerExecutionEnabled))
            throw new InvalidOperationException("A execução real do Windows Repair Center está bloqueada.");
        this.broker = broker;
        this.mode = mode;
        isSynthetic = mode == RepairExecutionMode.Simulation;
        authorization = new RepairAuthorizationService(this.time, authorizationLifetime);
    }

    public static bool BrokerExecutionEnabled =>
        RealRepairBuildGate.RealExecutionCompiled && RealRepairBuildGate.RealExecutionImplemented;

    public static RepairCenter CreateBrokered(RepairPreflightEngine preflight, IRepairBrokerExecutor broker,
        IRepairRecordStore store, Func<string> profilePath)
    {
        if (!RealRepairBuildGate.AllowsRealExecution(Environment.GetCommandLineArgs()))
            throw new InvalidOperationException("A composição real do Windows Repair Center está bloqueada.");
        return new(RepairCatalog.CreateDefault(), preflight, null, broker, store, profilePath, null, null,
            RepairExecutionMode.Real);
    }

    public IRepairCatalog Catalog => catalog;
    public RepairExecutionMode Mode => mode;
    public bool IsSynthetic => isSynthetic;
    public TimeSpan AuthorizationLifetime => authorization.Lifetime;

    public Task<RepairPreflightResult> EvaluateAsync(string operationId, string? volumeId,
        CancellationToken cancellationToken = default) => isSynthetic
        ? preflight.EvaluateAsync(catalog.Get(operationId), volumeId, cancellationToken)
        : preflight.EvaluateBrokeredAsync(catalog.Get(operationId), volumeId, cancellationToken);

    public async Task<RepairPlan> PrepareAsync(string operationId, string? volumeId,
        CancellationToken cancellationToken = default)
    {
        var operation = catalog.Get(operationId);
        var result = await EvaluateAsync(operationId, volumeId, cancellationToken);
        var invocation = result.SimulationAllowed
            ? RepairInvocationBuilder.Build(operation, result.Volume, result.Tool)
            : BuildPreview(operation, result.Volume);
        // Detalhes/limitações podem carregar mensagens de exceção com caminhos do perfil; a mesma sanitização
        // aplicada à saída é aplicada antes de persistir o preflight no plano/registro.
        var safePreflight = result with
        {
            Details = result.Details.Select(item => RepairOutputLimiter.Sanitize(item)).ToList(),
            Limitations = result.Limitations.Select(item => RepairOutputLimiter.Sanitize(item)).ToList()
        };

        var planId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var hash = RepairHashing.PlanHash(planId, executionId, operation.Id, operation.Version,
            catalog.Version, catalog.ContentHash, operation.Kind, operation.Risk, result.Volume,
            invocation, result.ObservedStateHash);
        var limitations = safePreflight.Limitations
            .Concat(isSynthetic ? [RepairGuidance.RealExecutionUnavailable, RepairGuidance.LocalOnly]
                : [RepairGuidance.LocalOnly, "A execução depende da autenticação e revalidação pelo broker."])
            .Distinct().ToList();
        return new RepairPlan(planId, executionId, operation.Id, operation.Version, catalog.Version,
            catalog.ContentHash, operation.DisplayName, operation.Category, operation.Kind, operation.Risk,
            operation.Availability, result.Volume, invocation, safePreflight, hash, time.GetUtcNow(),
            operation.Consequences, operation.PossibleChanges, limitations);
    }

    public RepairAuthorizationToken Authorize(RepairPlan plan, string confirmation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var operation = catalog.Get(plan.OperationId);
        if (!RepairPlanValidation.MatchesCatalog(catalog, operation, plan, out var reason))
            throw new InvalidOperationException("Plano não corresponde ao catálogo fechado: " + reason);
        if (!(isSynthetic ? plan.SimulationAllowed : plan.RealExecutionAllowed))
            throw new InvalidOperationException("Plano bloqueado não pode ser autorizado.");
        if (!isSynthetic && (plan.CreatedAtUtc > time.GetUtcNow() ||
            time.GetUtcNow() - plan.CreatedAtUtc >= RepairLimits.AuthorizationLifetime))
            throw new InvalidOperationException("Plano expirado; prepare e confirme uma nova operação.");
        if (operation.IsDocumentedOnly || operation.Risk is RiskLevel.High or RiskLevel.NotValidated)
            throw new InvalidOperationException("Operação não autorizável pelo backend.");
        return authorization.Issue(plan, confirmation);
    }

    public async Task<RepairExecutionResult> ExecuteAsync(RepairPlan plan, RepairAuthorizationToken token,
        RepairFixtureScenario? scenario, IProgress<RepairProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan is null || token is null)
            return Blocked(RepairBlocker.AuthorizationMissing, "Plano ou autorização ausente.");
        if (!isSynthetic && (!BrokerExecutionEnabled || !plan.RealExecutionAllowed || scenario is not null))
            return Blocked(RepairBlocker.RealExecutionDisabled, "Broker real indisponível para este plano.");
        if (!catalog.TryGet(plan.OperationId, out var operation) || operation is null)
            return Blocked(RepairBlocker.CatalogMismatch, "Operação não existe mais no catálogo fechado.");
        if (operation.IsDocumentedOnly || operation.Availability != RepairAvailability.Available)
            return Blocked(RepairBlocker.OperationUnavailable, "Operação indisponível no catálogo.");
        if (operation.Risk is RiskLevel.High or RiskLevel.NotValidated)
            return Blocked(RepairBlocker.RiskBlocked, "Risco alto ou não validado recusado no backend.");
        if (!RepairPlanValidation.MatchesCatalog(catalog, operation, plan, out var mismatch))
            return Blocked(RepairBlocker.PlanMismatch, mismatch);
        if (!string.Equals(plan.PlanHash, RepairPlanValidation.ComputeHash(plan), StringComparison.Ordinal))
            return Blocked(RepairBlocker.PlanMismatch, "Hash do plano não corresponde ao conteúdo apresentado.");
        if (!RepairConfirmationPolicy.IsValid(plan, token.UserConfirmationId))
            return Blocked(RepairBlocker.ConfirmationMismatch,
                "A confirmação vinculada à autorização não corresponde ao plano.");

        try
        {
            var history = await store.LoadRepairsAsync(cancellationToken);
            if (history.Records.Any(item => item.Id == plan.ExecutionId) ||
                history.UnfinishedIntents.Any(item => item.Id == plan.ExecutionId))
                return Blocked(RepairBlocker.AuthorizationReused,
                    "Execução já registrada ou pendente para este plano; a autorização não pode ser repetida.");
            if (!isSynthetic && history.Warnings.Count != 0)
                return Blocked(RepairBlocker.PersistenceFailed, "Histórico incompleto; revise os avisos antes de autorizar.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Blocked(RepairBlocker.PersistenceFailed,
                "Histórico de reparos indisponível; a execução não foi iniciada. " + error.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        RepairPreflightResult recheck;
        try
        {
            recheck = await EvaluateAsync(operation.Id, plan.Volume?.Id, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Blocked(RepairBlocker.ExecutionFailed, "Preflight de execução indisponível: " + error.Message);
        }
        if (!(isSynthetic ? recheck.SimulationAllowed : recheck.RealExecutionAllowed) ||
            !string.Equals(recheck.ObservedStateHash, plan.Preflight.ObservedStateHash, StringComparison.Ordinal))
            return Blocked(RepairBlocker.PlanMismatch,
                "Estado do sistema, do volume ou do executável divergiu desde a preparação.");

        var invocation = RepairInvocationBuilder.Build(operation, recheck.Volume, recheck.Tool);
        if (!RepairInvocationBuilder.Matches(operation, invocation, recheck.Volume, recheck.Tool, out var invocationReason))
            return Blocked(RepairBlocker.PlanMismatch, invocationReason);

        var validation = authorization.Validate(token, plan, recheck.ObservedStateHash);
        if (!validation.Valid)
            return Blocked(validation.Blocker, validation.Reason ?? "Autorização inválida.");

        var startedAt = time.GetUtcNow();
        var intent = new RepairIntent(1, plan.ExecutionId, plan.PlanId, plan.OperationId,
            plan.OperationVersion, plan.CatalogVersion, plan.CatalogContentHash, plan.DisplayName,
            plan.Kind, plan.Risk, mode, isSynthetic, plan.Volume, invocation.DisplayCommand,
            token.AuthorizationId, token.UserConfirmationId, startedAt, plan, plan.Limitations);
        try
        {
            await store.SaveRepairIntentAsync(intent, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Blocked(RepairBlocker.PersistenceFailed,
                "A intenção não pôde ser registrada; nenhuma execução foi iniciada. " + error.Message);
        }

        if (!authorization.Consume(token))
            return Blocked(RepairBlocker.AuthorizationReused, "A autorização não pôde ser consumida.");

        var steps = new List<RepairStep>
        {
            new(1, startedAt, RepairState.Planned,
                "Intenção e autorização registradas antes de qualquer execução.", null),
            new(2, time.GetUtcNow(), RepairState.PreflightPassed,
                "Preflight revalidado contra o catálogo, o volume e o executável.", null),
            new(3, time.GetUtcNow(), RepairState.Authorized,
                $"Autorização {token.AuthorizationId:D} consumida.", null)
        };

        var request = new RepairExecutionRequest(plan.ExecutionId, plan.PlanId, operation, invocation,
            recheck.Volume);
        var adapterStartedAt = time.GetUtcNow();
        RepairExecutionOutcome outcome;
        try
        {
            outcome = broker is null
                ? await executor!.ExecuteAsync(request, new RepairExecutionOptions(scenario), progress, cancellationToken)
                : await broker.ExecuteAsync(intent, token with { Consumed = true }, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = new RepairExecutionOutcome(isSynthetic, false, isSynthetic, !isSynthetic, null,
                "Cancelamento solicitado.", false, [], time.GetUtcNow(), time.GetUtcNow(), TimeSpan.Zero,
                isSynthetic, [RepairGuidance.CancellationLimitation])
            {
                Native = isSynthetic ? null : new(RepairBrokerStatus.Indeterminate, false, null, null)
            };
        }
        catch (Exception error)
        {
            var detail = isSynthetic ? error.Message : "Resultado do broker indisponível.";
            outcome = new RepairExecutionOutcome(isSynthetic, false, false, true, null, detail, false, [],
                time.GetUtcNow(), time.GetUtcNow(), TimeSpan.Zero, isSynthetic,
                ["Falha do adaptador: " + detail])
            {
                Native = isSynthetic ? null : new(RepairBrokerStatus.Indeterminate, false, null, null)
            };
        }

        var limited = RepairOutputLimiter.LimitLines(outcome.Output, profilePath());
        var interpretation = RepairResultInterpreter.Interpret(operation, isSynthetic ? outcome : outcome with
        {
            Output = limited.Lines, OutputTruncated = limited.Truncated || outcome.OutputTruncated
        });
        var finalState = ResolveState(outcome, interpretation);
        var completedAt = outcome.CompletedAtUtc == default ? time.GetUtcNow() : outcome.CompletedAtUtc;
        if (completedAt < startedAt) completedAt = startedAt;
        var duration = !isSynthetic || outcome.Duration > TimeSpan.Zero ? outcome.Duration : completedAt - startedAt;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        if (isSynthetic || outcome.Native?.ProcessStartedUtc is { })
            steps.Add(new(steps.Count + 1, outcome.Native?.ProcessStartedUtc ?? adapterStartedAt, RepairState.Running,
                isSynthetic ? "Adaptador sintético iniciado; nenhum processo real foi executado."
                    : "Início da ferramenta confirmado pelo broker.", null));
        steps.Add(new RepairStep(steps.Count + 1, completedAt, finalState,
            interpretation.Summary, outcome.ExitCode));
        if (!RepairStateMachine.Validate(steps, out var stepError))
            return Blocked(RepairBlocker.ExecutionFailed, "Máquina de estados recusou o resultado: " + stepError);

        var limitations = plan.Limitations.Concat(interpretation.Limitations)
            .Concat(outcome.Limitations)
            .Concat([
                RepairGuidance.NoRollback, RepairGuidance.NoAutomaticRepair,
                RepairGuidance.LocalOnly, RepairGuidance.OutputLocalization,
                "Estado final: " + finalState + "; resultado interpretado: " + interpretation.Classification + "."
            ])
            .Distinct().ToList();

        var record = new RepairRecord(1, plan.ExecutionId, SoftwareInfo.RepairSchemaVersion,
            SoftwareInfo.Version, plan.PlanId, plan.OperationId, plan.OperationVersion, plan.CatalogVersion,
            plan.CatalogContentHash, plan.DisplayName, plan.Category, plan.Kind, plan.Risk, mode,
            isSynthetic, recheck.Volume, invocation.DisplayCommand, finalState,
            interpretation.Classification, interpretation.Confidence, outcome.ExitCode,
            outcome.ExitCode is { } code ? RepairExitCodes.Display(code) : null, startedAt, completedAt,
            duration, token.AuthorizationId, token.UserConfirmationId, plan.Preflight,
            interpretation.Evidence, limited.Lines,
            BuildOutputSummary(interpretation, limited, isSynthetic), limited.Bytes,
            limited.Truncated || (!isSynthetic && outcome.OutputTruncated),
            interpretation.Summary, interpretation.RequiresRestart,
            interpretation.RequiresIntervention || finalState != RepairState.Completed,
            plan.PossibleChanges, limitations, steps) { Native = outcome.Native };

        try
        {
            var saved = await store.SaveRepairRecordAsync(record, CancellationToken.None);
            return new RepairExecutionResult(saved, true, [], BuildResultSummary(saved));
        }
        catch (Exception error)
        {
            return new RepairExecutionResult(record, false, [RepairBlocker.PersistenceFailed],
                BuildResultSummary(record) + " A gravação não foi concluída: " + error.Message);
        }
    }

    public Task<RepairRecord> SaveAsync(RepairRecord record, CancellationToken cancellationToken = default) =>
        store.SaveRepairRecordAsync(record, cancellationToken);

    public Task<RepairHistory> LoadHistoryAsync(CancellationToken cancellationToken = default) =>
        store.LoadRepairsAsync(cancellationToken);

    public string GetReportPath(Guid repairId) => store.GetRepairReportPath(repairId);

    private static RepairInvocation BuildPreview(RepairOperationDescriptor operation, RepairVolume? volume)
    {
        var arguments = operation.FixedArguments.Select(argument =>
            argument == RepairOperationDescriptor.VolumePlaceholder
                ? volume?.Id ?? RepairOperationDescriptor.VolumePlaceholder
                : argument).ToList();
        return new RepairInvocation(operation.Tool, operation.ExecutableName, string.Empty, arguments);
    }

    private static RepairState ResolveState(RepairExecutionOutcome outcome, RepairInterpretation interpretation)
    {
        if (outcome.Native is { } native)
            return native.Status switch
            {
                RepairBrokerStatus.Refused or RepairBrokerStatus.Conflict or RepairBrokerStatus.ElevationDeclined
                    => RepairState.Blocked,
                RepairBrokerStatus.CancelledBeforeStart => RepairState.Cancelled,
                RepairBrokerStatus.StartFailed or RepairBrokerStatus.StartDenied => RepairState.Failed,
                RepairBrokerStatus.ProcessExited when interpretation.Classification is
                    RepairResultClassification.RepairFailed or RepairResultClassification.AccessDenied or
                    RepairResultClassification.RepairSourceUnavailable or RepairResultClassification.UnsupportedSystem or
                    RepairResultClassification.VolumeUnavailable or RepairResultClassification.IncompatibleFileSystem
                    => RepairState.Failed,
                RepairBrokerStatus.ProcessExited when interpretation.Confidence != RepairConfidence.Low
                    => RepairState.Completed,
                _ => RepairState.Indeterminate
            };
        if (outcome.Cancelled) return RepairState.Cancelled;
        if (!outcome.Started) return RepairState.Failed;
        if (outcome.Interrupted || !outcome.Completed) return RepairState.Interrupted;
        return RepairState.Completed;
    }

    private static string BuildOutputSummary(RepairInterpretation interpretation,
        RepairLimitedOutput output, bool synthetic) =>
        (synthetic ? RepairGuidance.SyntheticDisclaimer + "\n" : string.Empty) +
        $"{interpretation.Summary} Saída: {output.Lines.Count}/{output.OriginalLineCount} linha(s), " +
        $"{output.Bytes} byte(s)" + (output.Truncated ? ", limitada localmente." : ".");

    private static string BuildResultSummary(RepairRecord record) =>
        $"Estado: {record.State} · resultado: {record.Result} · confiança: {record.Confidence}" +
        (record.ExitCode is { } code ? $" · código {record.ExitCodeText}" : " · código indisponível") +
        (record.RequiresRestart ? " · reinicialização indicada" : string.Empty) +
        (record.IsSynthetic ? " · fixture sintética" : string.Empty);

    private static RepairExecutionResult Blocked(RepairBlocker blocker, string message) =>
        new(null, false, [blocker], message);
}
