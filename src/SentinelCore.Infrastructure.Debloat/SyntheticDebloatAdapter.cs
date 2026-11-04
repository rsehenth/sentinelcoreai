using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Debloat;

/// <summary>Adapts a single exact fixture to the shared Safety Engine, never to Windows.</summary>
public sealed class SyntheticDebloatAdapter : IDebloatSyntheticRuntime
{
    private readonly SyntheticDebloatInventory inventory;
    private readonly DebloatPackage selected;
    private readonly DebloatScenario scenario;
    private readonly DebloatCatalogEntry entry;
    private readonly bool mediumReviewConfirmed;
    private readonly string operationId;
    private readonly OperationTarget target;
    private readonly TimeProvider time;
    private bool unverifiable;

    public SyntheticDebloatAdapter(SyntheticDebloatInventory inventory, DebloatPackage selected,
        DebloatCatalogEntry entry, DebloatScenario scenario, bool mediumReviewConfirmed,
        string operationId, OperationTarget target, TimeProvider? time = null)
    {
        this.inventory = inventory; this.selected = selected; this.scenario = scenario;
        this.entry = entry; this.mediumReviewConfirmed = mediumReviewConfirmed;
        this.operationId = operationId; this.target = target; this.time = time ?? TimeProvider.System;
    }

    public static TargetValueState State(DebloatPackage package, DebloatCatalogEntry entry,
        DebloatScenario scenario, bool mediumReviewConfirmed) =>
        new(true, "DebloatPackage", JsonSerializer.Serialize(new DebloatTargetSnapshot(package, entry, scenario, mediumReviewConfirmed)));

    private TargetValueState State(DebloatPackage package) => State(package, entry, scenario, mediumReviewConfirmed);

    public void AdvanceFixtureBeforeExecution()
    {
        if (scenario == DebloatScenario.UpdatedBeforeExecution)
            inventory.Set(selected with { Version = "2.0.0.0",
                PackageFullName = $"{selected.TechnicalName}_2.0.0.0_{selected.Architecture}__synthetic" });
    }

    public async Task<ObservedTargetState> ReadAsync(OperationTarget request, CancellationToken cancellationToken = default)
    {
        if (request != target || request.Root != OperationTargetRoots.Synthetic || unverifiable)
            return ObservedTargetState.Unknown(request.Identity, time.GetUtcNow(), "Fixture indisponível ou alvo recusado.");
        var all = await inventory.ReadAsync(cancellationToken);
        var items = all.Where(p => p.CatalogId == selected.CatalogId || p.TechnicalName == selected.TechnicalName ||
            p.PackageFamilyName == selected.PackageFamilyName).ToList();
        if (items.Count != 1)
            return ObservedTargetState.Unknown(request.Identity, time.GetUtcNow(), "Fixture ausente/duplicada.");
        if (all.Any(p => p.CatalogId != selected.CatalogId && p.Dependencies.Any(d =>
                d.PackageFamilyName == selected.PackageFamilyName)))
            return ObservedTargetState.Unknown(request.Identity, time.GetUtcNow(), "Dependência reversa detectada na fixture.");
        return new ObservedTargetState(request.Identity, true, true, State(items[0]), time.GetUtcNow(), null);
    }

    public async Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OperationId != operationId || !operationId.StartsWith("DBLT-", StringComparison.Ordinal) ||
            request.Target != target || target.Root != OperationTargetRoots.Synthetic || request.Kind != ChangeKind.SetValue ||
            !selected.Synthetic || selected.UserId != inventory.CurrentUserId || selected.Protected || selected.SystemPackage ||
            selected.Publisher != DebloatFixtureIdentity.Publisher || entry.Id != selected.CatalogId ||
            entry.TechnicalName != selected.TechnicalName || entry.RemovalMethod != "Appx" ||
            entry.PreliminaryRisk is not (RiskLevel.Low or RiskLevel.Medium) ||
            entry.PreliminaryRisk == RiskLevel.Medium && !mediumReviewConfirmed ||
            entry.TechnicalName is not ("Clipchamp.Clipchamp" or "Microsoft.BingWeather" or
                "Microsoft.WindowsCalculator" or "Microsoft.WindowsNotepad" or "Microsoft.MicrosoftSolitaireCollection") ||
            selected.Kind != DebloatPackageKind.Appx || selected.Scope != DebloatScope.CurrentUser || selected.Provisioned is not false ||
            selected.DependencyStatus != DebloatDependencyStatus.KnownNone || selected.Dependencies.Count != 0)
            return ExecutionOutcome.Failed("Fronteira sintética: identidade, escopo ou operação recusados.");
        var observed = await ReadAsync(target, cancellationToken);
        if (!observed.IsKnown || observed.Value != request.Before || request.Before != State(selected) ||
            request.After != State(selected with { State = DebloatInstallState.RemovedSynthetic }))
            return ExecutionOutcome.Failed("Identidade/estado alterado ou pós-estado não corresponde à remoção sintética.");
        switch (scenario)
        {
            case DebloatScenario.AccessDenied: return ExecutionOutcome.Failed("Acesso negado — fixture.");
            case DebloatScenario.InUse: return ExecutionOutcome.Failed("Aplicativo em uso — fixture; nenhum processo encerrado.");
            case DebloatScenario.Interrupted: throw new OperationCanceledException("Interrupção sintética após intenção.");
            case DebloatScenario.Cancelled: throw new OperationCanceledException("Cancelamento sintético.");
            case DebloatScenario.Indeterminate: return ExecutionOutcome.Unknown("Estado indeterminado — fixture.");
            case DebloatScenario.PartialFailure:
                inventory.Set(selected with { State = DebloatInstallState.Unknown });
                return ExecutionOutcome.Unknown("Falha parcial sintética: registro inconsistente; remoção não confirmada.");
            case DebloatScenario.Unverifiable:
                inventory.Set(selected with { State = DebloatInstallState.RemovedSynthetic });
                unverifiable = true;
                return ExecutionOutcome.Success();
            case DebloatScenario.Success:
                inventory.Set(selected with { State = DebloatInstallState.RemovedSynthetic });
                return ExecutionOutcome.Success();
            default: return ExecutionOutcome.Failed("Cenário bloqueado ou incompatível.");
        }
    }
}

public sealed class SyntheticDebloatRuntimeFactory(SyntheticDebloatInventory inventory) : IDebloatRuntimeFactory
{
    public bool SyntheticOnly => true;
    public IDebloatSyntheticRuntime Create(DebloatPackage package, DebloatCatalogEntry entry, DebloatScenario scenario,
        bool mediumReviewConfirmed, string operationId, OperationTarget target, TimeProvider time) =>
        new SyntheticDebloatAdapter(inventory, package, entry, scenario, mediumReviewConfirmed, operationId, target, time);
}
