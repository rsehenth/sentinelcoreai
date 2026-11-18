using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Reintroduz na leitura entradas Run removidas pelo próprio journal, permitindo prévia de reativação.</summary>
public sealed class RecoverableRunStartupProvider(IStartupInventoryProvider inventory, IRunStartupAccess access,
    IAppsOperationStore store) : IStartupInventoryProvider
{
    public async Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        var observed = await inventory.ReadAsync(cancellationToken);
        if (!access.IsSynthetic && !AppsOperationalExecutionGate.PermitsExecution) return observed;
        var entries = observed.Entries.ToList();
        var warnings = observed.Warnings.ToList();
        try
        {
            var history = await store.ReadAppsOperationsAsync(cancellationToken);
            warnings.AddRange(history.Warnings);
            foreach (var record in history.Records.Where(r => r.Intent.Kind == AppsOperationKind.StartupDisable &&
                         r.Outcome == AppsOperationOutcome.Succeeded && r.Intent.ProtectedStartupState is not null &&
                         (r.Intent.Origin == AppsDataOrigin.Synthetic) == access.IsSynthetic &&
                         !history.Records.Any(e => e.Intent.ReversesOperationId == r.Intent.Id && e.Outcome == AppsOperationOutcome.Succeeded)))
            {
                var original = store.UnprotectStartupState(record.Intent.ProtectedStartupState!, record.Intent.Origin);
                if (AppsOperationIdentity.Target(original.StableId) != record.Intent.TargetKey ||
                    AppsOperationJournal.Unresolved(history, original.StableId, original.Origin))
                { warnings.Add("run-restoration:reconciliation-required"); continue; }
                var state = access.Read(original.RegistryIdentity!);
                if (state.Exists || !state.Eligible) { warnings.Add("run-restoration:external-collision"); continue; }
                if (!entries.Any(e => e.StableId == original.StableId))
                    entries.Add(original with { State = StartupConfiguredState.Disabled, Runtime = StartupRuntimeState.NotVerified });
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { warnings.Add("run-restoration:unavailable"); }
        return new(entries, warnings, observed.Complete && warnings.Count == 0);
    }
}
