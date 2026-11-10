using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Read-only aggregation with recovery delegated to the configured Tweaks flow.</summary>
public sealed class RecoveryCenter
{
    private readonly IRecoveryHistoryReader history;
    private readonly RecoveryService? recovery;
    private readonly TweakManager? tweaks;
    private readonly DebloatCatalog debloatCatalog = DebloatCatalog.CreateDefault();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> analyzedPlans = new();
    public bool SyntheticRecoveryAvailable => recovery is not null;
    public bool RealRecoveryAvailable => tweaks?.RealExecutionEnabled == true;

    public RecoveryCenter(IRecoveryHistoryReader history, RecoveryService? syntheticRecovery = null,
        TweakManager? tweaks = null)
    {
        this.history = history;
        if (syntheticRecovery is { SyntheticOnly: false })
            throw new ArgumentException("A central aceita somente RecoveryService restrito a alvos sintéticos.", nameof(syntheticRecovery));
        recovery = syntheticRecovery;
        this.tweaks = tweaks;
    }

    public async Task<RecoveryCenterSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RecoveryHistorySource> loaded;
        try { loaded = await history.ReadRecoveryHistoryAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            loaded = Enum.GetValues<RecoveryModule>().Select(module => new RecoveryHistorySource(module,
                RecoveryEvidenceStatus.Unavailable, [], [], [], ["Leitura indisponível: " + error.Message])).ToArray();
        }
        var warnings = new List<string>();
        var sources = new List<RecoveryHistorySource>();
        var operations = new List<RecoveryOperation>();
        foreach (var module in Enum.GetValues<RecoveryModule>())
        {
            var matches = loaded.Where(s => s.Module == module).ToArray();
            if (matches.Length != 1)
            {
                var reason = matches.Length == 0 ? "Fonte não retornada; quantidade indisponível."
                    : "Fonte duplicada e ambígua; carga recusada.";
                sources.Add(new(module, RecoveryEvidenceStatus.Unavailable, [], [], [], [reason]));
                warnings.Add($"{module}: {reason}");
                continue;
            }
            var source = matches[0];
            var sourceWarnings = source.Warnings.ToList();
            if (!Enum.IsDefined(source.Status))
                source = source with { Status = RecoveryEvidenceStatus.Unavailable };
            if (source.Status != RecoveryEvidenceStatus.Unavailable)
            {
                foreach (var transaction in source.Transactions)
                {
                    try { operations.Add(MapTransaction(module, transaction)); }
                    catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException
                        or InvalidOperationException or NullReferenceException)
                    { sourceWarnings.Add($"{transaction.TransactionId:D}: histórico inválido preservado e excluído: {error.Message}"); }
                }
                foreach (var record in source.Repairs)
                {
                    try
                    {
                        if (module != RecoveryModule.WindowsRepair) throw new InvalidDataException("Reparo em fonte incompatível.");
                        operations.Add(MapRepair(record));
                    }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException or NullReferenceException)
                    { sourceWarnings.Add($"{record.Id:D}: reparo inválido preservado e excluído: {error.Message}"); }
                }
                foreach (var intent in source.UnfinishedRepairs)
                {
                    if (module != RecoveryModule.WindowsRepair)
                    { sourceWarnings.Add("Intenção R1 em fonte incompatível."); continue; }
                    if (source.Repairs.Any(r => r.Id == intent.Id))
                    { sourceWarnings.Add($"{intent.Id:D}: intenção duplicada com registro final; intenção não contada novamente."); continue; }
                    operations.Add(MapIntent(intent));
                }
            }
            source = source with { Warnings = sourceWarnings.Distinct().ToArray(),
                Status = source.Status == RecoveryEvidenceStatus.Unavailable ? source.Status
                    : sourceWarnings.Count > 0 ? RecoveryEvidenceStatus.Partial : source.Status };
            sources.Add(source);
            warnings.AddRange(source.Warnings.Select(w => $"{module}: {w}"));
        }
        if (loaded.Any(s => !Enum.IsDefined(s.Module))) warnings.Add("Fonte com módulo desconhecido excluída.");
        var unique = new List<RecoveryOperation>();
        foreach (var group in operations.GroupBy(o => o.Id))
        {
            // A single record cannot diverge from itself; fingerprints (full JSON serialization) are needed only for repeated identities.
            if (group.Count() == 1) { unique.Add(group.First()); continue; }
            var variants = group.GroupBy(Fingerprint, StringComparer.Ordinal).ToArray();
            if (variants.Length != 1)
            {
                warnings.Add($"{group.Key:D}: identidade duplicada com conteúdo/origem divergente; registros excluídos.");
                continue;
            }
            unique.Add(group.First());
        }
        var auditHistory = history is IAuditHistoryReader auditReader
            ? await auditReader.ReadAuditHistoryAsync(cancellationToken).ConfigureAwait(false) : null;
        var audits = auditHistory is not null
            ? new StoreSnapshot(auditHistory.Audits.Where(audit => audit.Status == DiagnosticStatus.Interrupted).ToArray(), auditHistory.Warnings)
            : history is IAuditCheckpointStore auditCheckpoints
            ? await auditCheckpoints.ReadInterruptedAuditsAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return new(unique.OrderByDescending(o => o.AtUtc).ThenBy(o => o.Module).ThenBy(o => o.Id).ToArray(),
            sources, warnings.Distinct().ToArray()) { InterruptedAudits = audits, Audits = auditHistory };
    }

    public static IReadOnlyList<RecoveryOperation> Filter(RecoveryCenterSnapshot snapshot, RecoveryCenterFilter filter)
    {
        var search = filter.Search?.Trim();
        return snapshot.Operations.Where(o =>
            (filter.Module is null || o.Module == filter.Module) &&
            (string.IsNullOrWhiteSpace(filter.Type) || o.Type == filter.Type) &&
            (filter.FromUtc is null || o.AtUtc >= filter.FromUtc) &&
            (filter.ToUtc is null || o.AtUtc <= filter.ToUtc) &&
            (string.IsNullOrWhiteSpace(filter.State) || o.State == filter.State) &&
            (filter.Risk is null || o.Risk == filter.Risk) &&
            (filter.Outcome is null || o.Outcome == filter.Outcome) &&
            (filter.Origin is null || o.Origin == filter.Origin) &&
            (filter.Capability is null || o.Capability == filter.Capability) &&
            (filter.RecoveryAvailable is null || (o.Capability == RecoveryCapability.PotentiallyAvailable) == filter.RecoveryAvailable) &&
            (filter.Conflict is null || o.HasConflict == filter.Conflict) &&
            (filter.Intervention is null || o.RequiresIntervention == filter.Intervention) &&
            (string.IsNullOrWhiteSpace(search) || Searchable(o).Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static RecoveryCenterSummary Summarize(RecoveryCenterSnapshot snapshot)
    {
        var operations = snapshot.Operations;
        var attempts = operations.SelectMany(o => o.Recoveries).GroupBy(r => r.RecoveryId)
            .Select(g => g.First()).ToArray();
        return new(operations.Count, operations.Count(o => o.Outcome == RecoveryOperationOutcome.Completed),
            operations.Count(o => o.Outcome == RecoveryOperationOutcome.Failed),
            operations.Count(o => o.Outcome == RecoveryOperationOutcome.Indeterminate),
            operations.Count(o => o.HasConflict), operations.Count(o => o.RequiresIntervention),
            attempts.Count(r => r.FullyRecovered && r.Outcome is RecoveryOutcome.Completed or RecoveryOutcome.AlreadyRestored),
            attempts.Count(r => r.Outcome == RecoveryOutcome.PartiallyCompleted),
            attempts.Count(r => r.Outcome is RecoveryOutcome.Blocked or RecoveryOutcome.Conflict or RecoveryOutcome.NotRecoverable),
            attempts.Count(r => r.Outcome is RecoveryOutcome.Failed or RecoveryOutcome.Interrupted), snapshot.IsComplete);
    }

    public async Task<RecoveryOperation> AnalyzeAsync(RecoveryOperation operation, CancellationToken cancellationToken = default)
    {
        analyzedPlans.TryRemove(operation.Id, out _);
        var current = await ResolveTweakAsync(operation, cancellationToken);
        var preview = IsSyntheticTweak(current)
            ? await recovery!.PreviewAsync(current.Id, cancellationToken)
            : await tweaks!.PreviewRecoveryAsync(current.Id, cancellationToken);
        var conflict = current.HasConflict || preview.Conflicts.Count > 0;
        var capability = preview.CanRecover && (preview.Outcome != RecoveryOutcome.AlreadyRestored ||
                current is { Origin: RecoveryOrigin.Real, Transaction.IsIndeterminate: true })
            ? RecoveryCapability.PotentiallyAvailable : RecoveryCapability.Blocked;
        if (IsSyntheticTweak(current) && capability == RecoveryCapability.PotentiallyAvailable)
            analyzedPlans[current.Id] = current.Transaction!.PlanHash;
        return current with { Preview = preview, HasConflict = conflict,
            RequiresIntervention = conflict || current.RequiresIntervention, Capability = capability,
            Limitations = current.Limitations.Concat(preview.Limitations).Distinct().ToArray(),
            Guidance = current.Guidance.Append(preview.Reason ?? (preview.Outcome == RecoveryOutcome.AlreadyRestored
                ? "Estado anterior já confirmado; nenhuma recuperação necessária."
                : "Estado e backup serão revalidados no momento do uso.")).ToArray() };
    }

    public async Task<RecoveryResult> RecoverAsync(RecoveryOperation operation, string confirmation,
        CancellationToken cancellationToken = default)
    {
        var current = await ResolveTweakAsync(operation, cancellationToken);
        if (!IsSyntheticTweak(current))
            return await tweaks!.RollbackAsync(current.Id, new RecoveryOptions(confirmation)
                { Preview = operation.Preview }, cancellationToken);
        if (confirmation != TweakManager.BuildRecoveryConfirmationText(current.Id) ||
            !analyzedPlans.TryRemove(current.Id, out var planHash) || planHash != current.Transaction!.PlanHash)
            return Blocked(current.Id, "Confirmação específica inválida ou prévia sintética ausente; analisar novamente.");
        // The original service revalidates backup, locks, catalog and current states before each effect.
        return await recovery!.RollbackAsync(current.Id, new RecoveryOptions(confirmation), cancellationToken);
    }

    public bool CanAnalyze(RecoveryOperation? operation) => operation is not null &&
        (SyntheticRecoveryAvailable && IsSyntheticTweak(operation) ||
            RealRecoveryAvailable && operation is { Module: RecoveryModule.Tweaks,
                Origin: RecoveryOrigin.Real, Transaction.Plan.HkcuContextHash: not null });

    private async Task<RecoveryOperation> ResolveTweakAsync(RecoveryOperation selected, CancellationToken token)
    {
        if (!CanAnalyze(selected))
            throw new InvalidOperationException("Recuperação indisponível para este registro ou nesta composição.");
        var snapshot = await LoadAsync(token);
        var current = snapshot.Operations.SingleOrDefault(o => o.Id == selected.Id && o.Module == RecoveryModule.Tweaks);
        if (current is null || !CanAnalyze(current) || current.Transaction!.PlanHash != selected.Transaction!.PlanHash ||
            snapshot.Sources.Single(s => s.Module == RecoveryModule.Tweaks).Status != RecoveryEvidenceStatus.Complete)
            throw new InvalidOperationException("Evidências indisponíveis, alteradas ou incompletas; recuperação bloqueada.");
        return current;
    }

    private static bool IsSyntheticTweak(RecoveryOperation operation) =>
        operation.Module == RecoveryModule.Tweaks && operation.Origin == RecoveryOrigin.Synthetic &&
        operation.Transaction?.Plan is { Changes.Count: > 0 } plan &&
        !operation.OperationId.StartsWith("DBLT-", StringComparison.Ordinal) &&
        plan.Changes.All(c => c.Target.Root == OperationTargetRoots.Synthetic);

    private RecoveryOperation MapTransaction(RecoveryModule module, TransactionRecord input)
    {
        if (module == RecoveryModule.WindowsRepair || input.Events.Count == 0)
            throw new InvalidDataException("Fonte ou journal incompatível.");
        if (input.Events.Any(e => e.FormatVersion != 1 || !Enum.IsDefined(e.State) ||
            e.TransactionId != input.TransactionId || e.OperationId != input.OperationId || e.PlanHash != input.PlanHash))
            throw new InvalidDataException("Versão, estado ou identidade de evento inválido.");
        var ordered = input.Events.OrderBy(e => e.Sequence).ToArray();
        if (ordered.Where((e, i) => e.Sequence != i + 1).Any())
            throw new InvalidDataException("Sequência duplicada ou incompleta.");
        var t = TransactionProjection.Build(ordered, input.Limitations);
        var inferred = t.OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal) ? RecoveryModule.ControlledRemoval
            : t.OperationId.StartsWith("DBLT-", StringComparison.Ordinal) ? RecoveryModule.Debloat : RecoveryModule.Tweaks;
        if (inferred != module) throw new InvalidDataException("Módulo diverge da identidade persistida.");
        var recoveries = t.Events.Where(e => e.Recovery is not null).Select(e => e.Recovery!)
            .GroupBy(r => r.RecoveryId).Select(g => g.Last()).ToArray();
        var conflict = recoveries.Any(r => r.Conflicts.Count > 0 || r.Outcome == RecoveryOutcome.Conflict);
        var receipt = t.Events.LastOrDefault(e => e.RemovalReceipt is not null)?.RemovalReceipt;
        var effect = receipt?.EffectMayHaveOccurred == true || t.Steps.Any(s => s.Applied) ||
            t.Events.Any(e => e.State == TransactionState.ExecutionStarted);
        var unresolved = t.IsIndeterminate || t.State is TransactionState.ExecutionIndeterminate
            or TransactionState.Interrupted or TransactionState.ExecutionStarted or TransactionState.RecoveryStarted ||
            t.State == TransactionState.VerificationFailed ||
            (receipt?.PartialFailure == true && t.State != TransactionState.VerificationPassed);
        var blockedTarget = module == RecoveryModule.ControlledRemoval && t.IsIndeterminate;
        var outcome = unresolved ? RecoveryOperationOutcome.Indeterminate : t.State switch
        {
            TransactionState.VerificationPassed when t.Verification?.Passed == true => RecoveryOperationOutcome.Completed,
            TransactionState.RecoveryCompleted when t.Recovery?.FullyRecovered == true => RecoveryOperationOutcome.Completed,
            TransactionState.ExecutionFailed or TransactionState.RecoveryFailed => RecoveryOperationOutcome.Failed,
            TransactionState.Blocked => RecoveryOperationOutcome.Blocked,
            TransactionState.Cancelled => RecoveryOperationOutcome.Cancelled,
            TransactionState.Planned or TransactionState.PreflightPassed or TransactionState.BackupPrepared
                or TransactionState.BackupVerified or TransactionState.Authorized => RecoveryOperationOutcome.NotStarted,
            _ => RecoveryOperationOutcome.Indeterminate
        };
        var limitations = t.Limitations.ToList();
        var guidance = new List<string>();
        var consequences = t.Steps.SelectMany(s => s.Effects).ToList();
        var target = string.Join("; ", t.Plan?.Changes.Select(c => c.TargetIdentity) ?? []);
        var synthetic = t.Plan?.Changes is { Count: > 0 } changes && changes.All(c => c.Target.Root == OperationTargetRoots.Synthetic);
        var origin = synthetic ? RecoveryOrigin.Synthetic
            : t.Plan?.HkcuContextHash is not null ? RecoveryOrigin.Real : RecoveryOrigin.Unknown;
        if (origin == RecoveryOrigin.Unknown)
            limitations.Add("O modo sintético/real não foi registrado neste contrato histórico; origem desconhecida. Nenhuma consulta atual foi realizada.");
        var type = string.Join(" + ", t.Plan?.Changes.Select(c => c.Kind.ToString()).Distinct() ?? []);
        if (module == RecoveryModule.Debloat)
        {
            var state = DebloatHistoryValidation.ReadDebloat(t);
            target = $"{state.Package.FriendlyName} | {state.Package.PackageFullName} | {state.Package.Publisher} | {state.Package.UserId}";
            type = "Simulação de Debloat";
            consequences.AddRange([state.CatalogEntry.Consequence, state.CatalogEntry.DataLossWarning,
                "Dependências: " + state.Package.DependencyStatus + "; " + string.Join(", ", state.Package.Dependencies.Select(d => d.PackageFamilyName)),
                $"Escopo anterior: {DebloatPresentation.Scope(state.Package.Scope)} (fixture)",
                $"Estado anterior conhecido: {state.Package.State} · PFN: {state.Package.PackageFamilyName} · versão: {state.Package.Version} · arquitetura: {state.Package.Architecture}"]);
            guidance.Add("Reinstalação real indisponível. Orientação documental: " + state.CatalogEntry.ReinstallInformation);
            limitations.Add($"Risco preliminar histórico: {RecoveryCenterPresentation.Label(state.CatalogEntry.PreliminaryRisk)}; efetivo do plano: {RecoveryCenterPresentation.Label(t.Plan!.Risk)}; risco real T0: {RecoveryCenterPresentation.Label(state.CatalogEntry.RealRisk)}. Simulação não autoriza remoção real.");
        }
        if (module == RecoveryModule.ControlledRemoval)
        {
            var state = DebloatHistoryValidation.ReadRemoval(t, debloatCatalog);
            var package = state.Observation.SelectedPackage;
            origin = state.Observation.Synthetic ? RecoveryOrigin.Synthetic : RecoveryOrigin.Real;
            target = $"{state.CatalogEntry.FriendlyName} | {package.PackageFullName} | {package.Publisher} | {package.UserSecurityId}";
            type = "Remoção controlada T3C";
            consequences.AddRange(state.Observation.Consequences);
            consequences.AddRange(DebloatRemovalPresentation.HistoricalEvidence(state, receipt, t).Split('\n'));
            var dependencies = state.Observation.Dependencies;
            consequences.AddRange(dependencies.UnknownConsequences);
            consequences.AddRange(dependencies.ApplicationsMayClose.Select(a => "Aplicativo pode ser encerrado: " + a));
            guidance.Add("Reinstalação real indisponível. Orientação documental: " + state.Observation.ReinstallInformation);
            limitations.Add($"Risco preliminar histórico: {RecoveryCenterPresentation.Label(state.CatalogEntry.PreliminaryRisk)}; efetivo do plano: {RecoveryCenterPresentation.Label(t.Plan!.Risk)}; risco real T0: {RecoveryCenterPresentation.Label(state.CatalogEntry.RealRisk)}. T0 permanece bloqueada para execução real.");
            if (receipt is not null) limitations.AddRange(receipt.Limitations);
        }
        var intervention = unresolved || conflict || blockedTarget ||
            recoveries.Any(r => r.Outcome is RecoveryOutcome.PartiallyCompleted or RecoveryOutcome.Failed or RecoveryOutcome.Interrupted);
        RecoveryCapability capability;
        if (unresolved) capability = RecoveryCapability.Indeterminate;
        else if (intervention) capability = RecoveryCapability.InterventionRequired;
        else if (module != RecoveryModule.Tweaks) capability = RecoveryCapability.Unsupported;
        else capability = origin == RecoveryOrigin.Real && RealRecoveryAvailable && t.BackupPath is not null && effect &&
            t.Recovery?.FullyRecovered != true ? RecoveryCapability.RequiresAnalysis : RecoveryCapability.Blocked;
        if (module == RecoveryModule.Tweaks)
        {
            guidance.Add("Recuperação pelo RecoveryService; sem rollback forçado ou sobrescrita de conflitos.");
            guidance.Add(origin == RecoveryOrigin.Real && RealRecoveryAvailable
                ? "Analise o backup e o estado atual do alvo antes de confirmar."
                : origin == RecoveryOrigin.Unknown
                    ? "Origem sem vínculo de contexto. Consulte o registro para investigação."
                    : "O estado atual exige prévia em composição com recuperação habilitada.");
            if (t.Recovery?.FullyRecovered == true) guidance.Add("Recuperação anterior integral registrada; não presume estado atual inalterado.");
        }
        else
        {
            guidance.Add("Recuperação automática de aplicativos não suportada. " + DebloatLimits.RecoveryWarning);
            limitations.Add("Snapshot preserva somente metadados; não contém backup de arquivos, configurações, preferências, licenças, contas, sessões ou dados removidos.");
        }
        if (blockedTarget) guidance.Add("Alvo persistentemente bloqueado pela T3C. Reabertura e reconstrução do SQLite não liberam a pendência; intervenção necessária. Nenhum desbloqueio disponível.");
        return new(t.TransactionId, module, t.OperationId, string.IsNullOrEmpty(type) ? "Tipo não registrado" : type,
            string.IsNullOrEmpty(target) ? "Alvo não registrado" : target, t.UpdatedAtUtc, t.State.ToString(), outcome, origin,
            t.Plan?.Risk, capability, conflict, intervention, effect, blockedTarget,
            t.Authorization is { } auth ? $"{auth.AuthorizationId:D}; confirmação: {auth.UserConfirmationId}; consumida: {auth.Consumed}" : "Não registrada",
            t.BackupPath is null ? "Backup persistido não registrado" :
                $"{t.Backup?.BackupId:D} · {(t.Events.Any(e => e.State == TransactionState.BackupVerified) ? "Conferido na aplicação" : "Conferência não registrada")}. {t.BackupPath}. Integridade atual exige revalidação.",
            t.Verification is null ? "Pós-verificação não registrada" : t.Verification.Passed ? "Pós-verificação histórica aprovada" : "Pós-verificação histórica falhou",
            consequences.Distinct().ToArray(), limitations.Distinct().ToArray(), guidance,
            t.Events.OrderBy(e => e.Sequence).Select(e => new RecoveryTimelineEvent(e.Sequence, e.AtUtc, e.State.ToString(), e.Detail, e.Error)).ToArray(),
            recoveries, history.GetRecoveryReportPath(module, t.TransactionId), t);
    }

    private RecoveryOperation MapRepair(RepairRecord r)
    {
        if (r.FormatVersion != 1 || !Enum.IsDefined(r.State) || !Enum.IsDefined(r.Result) || !Enum.IsDefined(r.ExecutionMode))
            throw new InvalidDataException("Versão ou estado R1 desconhecido.");
        var incomplete = r.State is RepairState.Indeterminate or RepairState.Interrupted || r.OutputTruncated ||
            r.State == RepairState.Completed && r.Confidence == RepairConfidence.Low ||
            r.Result is RepairResultClassification.OutputIncomplete or RepairResultClassification.UnknownExitCode
                or RepairResultClassification.NotEvaluated or RepairResultClassification.Interrupted or RepairResultClassification.RepairPartial;
        var outcome = incomplete ? RecoveryOperationOutcome.Indeterminate : r.State switch
        {
            RepairState.Completed when r.VerifiedSuccess ||
                r.Native?.Status == RepairBrokerStatus.ProcessExited ||
                r.Confidence is RepairConfidence.High or RepairConfidence.Medium &&
                r.Result is RepairResultClassification.CorruptionDetected or RepairResultClassification.RestartRequired
                => RecoveryOperationOutcome.Completed,
            RepairState.Failed => RecoveryOperationOutcome.Failed,
            RepairState.Blocked => RecoveryOperationOutcome.Blocked,
            RepairState.Cancelled => RecoveryOperationOutcome.Cancelled,
            _ => RecoveryOperationOutcome.Indeterminate
        };
        var intervention = r.RequiresIntervention || incomplete || outcome == RecoveryOperationOutcome.Indeterminate;
        var details = new List<string>(r.PossibleChanges)
        {
            $"Comando documentado: {r.InvocationDisplay}", $"Resultado: {r.ResultSummary}",
            $"Código de saída: {r.ExitCodeText ?? r.ExitCode?.ToString() ?? "indisponível"}",
            $"Confiabilidade: {r.Confidence}; reinicialização requerida: {r.RequiresRestart}",
            "Evidências: " + JsonSerializer.Serialize(r.Evidence), "Saída: " + r.OutputSummary
        };
        if (r.Native is { } native)
            details.Add($"Broker: {RepairPresentation.BrokerStatusName(native.Status)} · UAC solicitado: " +
                (native.ElevationRequested ? "sim" : "não"));
        return new(r.Id, RecoveryModule.WindowsRepair, r.OperationId, r.Kind.ToString(),
            r.Volume?.DisplayName ?? r.DisplayName, r.CompletedUtc, r.State.ToString(), outcome,
            r.IsSynthetic && r.ExecutionMode == RepairExecutionMode.Simulation ? RecoveryOrigin.Synthetic
                : !r.IsSynthetic && r.ExecutionMode == RepairExecutionMode.Real ? RecoveryOrigin.Real : RecoveryOrigin.Unknown,
            r.Risk, incomplete ? RecoveryCapability.Indeterminate : intervention ? RecoveryCapability.InterventionRequired : RecoveryCapability.Unsupported,
            false, intervention, r.Steps.Any(s => s.State == RepairState.Running) ||
                r.Native?.Status == RepairBrokerStatus.Indeterminate, false,
            $"{r.AuthorizationId:D}; confirmação: {r.ConfirmationId}", "Rollback equivalente não suportado; backup de reversão não disponível",
            r.Native is null
                ? $"Resultado {r.Result}; confiabilidade {r.Confidence}; conclusão suficientemente documentada: {outcome == RecoveryOperationOutcome.Completed}"
                : "Evidência da ferramenta. Verificação independente posterior não realizada.", details, r.Limitations,
            r.AdditionalActions.Append("Sem rollback de SFC/DISM/CHKDSK/WinRE. Ponto de restauração não garante reversão. Esta central não repete reparos.").ToArray(),
            r.Steps.OrderBy(s => s.Sequence).Select(s => new RecoveryTimelineEvent(s.Sequence, s.AtUtc, s.State.ToString(), s.Detail, null)).ToArray(),
            [], history.GetRecoveryReportPath(RecoveryModule.WindowsRepair, r.Id), Repair: r);
    }

    private RecoveryOperation MapIntent(RepairIntent intent) => new(intent.Id, RecoveryModule.WindowsRepair,
        intent.OperationId, intent.Kind.ToString(), intent.Volume?.DisplayName ?? intent.DisplayName,
        intent.CreatedAtUtc, "Intenção órfã — execução não comprovada", RecoveryOperationOutcome.Indeterminate,
        intent.IsSynthetic && intent.ExecutionMode == RepairExecutionMode.Simulation ? RecoveryOrigin.Synthetic : RecoveryOrigin.Unknown,
        intent.Risk, RecoveryCapability.Indeterminate, false, true, false, false,
        $"{intent.AuthorizationId:D}; confirmação: {intent.ConfirmationId}", "Não registrado",
        "Resultado e pós-verificação indisponíveis", ["Comando planejado: " + intent.InvocationDisplay], intent.Limitations,
        [intent.IsSynthetic
            ? "Intenção durável não comprova execução iniciada. Investigar antes de qualquer nova tentativa; sem rollback ou repetição nesta central."
            : "A ferramenta pode continuar após o encerramento do aplicativo. Investigue o estado antes de preparar e autorizar uma nova operação."],
        [new(1, intent.CreatedAtUtc, "Intenção durável", "Sem registro final; não se presume início nem sucesso.", null)],
        [], history.GetRecoveryReportPath(RecoveryModule.WindowsRepair, intent.Id));

    private static string Fingerprint(RecoveryOperation operation) => JsonSerializer.Serialize(new
        { operation.Module, operation.Transaction, operation.Repair, operation.Id, operation.State, operation.AtUtc, operation.Authorization });
    private static string Searchable(RecoveryOperation o) => string.Join(" ", new[] { o.Id.ToString("D"),
        o.OperationId, o.Target, o.Type, o.State, RecoveryCenterPresentation.Label(o.Module), RecoveryCenterPresentation.Label(o.Outcome),
        RecoveryCenterPresentation.Label(o.Capability), RecoveryCenterPresentation.Label(o.Origin), o.Authorization,
        string.Join(" ", o.Consequences), string.Join(" ", o.Limitations), string.Join(" ", o.Guidance) });
    private static RecoveryResult Blocked(Guid id, string reason) => new(Guid.NewGuid(), id,
        RecoveryOutcome.Blocked, false, false, [], [], reason, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
}
