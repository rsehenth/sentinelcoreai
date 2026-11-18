using SentinelCore.Domain;

namespace SentinelCore.Application;

public static class AppsStartupPolicy
{
    public static StartupEligibility Evaluate(StartupEntry entry)
    {
        var privilege = entry.Scope == AppsScope.Machine;
        if (!Enum.IsDefined(entry.Source) || !Enum.IsDefined(entry.Scope) || !Enum.IsDefined(entry.State) || !Enum.IsDefined(entry.Origin))
            return new(ChangeEligibility.Unavailable, ChangeBlockReason.UnknownState, privilege);
        if (entry.Origin == AppsDataOrigin.Operational && entry.Source is StartupSourceKind.StartupFolder or StartupSourceKind.PackagedStartupTask)
            return new(ChangeEligibility.ReadOnly, ChangeBlockReason.NoReversibleMechanism, privilege);
        if (entry.Origin == AppsDataOrigin.Operational && entry.Source == StartupSourceKind.RegistryRun &&
            !AppsOperationIdentity.ValidRunIdentity(entry))
            return new(ChangeEligibility.ReadOnly, ChangeBlockReason.NoReversibleMechanism, privilege);
        switch (entry.Source)
        {
            case StartupSourceKind.RegistryRunOnce or StartupSourceKind.Service or StartupSourceKind.Driver
                or StartupSourceKind.SecurityComponent or StartupSourceKind.WindowsInternalTask:
                return new(ChangeEligibility.ReadOnly, ChangeBlockReason.NotAnApplication, privilege);
            case StartupSourceKind.ScheduledTask:
                return new(ChangeEligibility.ReadOnly, ChangeBlockReason.ConservativeScheduledTask, privilege);
            case StartupSourceKind.Other:
                return new(ChangeEligibility.ReadOnly, ChangeBlockReason.NoReversibleMechanism, privilege);
        }
        if (entry.State == StartupConfiguredState.Unknown)
            return new(ChangeEligibility.Unavailable, ChangeBlockReason.UnknownState, privilege);
        if (string.IsNullOrWhiteSpace(entry.StableId) || string.IsNullOrWhiteSpace(entry.Reference))
            return new(ChangeEligibility.Unavailable, ChangeBlockReason.IdentityIncomplete, privilege);
        return new(ChangeEligibility.Eligible, ChangeBlockReason.None, privilege);
    }

    public static UninstallEligibility Evaluate(InstalledApplicationEntry app)
    {
        var privilege = app.Scope == AppsScope.Machine;
        if (!Enum.IsDefined(app.Kind) || !Enum.IsDefined(app.Scope) || !Enum.IsDefined(app.Origin))
            return new(ChangeEligibility.Unavailable, ChangeBlockReason.UnknownState, UninstallMethod.None, privilege);
        if (app.IsSystemOrFramework)
            return new(ChangeEligibility.Unavailable, ChangeBlockReason.SystemOrFramework, UninstallMethod.None, privilege);
        switch (app.Kind)
        {
            case InstalledApplicationKind.Msi:
                if (app.Origin == AppsDataOrigin.Operational && app.Scope == AppsScope.Machine)
                    return new(ChangeEligibility.ReadOnly, ChangeBlockReason.NoReliableUninstallMechanism, UninstallMethod.None, privilege);
                return Guid.TryParse(app.ProductCode, out _)
                    ? new(ChangeEligibility.Eligible, ChangeBlockReason.None, UninstallMethod.MsiProductCode, privilege)
                    : new(ChangeEligibility.Unavailable, ChangeBlockReason.IdentityIncomplete, UninstallMethod.None, privilege);
            case InstalledApplicationKind.Msix:
                if (app.PackageClassification == "ApplicationCandidate")
                    return new(ChangeEligibility.ReadOnly, ChangeBlockReason.ProtectedEntry, UninstallMethod.None, privilege);
                if (app.Scope == AppsScope.Machine)
                    return new(ChangeEligibility.Unavailable, ChangeBlockReason.AllUsersNotSupported, UninstallMethod.None, privilege);
                return string.IsNullOrWhiteSpace(app.PackageFullName)
                    ? new(ChangeEligibility.Unavailable, ChangeBlockReason.IdentityIncomplete, UninstallMethod.None, privilege)
                    : new(ChangeEligibility.Eligible, ChangeBlockReason.None, UninstallMethod.MsixPackageFullName, privilege);
            default:
                // UninstallString nunca é executada: sem GUID MSI validável não há mecanismo confiável.
                return new(ChangeEligibility.Unavailable, ChangeBlockReason.NoReliableUninstallMechanism, UninstallMethod.None, privilege);
        }
    }
}

public static class AppsStartupFilters
{
    public static IReadOnlyList<StartupEntry> Filter(IEnumerable<StartupEntry> entries, string? search,
        StartupSourceKind? source, StartupConfiguredState? state) =>
        entries.Where(e => Matches(e.Name, e.Publisher, e.Reference, search) &&
            (source is null || e.Source == source) && (state is null || e.State == state)).ToArray();

    public static IReadOnlyList<InstalledApplicationEntry> Filter(IEnumerable<InstalledApplicationEntry> apps,
        string? search, InstalledApplicationKind? kind, AppsScope? scope) =>
        apps.Where(a => Matches(a.Name, a.Publisher, a.Version, search) &&
            (kind is null || a.Kind == kind) && (scope is null || a.Scope == scope)).ToArray();

    private static bool Matches(string name, string? publisher, string? other, string? search) =>
        string.IsNullOrWhiteSpace(search) ||
        new[] { name, publisher, other }.Any(v => v?.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase) == true);

    /// <summary>Remove duplicatas por identidade exata; nomes iguais com identidades distintas permanecem.</summary>
    public static (IReadOnlyList<T> Items, int Duplicates) Deduplicate<T>(IEnumerable<T> items, Func<T, string> id)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = items.Where(item => seen.Add(id(item))).ToArray();
        return (kept, items.Count() - kept.Length);
    }
}

public sealed record AppsStartupSnapshot(IReadOnlyList<StartupEntry> Startup, IReadOnlyList<InstalledApplicationEntry> Apps,
    IReadOnlyList<string> Warnings, bool Complete)
{
    public static AppsStartupSnapshot Empty { get; } = new([], [], [], false);
}

public sealed record StartupChangePreview(StartupEntry Entry, StartupAction Action, StartupEligibility Eligibility,
    string ConfirmationPhrase, bool CanExecute);

public sealed record UninstallPreview(InstalledApplicationEntry Entry, UninstallEligibility Eligibility,
    string ConfirmationPhrase, bool CanExecute);

public sealed record AppsStartupHistoryEntry(DateTimeOffset AtUtc, string Kind, string StableId, string Name, string Action,
    AppsOperationOutcome Outcome, string Detail, bool Synthetic)
{
    public Guid? OperationId { get; init; }
    public int? NativeCode { get; init; }
    public bool RestartRequired { get; init; }
}

/// <summary>
/// Orquestra inventário, prévia, confirmação e execução individual. Fronteira de segurança: um item operacional só chega
/// a um executor se o gate correspondente permitir (hoje: nunca); itens/executores sintéticos nunca se misturam com reais.
/// </summary>
public sealed class AppsStartupManager
{
    private readonly IStartupInventoryProvider startupProvider;
    private readonly IInstalledApplicationProvider appProvider;
    private readonly IStartupChangeExecutor startupExecutor;
    private readonly IApplicationUninstallExecutor uninstallExecutor;
    private readonly TimeProvider time;
    private readonly IAppsOperationStore? operationStore;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<AppsStartupHistoryEntry> history = [];

    public AppsStartupManager(IStartupInventoryProvider startupProvider, IInstalledApplicationProvider appProvider,
        IStartupChangeExecutor startupExecutor, IApplicationUninstallExecutor uninstallExecutor, TimeProvider? time = null,
        IAppsOperationStore? operationStore = null)
    {
        this.startupProvider = startupProvider;
        this.appProvider = appProvider;
        this.startupExecutor = startupExecutor;
        this.uninstallExecutor = uninstallExecutor;
        this.time = time ?? TimeProvider.System;
        this.operationStore = operationStore;
    }

    public AppsStartupSnapshot Snapshot { get; private set; } = AppsStartupSnapshot.Empty;
    public IReadOnlyList<AppsStartupHistoryEntry> History { get { lock (history) return history.ToArray(); } }

    public async Task<AppsStartupSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var startup = await startupProvider.ReadAsync(cancellationToken);
        var apps = await appProvider.ReadAsync(cancellationToken);
        var (startupItems, startupDup) = AppsStartupFilters.Deduplicate(startup.Entries, e => e.StableId);
        var (appItems, appDup) = AppsStartupFilters.Deduplicate(apps.Entries, a => a.StableId);
        var warnings = startup.Warnings.Concat(apps.Warnings).ToList();
        if (operationStore is not null)
        {
            try
            {
                var stored = await operationStore.ReadAppsOperationsAsync(cancellationToken);
                warnings.AddRange(stored.Warnings);
                lock (history)
                {
                    history.RemoveAll(h => h.OperationId is not null);
                    foreach (var record in stored.Records)
                    {
                        var intent = record.Intent;
                        var startupEntry = startupItems.FirstOrDefault(e => e.Origin == intent.Origin && AppsOperationIdentity.Target(e.StableId) == intent.TargetKey);
                        var app = appItems.FirstOrDefault(e => e.Origin == intent.Origin && AppsOperationIdentity.Target(e.StableId) == intent.TargetKey);
                        var isStartup = intent.Kind is AppsOperationKind.StartupDisable or AppsOperationKind.StartupEnable;
                        history.Add(new(record.Completion?.CompletedUtc ?? intent.AtUtc, isStartup ? "startup" : "uninstall",
                            startupEntry?.StableId ?? app?.StableId ?? intent.TargetKey,
                            startupEntry?.Name ?? app?.Name ?? intent.ApplicationIdentity ?? intent.TargetKey[..12],
                            isStartup ? intent.Kind == AppsOperationKind.StartupDisable ? "disable" : "enable" : "uninstall",
                            record.Outcome, record.Completion?.Code ?? "interrupted", intent.Origin == AppsDataOrigin.Synthetic)
                            { OperationId = intent.Id, NativeCode = record.Completion?.NativeCode,
                                RestartRequired = record.Completion?.RestartRequired == true });
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { warnings.Add("apps-history:unavailable"); }
        }
        if (startupDup + appDup > 0) warnings.Add($"duplicates:{startupDup + appDup}");
        return Snapshot = new(startupItems, appItems, warnings, startup.Complete && apps.Complete && warnings.Count == 0);
    }

    public StartupChangePreview PreviewStartupChange(StartupEntry entry, StartupAction action)
    {
        var eligibility = AppsStartupPolicy.Evaluate(entry);
        var wrongState = action == StartupAction.Disable ? StartupConfiguredState.Disabled : StartupConfiguredState.Enabled;
        var can = Enum.IsDefined(action) && eligibility.Eligibility == ChangeEligibility.Eligible && entry.State != wrongState;
        var verb = action == StartupAction.Disable ? "DESATIVAR" : "REATIVAR";
        var phrase = $"CONFIRMAR INICIALIZACAO {verb} {AppsStartupHashing.Short($"{entry.StableId}|{action}|{entry.StateToken}")}";
        return new(entry, action, eligibility, phrase, can);
    }

    public UninstallPreview PreviewUninstall(InstalledApplicationEntry app)
    {
        var eligibility = AppsStartupPolicy.Evaluate(app);
        var phrase = $"CONFIRMAR DESINSTALACAO {AppsStartupHashing.Short($"{app.StableId}|{eligibility.Method}|{app.StateToken}")}";
        return new(app, eligibility, phrase, eligibility.Eligibility == ChangeEligibility.Eligible);
    }

    public async Task<StartupChangeResult> ExecuteStartupChangeAsync(StartupChangePreview preview, string confirmation,
        CancellationToken cancellationToken)
    {
        var action = preview.Action == StartupAction.Disable ? "disable" : "enable";
        if (preview != PreviewStartupChange(preview.Entry, preview.Action) || !preview.CanExecute ||
            !string.Equals(confirmation, preview.ConfirmationPhrase, StringComparison.Ordinal))
            return Record("startup", preview.Entry.StableId, preview.Entry.Name, action, preview.Entry.Origin,
                new StartupChangeResult(AppsOperationOutcome.Blocked, "blocked", null));
        var synthetic = preview.Entry.Origin == AppsDataOrigin.Synthetic;
        if (synthetic != startupExecutor.IsSynthetic ||
            !synthetic && !StartupModificationBuildGate.PermitsOperationalExecution)
            return Record("startup", preview.Entry.StableId, preview.Entry.Name, action, preview.Entry.Origin,
                new StartupChangeResult(AppsOperationOutcome.GateClosed, "gate", null));

        await gate.WaitAsync(cancellationToken);
        try
        {
            StartupChangeResult result;
            try
            {
                var request = new StartupChangeRequest(preview.Entry, preview.Action, preview.Entry.StateToken) { Confirmation = confirmation };
                if (operationStore is not null && startupExecutor is not IJournaledAppsExecutor)
                {
                    using var lease = operationStore.AcquireAppsOperationLease();
                    var intent = AppsOperationJournal.Intent(preview.Action == StartupAction.Disable ? AppsOperationKind.StartupDisable :
                        AppsOperationKind.StartupEnable, preview.Entry.Origin, preview.Entry.StableId, preview.Entry.Scope,
                        preview.Entry.Source.ToString(), preview.Entry.StateToken, preview.Entry.State.ToString(), confirmation, time);
                    var persisted = await AppsOperationJournal.ExecuteAsync(operationStore, intent, async () =>
                    {
                        var executed = await startupExecutor.ExecuteAsync(request, CancellationToken.None);
                        return new(executed.Outcome, executed.Detail);
                    }, time, cancellationToken);
                    result = new(persisted.Outcome, persisted.Code, null);
                }
                else result = await startupExecutor.ExecuteAsync(request, cancellationToken);
            }
            catch (Exception) // possível efeito parcial: nunca tratar como sucesso nem como falha limpa.
            {
                result = new(AppsOperationOutcome.Indeterminate, "exception", null);
            }
            if (operationStore is not null) { await ReconcileAsync(); return result; }
            return Record("startup", preview.Entry.StableId, preview.Entry.Name, action, preview.Entry.Origin, result);
        }
        finally { gate.Release(); }
    }

    public async Task<UninstallResult> ExecuteUninstallAsync(UninstallPreview preview, string confirmation,
        CancellationToken cancellationToken)
    {
        if (preview != PreviewUninstall(preview.Entry) || !preview.CanExecute ||
            !string.Equals(confirmation, preview.ConfirmationPhrase, StringComparison.Ordinal))
            return Record("uninstall", preview.Entry.StableId, preview.Entry.Name, "uninstall", preview.Entry.Origin,
                new UninstallResult(AppsOperationOutcome.Blocked, "blocked"));
        var synthetic = preview.Entry.Origin == AppsDataOrigin.Synthetic;
        if (synthetic != uninstallExecutor.IsSynthetic ||
            !synthetic && !ApplicationUninstallBuildGate.PermitsOperationalExecution)
            return Record("uninstall", preview.Entry.StableId, preview.Entry.Name, "uninstall", preview.Entry.Origin,
                new UninstallResult(AppsOperationOutcome.GateClosed, "gate"));

        await gate.WaitAsync(cancellationToken);
        try
        {
            UninstallResult result;
            try
            {
                var request = new UninstallRequest(preview.Entry, preview.Eligibility.Method, preview.Entry.StateToken) { Confirmation = confirmation };
                if (operationStore is not null && uninstallExecutor is not IJournaledAppsExecutor)
                {
                    using var lease = operationStore.AcquireAppsOperationLease();
                    var intent = AppsOperationJournal.Intent(preview.Entry.Kind == InstalledApplicationKind.Msi ? AppsOperationKind.MsiUninstall :
                        AppsOperationKind.MsixUninstall, preview.Entry.Origin, preview.Entry.StableId, preview.Entry.Scope,
                        preview.Entry.Kind.ToString(), preview.Entry.StateToken, "Installed", confirmation, time,
                        applicationIdentity: preview.Entry.Kind == InstalledApplicationKind.Msi ? preview.Entry.ProductCode : preview.Entry.PackageFullName);
                    var persisted = await AppsOperationJournal.ExecuteAsync(operationStore, intent, async () =>
                    {
                        var executed = await uninstallExecutor.ExecuteAsync(request, CancellationToken.None);
                        return new(executed.Outcome, executed.Detail, executed.NativeCode, executed.RestartRequired);
                    }, time, cancellationToken);
                    result = new(persisted.Outcome, persisted.Code) { NativeCode = persisted.NativeCode, RestartRequired = persisted.RestartRequired };
                }
                else result = await uninstallExecutor.ExecuteAsync(request, cancellationToken);
            }
            catch (Exception)
            {
                result = new(AppsOperationOutcome.Indeterminate, "exception");
            }
            if (operationStore is not null) { await ReconcileAsync(); return result; }
            return Record("uninstall", preview.Entry.StableId, preview.Entry.Name, "uninstall", preview.Entry.Origin, result);
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileAsync()
    {
        try { await LoadAsync(CancellationToken.None); }
        catch (Exception) { Snapshot = Snapshot with { Complete = false, Warnings = [.. Snapshot.Warnings, "inventory:reconciliation-unavailable"] }; }
    }

    private T Record<T>(string kind, string id, string name, string action, AppsDataOrigin origin, T result)
    {
        var (outcome, detail) = result switch
        {
            StartupChangeResult s => (s.Outcome, s.Detail),
            UninstallResult u => (u.Outcome, u.Detail),
            _ => throw new InvalidOperationException()
        };
        lock (history)
            history.Add(new(time.GetUtcNow(), kind, id, name, action, outcome, detail, origin == AppsDataOrigin.Synthetic));
        return result;
    }
}
