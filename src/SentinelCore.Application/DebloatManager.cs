using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Single-package synthetic orchestration. Security, authorization, journal, backup and verification
/// are the existing Safety Engine. A metadata snapshot is never an application-data recovery promise.
/// </summary>
public sealed class DebloatManager
{
    private readonly IDebloatInventory inventory;
    private readonly IDebloatRuntimeFactory runtimeFactory;
    private readonly ITransactionJournalStore journal;
    private readonly IExclusiveLockFactory locks;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<Guid, Prepared> prepared = new();
    private readonly SemaphoreSlim executing = new(1, 1);
    public DebloatCatalog Catalog { get; }

    public DebloatManager(DebloatCatalog catalog, IDebloatInventory inventory,
        IDebloatRuntimeFactory runtimeFactory, ITransactionJournalStore journal,
        IExclusiveLockFactory locks, TimeProvider? time = null)
    {
        Catalog = catalog; this.inventory = inventory; this.runtimeFactory = runtimeFactory;
        this.journal = journal; this.locks = locks; this.time = time ?? TimeProvider.System;
        if (!inventory.SyntheticOnly || !runtimeFactory.SyntheticOnly)
            throw new InvalidOperationException("T3 só compõe provedores sintéticos; inventário real não autorizado.");
    }

    public async Task<IReadOnlyList<DebloatAssessment>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var packages = await ReadInventoryAsync(cancellationToken);
        var result = packages.Select(p => DebloatPolicy.Assess(Catalog, p, inventory.CurrentUserId,
            packages.Count(other => other.CatalogId == p.CatalogId ||
                other.TechnicalName == p.TechnicalName || other.PackageFamilyName == p.PackageFamilyName) != 1)).ToList();
        // Catalog entries without an observation are reported as not verified, never as absent or uninstalled.
        foreach (var missing in Catalog.All.Where(e =>
                     packages.All(p => p.CatalogId != e.Id && p.TechnicalName != e.TechnicalName)))
            result.Add(DebloatPolicy.Assess(Catalog, Unobserved(missing), inventory.CurrentUserId, observed: false));
        return result.OrderBy(e => e.Package.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private DebloatPackage Unobserved(DebloatCatalogEntry entry) => new(
        entry.Id, entry.FriendlyName, entry.TechnicalName, "Não observado", "Não observado", "Não observado",
        "Não observado", "Não observado", inventory.CurrentUserId,
        entry.RemovalMethod == "Appx" ? DebloatPackageKind.Appx : DebloatPackageKind.Win32,
        DebloatScope.Unknown, DebloatInstallState.Unknown, null, false, false,
        DebloatDependencyStatus.NotInvestigated, [], true,
        "Catálogo documental — sem observação de inventário", []);

    public async Task<DebloatPreparedPlan> PrepareAsync(string catalogId,
        DebloatScenario scenario = DebloatScenario.Success, bool mediumReviewConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(scenario)) throw new InvalidOperationException("Cenário desconhecido.");
        if (prepared.Count >= 64) throw new InvalidOperationException("Limite de planos desta sessão atingido; reabra o módulo.");
        var packages = await ReadInventoryAsync(cancellationToken);
        var candidates = packages.Where(p => p.CatalogId == catalogId).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("Identidade ausente ou duplicada; plano recusado.");
        var package = candidates[0];
        var assessment = DebloatPolicy.Assess(Catalog, package, inventory.CurrentUserId,
            packages.Count(p => p.TechnicalName == package.TechnicalName || p.PackageFamilyName == package.PackageFamilyName) != 1);
        if (!assessment.CanSimulate || assessment.CatalogEntry is null)
            throw new InvalidOperationException(string.Join(" ", assessment.Blockers));
        if (assessment.EffectiveRisk == RiskLevel.Medium && !mediumReviewConfirmed)
            throw new InvalidOperationException("Risco médio exige revisão explícita das consequências e perdas de dados, mesmo na fixture.");
        if (package.DependencyStatus != DebloatDependencyStatus.KnownNone || package.Dependencies.Count != 0)
            throw new InvalidOperationException("A primeira política sintética admite somente ausência conhecida de dependências.");
        if (scenario is DebloatScenario.Protected or DebloatScenario.DependencyDetected or
            DebloatScenario.IdentityMismatch or DebloatScenario.NotFound)
            throw new InvalidOperationException(scenario switch
            {
                DebloatScenario.Protected => "Fixture de pacote protegido: operação bloqueada antes da autorização.",
                DebloatScenario.DependencyDetected => "Fixture de dependência crítica: operação bloqueada antes da autorização.",
                DebloatScenario.IdentityMismatch => "Fixture de identidade divergente: operação bloqueada antes da autorização.",
                _ => "Fixture de aplicativo ausente: nenhuma remoção a preparar."
            });

        var entry = assessment.CatalogEntry;
        var operationId = "DBLT-" + entry.Id;
        var target = new OperationTarget(OperationTargetRoots.Synthetic, "Debloat." + entry.Id, "PackageMetadata");
        var runtime = runtimeFactory.Create(package, entry, scenario, mediumReviewConfirmed,
            operationId, target, time);
        var after = new TargetValueState(true, "DebloatPackage", JsonSerializer.Serialize(
            new DebloatTargetSnapshot(package with { State = DebloatInstallState.RemovedSynthetic }, entry,
                scenario, mediumReviewConfirmed)));
        var operation = new OperationDescriptor(operationId, 1, "Debloat sintético", entry.FriendlyName,
            OperationScope.CurrentUser, ChangeKind.SetValue, assessment.EffectiveRisk, OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true), [target], [new PlannedTarget(target, after)],
            new ProtectionPolicy(true, false, false), [],
            [DebloatLimits.SyntheticWarning, entry.Consequence, entry.DataLossWarning],
            [DebloatLimits.RecoveryWarning], false, false,
            ["Snapshot exclusivamente de metadados sintéticos; não é backup de aplicativo ou dados.",
             $"Cenário: {scenario}. Reinstalação: {entry.ReinstallInformation}",
             "Nenhuma execução real, consulta de pacotes reais, provisionamento ou outros usuários."],
            "Win11Debloat MIT " + entry.UpstreamCommit + " — definições documentais; executor próprio somente sintético.");
        var confirmation = new SpecificConfirmation();
        var activeJournal = scenario == DebloatScenario.PersistenceUnavailable ? new UnavailableJournal(journal) : journal;
        var engine = new SafetyEngine(new OperationCatalog(DebloatCatalog.Version, [operation]),
            new FixtureProfile(time), runtime, new FixtureProtection(), activeJournal, runtime, locks, time,
            confirmationPolicy: confirmation);
        var simulation = await engine.SimulateAsync(operationId, cancellationToken);
        if (!simulation.CanExecute) throw new InvalidOperationException(string.Join(" ", simulation.Blockers));
        var draft = new DebloatPreparedPlan(simulation.Plan.PlanId, package, entry, assessment.EffectiveRisk,
            scenario, mediumReviewConfirmed, simulation, "", "", operation.Consequences, operation.Limitations);
        var hash = Hash(draft);
        var text = $"SIMULAR REMOCAO {entry.Id} {hash[..12]}";
        var plan = draft with { PlanHash = hash, Confirmation = text };
        confirmation.Expected = text;
        if (!prepared.TryAdd(plan.Id, new Prepared(Clone(plan), engine, runtime)))
            throw new InvalidOperationException("Plano duplicado.");
        return Clone(plan);
    }

    public AuthorizationToken Authorize(DebloatPreparedPlan plan, string confirmation)
    {
        var context = Resolve(plan);
        if (!string.Equals(context.Plan.Confirmation, confirmation, StringComparison.Ordinal))
            throw new InvalidOperationException("Confirmação deve corresponder exatamente ao aplicativo e ao hash apresentado.");
        return context.Engine.Authorize(context.Plan.Simulation, confirmation);
    }

    public async Task<DebloatExecutionResult> ExecuteAsync(DebloatPreparedPlan plan, AuthorizationToken token,
        DebloatExecutionMode mode = DebloatExecutionMode.Synthetic, CancellationToken cancellationToken = default)
    {
        // Unconditional before resolving plans or invoking any adapter. No real executor exists.
        if (mode != DebloatExecutionMode.Synthetic || RealDebloatBuildGate.AllowsRealExecution())
            throw new InvalidOperationException("Remoção real indisponível em toda a T3.");
        var context = Resolve(plan);
        await executing.WaitAsync(cancellationToken);
        try
        {
            context.Runtime.AdvanceFixtureBeforeExecution();
            TransactionExecutionResult result;
            try { result = await context.Engine.ExecuteAsync(context.Plan.Simulation.Plan, token, cancellationToken); }
            catch (OperationCanceledException)
            {
                var record = await journal.FindTransactionAsync(context.Plan.Simulation.Plan.TransactionId, CancellationToken.None);
                result = new TransactionExecutionResult(record, false,
                    [record?.IsIndeterminate == true ? BlockerCode.IndeterminateState : BlockerCode.ExecutionFailed],
                    "Simulação cancelada/interrompida; somente eventos duráveis confirmam o estado.");
            }
            var packages = await inventory.ReadAsync(CancellationToken.None);
            var current = packages.SingleOrDefault(p => p.CatalogId == context.Plan.Package.CatalogId);
            var summary = result.Succeeded
                ? "Remoção SINTÉTICA verificada. Nenhum aplicativo real foi removido."
                : $"Simulação não concluída: {result.Summary} Nenhuma remoção real realizada.";
            return new DebloatExecutionResult(true, result.Succeeded, result, summary, current);
        }
        finally { executing.Release(); }
    }

    public async Task<DebloatHistorySnapshot> LoadHistoryAsync(CancellationToken cancellationToken = default,
        bool includeControlledRemoval = false)
    {
        var snapshot = await journal.LoadTransactionsAsync(cancellationToken);
        var result = new List<DebloatHistoryItem>();
        var warnings = snapshot.Warnings.ToList();
        foreach (var record in snapshot.Transactions)
        {
            try
            {
                if (record.OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal))
                {
                    if (includeControlledRemoval)
                    {
                        var removal = DebloatHistoryValidation.ReadRemoval(record, Catalog);
                        var package = removal.Observation.SelectedPackage;
                        var receipt = record.Events.LastOrDefault(e => e.RemovalReceipt is not null)?.RemovalReceipt;
                        result.Add(new DebloatHistoryItem(record.TransactionId, removal.CatalogEntry.FriendlyName,
                            package.PackageFullName, package.Publisher, package.Version, package.UserSecurityId,
                            "SINTÉTICO — " + record.State, record.Succeeded, record.IsIndeterminate, record.UpdatedAtUtc,
                            "Reinstalação real indisponível. Orientação documental: " + removal.Observation.ReinstallInformation,
                            string.Join(" ", record.Limitations.Append(DebloatLimits.RecoveryWarning)),
                            journal.GetTransactionReportPath(record.TransactionId))
                        {
                            Evidence = DebloatRemovalPresentation.HistoricalEvidence(removal, receipt, record)
                        });
                    }
                    continue;
                }
                var state = DebloatHistoryValidation.ReadDebloat(record);
                result.Add(new DebloatHistoryItem(record.TransactionId, state.Package.FriendlyName,
                    state.Package.PackageFullName, state.Package.Publisher, state.Package.Version,
                    state.Package.UserId, "SINTÉTICO — " + record.State,
                    record.Succeeded, record.IsIndeterminate, record.UpdatedAtUtc,
                    state.CatalogEntry.ReinstallInformation,
                    string.Join(" ", record.Limitations.Append(DebloatLimits.RecoveryWarning)),
                    journal.GetTransactionReportPath(record.TransactionId)));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or InvalidDataException or NullReferenceException)
            { warnings.Add($"Transação Debloat {record.TransactionId:D} excluída da apresentação: {error.Message} Original preservado."); }
        }
        return new DebloatHistorySnapshot(result, warnings);
    }

    public async Task<IReadOnlyList<DebloatHistoryItem>> HistoryAsync(CancellationToken cancellationToken = default) =>
        (await LoadHistoryAsync(cancellationToken)).Items;

    public async Task<IReadOnlyList<string>> HistoryWarningsAsync(CancellationToken cancellationToken = default) =>
        (await LoadHistoryAsync(cancellationToken)).Warnings;

    private async Task<IReadOnlyList<DebloatPackage>> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        if (!inventory.SyntheticOnly) throw new InvalidOperationException("Inventário real recusado.");
        var values = await inventory.ReadAsync(cancellationToken);
        if (values.Count > DebloatLimits.MaximumInventory || values.Any(p => !p.Synthetic))
            throw new InvalidOperationException("Inventário fora dos limites ou sem proveniência sintética.");
        return values;
    }

    private Prepared Resolve(DebloatPreparedPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!prepared.TryGetValue(plan.Id, out var context) ||
            context.Plan.PlanHash != plan.PlanHash || Hash(plan) != plan.PlanHash ||
            context.Plan.Confirmation != plan.Confirmation)
            throw new InvalidOperationException("Plano desconhecido, adulterado ou de outra sessão.");
        return context;
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static string Hash(DebloatPreparedPlan plan) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { plan.Id, plan.Package, plan.CatalogEntry, plan.Risk, plan.Scenario,
            plan.MediumReviewConfirmed, plan.Simulation, plan.Consequences, plan.Limitations }))));
    private sealed record Prepared(DebloatPreparedPlan Plan, SafetyEngine Engine, IDebloatSyntheticRuntime Runtime);
    private sealed class SpecificConfirmation : IAuthorizationConfirmationPolicy
    {
        public string Expected { get; set; } = "";
        public bool IsValid(OperationPlan plan, string? confirmation) =>
            SafetyText.IsValidConfirmation(confirmation) && string.Equals(Expected, confirmation, StringComparison.Ordinal);
    }
    private sealed class FixtureProfile(TimeProvider time) : ISystemProfileReader
    {
        public SystemProfile Read() => new("Windows 11 — fixture T3", 26100, true, false, [], [], [],
            "Fixture sintética T3; nenhum perfil real consultado", time.GetUtcNow(), []);
    }
    private sealed class FixtureProtection : IProtectionProvider
    {
        public Task<ProtectionStatus> ProbeAsync(ProtectionPolicy policy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProtectionStatus(false, "Nenhum ponto de restauração real criado.",
                ["Proteção limitada ao snapshot de metadados sintéticos; não permite reinstalação/rollback de apps."]));
    }
    private sealed class UnavailableJournal(ITransactionJournalStore inner) : ITransactionJournalStore
    {
        public string GetTransactionReportPath(Guid id) => inner.GetTransactionReportPath(id);
        public Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry, CancellationToken token = default) =>
            throw new IOException("Persistência indisponível — fixture antes do efeito.");
        public Task<string> WriteBackupAsync(BackupRecord backup, CancellationToken token = default) => inner.WriteBackupAsync(backup, token);
        public Task<BackupRecord> ReadBackupAsync(string path, CancellationToken token = default) => inner.ReadBackupAsync(path, token);
        public Task<TransactionJournalSnapshot> LoadTransactionsAsync(CancellationToken token = default) => inner.LoadTransactionsAsync(token);
        public Task<TransactionRecord?> FindTransactionAsync(Guid id, CancellationToken token = default) => inner.FindTransactionAsync(id, token);
    }
}
