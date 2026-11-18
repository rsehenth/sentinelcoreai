using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Run HKCU textual, com restauração cifrada. HKLM e outras origens exigem decisão futura.</summary>
public sealed class RunStartupChangeExecutor(IRunStartupAccess access, IAppsOperationStore store,
    TimeProvider? clock = null) : IStartupChangeExecutor, IJournaledAppsExecutor
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public bool IsSynthetic => access.IsSynthetic;

    public async Task<StartupChangeResult> ExecuteAsync(StartupChangeRequest request, CancellationToken cancellationToken)
    {
        var entry = request.Entry;
        if (!IsSynthetic && !AppsOperationalExecutionGate.PermitsExecution)
            return new(AppsOperationOutcome.GateClosed, "gate", null);
        if (IsSynthetic != (entry.Origin == AppsDataOrigin.Synthetic) || !AppsOperationIdentity.ValidRunIdentity(entry) ||
            !Enum.IsDefined(request.Action) || request.ExpectedStateToken != entry.StateToken ||
            request.Confirmation != AppsOperationConfirmation.Startup(entry, request.Action) ||
            AppsStartupPolicy.Evaluate(entry).Eligibility != ChangeEligibility.Eligible ||
            entry.State != (request.Action == StartupAction.Disable ? StartupConfiguredState.Enabled : StartupConfiguredState.Disabled))
            return new(AppsOperationOutcome.Blocked, "invalid-request", null);
        try
        {
            using var lease = store.AcquireAppsOperationLease();
            var history = await store.ReadAppsOperationsAsync(cancellationToken);
            if (AppsOperationJournal.Unresolved(history, entry.StableId, entry.Origin))
                return new(AppsOperationOutcome.Blocked, "reconciliation-required", null);
            var original = entry;
            Guid? reverses = null;
            string protectedState;
            if (request.Action == StartupAction.Enable)
            {
                var backup = history.Records.LastOrDefault(r => r.Intent.TargetKey == AppsOperationIdentity.Target(entry.StableId) &&
                    r.Intent.Origin == entry.Origin && r.Intent.Kind == AppsOperationKind.StartupDisable &&
                    r.Outcome == AppsOperationOutcome.Succeeded && !history.Records.Any(e =>
                        e.Intent.ReversesOperationId == r.Intent.Id && e.Outcome == AppsOperationOutcome.Succeeded));
                if (backup?.Intent.ProtectedStartupState is not { } ciphertext)
                    return new(AppsOperationOutcome.Blocked, "no-restoration-state", null);
                original = store.UnprotectStartupState(ciphertext, entry.Origin);
                if (original.RegistryIdentity != entry.RegistryIdentity || original.StableId != entry.StableId ||
                    original.Reference != entry.Reference || original.Scope != entry.Scope || original.State != StartupConfiguredState.Enabled)
                    return new(AppsOperationOutcome.Conflict, "restoration-identity-conflict", null);
                protectedState = ciphertext;
                reverses = backup.Intent.Id;
            }
            else protectedState = store.ProtectStartupState(entry);

            var identity = original.RegistryIdentity!;
            var observed = access.Read(identity);
            var intent = AppsOperationJournal.Intent(request.Action == StartupAction.Disable ? AppsOperationKind.StartupDisable :
                AppsOperationKind.StartupEnable, entry.Origin, entry.StableId, entry.Scope, entry.Source.ToString(),
                entry.StateToken, entry.State.ToString(), request.Confirmation!, time, protectedState, reverses);
            var result = await AppsOperationJournal.ExecuteAsync(store, intent, () =>
            {
                // Recheck after persistence; registry and file writes are deliberately not claimed atomic.
                if (!Matches(observed, original, request.Action == StartupAction.Disable) ||
                    !Matches(access.Read(identity), original, request.Action == StartupAction.Disable))
                    return Task.FromResult(new AppsExecutionResult(AppsOperationOutcome.Conflict, "concurrent-change"));
                var changed = request.Action == StartupAction.Disable
                    ? access.DeleteIfUnchanged(identity, observed)
                    : access.RestoreIfAbsent(identity, original.Reference);
                if (!changed) return Task.FromResult(new AppsExecutionResult(AppsOperationOutcome.Conflict, "compare-write-conflict"));
                var verified = Matches(access.Read(identity), original, request.Action == StartupAction.Enable);
                return Task.FromResult(new AppsExecutionResult(verified ? AppsOperationOutcome.Succeeded :
                    AppsOperationOutcome.Indeterminate, verified ? "verified" : "verification-indeterminate"));
            }, time, cancellationToken);
            return new(result.Outcome, result.Code, null);
        }
        catch (Exception) { return new(AppsOperationOutcome.Blocked, "preflight-or-protection-unavailable", null); }
    }

    private static bool Matches(RunValueState state, StartupEntry original, bool exists) => state.Eligible &&
        state.Exists == exists && (!exists || state.Content == original.Reference && state.ValueKind == original.RegistryIdentity!.ValueKind);
}
