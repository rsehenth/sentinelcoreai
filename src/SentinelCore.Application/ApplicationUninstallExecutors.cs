using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Roteia exatamente um pedido ao mecanismo tipado; os dois destinos mantêm journal próprio.</summary>
public sealed class IndividualApplicationUninstallExecutor : IApplicationUninstallExecutor, IJournaledAppsExecutor
{
    private readonly IApplicationUninstallExecutor msi;
    private readonly IApplicationUninstallExecutor msix;
    public IndividualApplicationUninstallExecutor(IApplicationUninstallExecutor msi, IApplicationUninstallExecutor msix)
    {
        if (msi.IsSynthetic != msix.IsSynthetic || msi is not IJournaledAppsExecutor || msix is not IJournaledAppsExecutor)
            throw new ArgumentException("incompatible-executors");
        this.msi = msi;
        this.msix = msix;
    }
    public bool IsSynthetic => msi.IsSynthetic;
    public Task<UninstallResult> ExecuteAsync(UninstallRequest request, CancellationToken cancellationToken) => request.Method switch
    {
        UninstallMethod.MsiProductCode => msi.ExecuteAsync(request, cancellationToken),
        UninstallMethod.MsixPackageFullName => msix.ExecuteAsync(request, cancellationToken),
        _ => Task.FromResult(new UninstallResult(AppsOperationOutcome.Blocked, "unsupported-method"))
    };
}

public sealed class MsiApplicationUninstallExecutor(IMsiUninstallApi api, IAppsOperationStore store,
    TimeProvider? clock = null) : IApplicationUninstallExecutor, IJournaledAppsExecutor
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public bool IsSynthetic => api.IsSynthetic;

    public async Task<UninstallResult> ExecuteAsync(UninstallRequest request, CancellationToken cancellationToken)
    {
        if (!IsSynthetic && !AppsOperationalExecutionGate.PermitsExecution) return new(AppsOperationOutcome.GateClosed, "gate");
        var entry = request.Entry;
        if (!AppsUninstallValidation.Request(request, IsSynthetic, InstalledApplicationKind.Msi, UninstallMethod.MsiProductCode) ||
            !Guid.TryParseExact(entry.ProductCode, "B", out _)) return new(AppsOperationOutcome.Blocked, "invalid-request");
        try
        {
            using var lease = store.AcquireAppsOperationLease();
            if (AppsOperationJournal.Unresolved(await store.ReadAppsOperationsAsync(cancellationToken), entry.StableId, entry.Origin))
                return new(AppsOperationOutcome.Blocked, "reconciliation-required");
            var before = api.Inspect(entry.ProductCode!);
            if (before is null || !Matches(before, entry)) return new(AppsOperationOutcome.Blocked, "installation-not-proven");
            var intent = AppsOperationJournal.Intent(AppsOperationKind.MsiUninstall, entry.Origin, entry.StableId, entry.Scope,
                "Msi", entry.StateToken, "Installed", request.Confirmation!, time, applicationIdentity: entry.ProductCode);
            var result = await AppsOperationJournal.ExecuteAsync(store, intent, () =>
            {
                if (api.Inspect(entry.ProductCode!) != before)
                    return Task.FromResult(new AppsExecutionResult(AppsOperationOutcome.Conflict, "installation-changed"));
                var code = api.Uninstall(entry.ProductCode!);
                var restart = code is 3010 or 1641;
                if (code is 0 or 3010)
                {
                    var after = api.Inspect(entry.ProductCode!);
                    var absent = after is null || !after.Installed;
                    return Task.FromResult(new AppsExecutionResult(absent ? AppsOperationOutcome.Succeeded : AppsOperationOutcome.Indeterminate,
                        absent ? "msi-verified" : "msi-verification-indeterminate", (int)code, restart));
                }
                // Installer errors may follow custom actions or partial effects. Never automatically retry them.
                return Task.FromResult(new AppsExecutionResult(code == 1618 ? AppsOperationOutcome.Failed :
                    AppsOperationOutcome.Indeterminate, "msi-native-result", unchecked((int)code), restart));
            }, time, cancellationToken);
            return new(result.Outcome, result.Code) { NativeCode = result.NativeCode, RestartRequired = result.RestartRequired };
        }
        catch (Exception) { return new(AppsOperationOutcome.Blocked, "installation-or-store-unavailable"); }
    }

    private static bool Matches(MsiInstallation actual, InstalledApplicationEntry entry) =>
        actual.Installed && actual.Eligible && actual.Unambiguous && actual.Scope == entry.Scope &&
        actual.Context == "UserUnmanaged" && entry.Scope == AppsScope.CurrentUser &&
        string.Equals(actual.ProductCode, entry.ProductCode, StringComparison.OrdinalIgnoreCase) &&
        actual.Name == entry.Name && actual.Version == entry.Version && actual.Publisher == entry.Publisher;
}

public sealed class MsixApplicationUninstallExecutor(IMsixUninstallApi api, IAppsOperationStore store,
    TimeProvider? clock = null) : IApplicationUninstallExecutor, IJournaledAppsExecutor
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public bool IsSynthetic => api.IsSynthetic;

    public async Task<UninstallResult> ExecuteAsync(UninstallRequest request, CancellationToken cancellationToken)
    {
        if (!IsSynthetic && !AppsOperationalExecutionGate.PermitsExecution) return new(AppsOperationOutcome.GateClosed, "gate");
        var entry = request.Entry;
        if (!AppsUninstallValidation.Request(request, IsSynthetic, InstalledApplicationKind.Msix, UninstallMethod.MsixPackageFullName) ||
            entry.Scope != AppsScope.CurrentUser || string.IsNullOrWhiteSpace(entry.PackageFamilyName) ||
            !AppsOperationIdentity.ValidPackageFullName(entry.PackageFullName))
            return new(AppsOperationOutcome.Blocked, "invalid-request");
        try
        {
            using var lease = store.AcquireAppsOperationLease();
            if (AppsOperationJournal.Unresolved(await store.ReadAppsOperationsAsync(cancellationToken), entry.StableId, entry.Origin))
                return new(AppsOperationOutcome.Blocked, "reconciliation-required");
            var before = api.Inspect(entry.PackageFullName!);
            if (before is null || !before.CurrentUser || !before.Available || !before.IdentityKnown || before.Framework ||
                before.Dependency || before.Protected || before.FullName != entry.PackageFullName ||
                before.FamilyName != entry.PackageFamilyName || before.Name != entry.Name || before.Version != entry.Version)
                return new(AppsOperationOutcome.Blocked, "package-not-proven");
            var intent = AppsOperationJournal.Intent(AppsOperationKind.MsixUninstall, entry.Origin, entry.StableId, entry.Scope,
                "Msix", entry.StateToken, "Installed", request.Confirmation!, time, applicationIdentity: entry.PackageFullName);
            var result = await AppsOperationJournal.ExecuteAsync(store, intent, async () =>
            {
                if (api.Inspect(entry.PackageFullName!) != before) return new(AppsOperationOutcome.Conflict, "package-changed");
                // No cancellation token reaches the removal after start.
                var receipt = await api.RemoveAsync(entry.PackageFullName!);
                var absent = api.Inspect(entry.PackageFullName!) is null;
                return new(receipt.Completed && receipt.ErrorCode == 0 && absent ? AppsOperationOutcome.Succeeded :
                    AppsOperationOutcome.Indeterminate, receipt.Completed && receipt.ErrorCode == 0 && absent ?
                    "msix-verified" : "msix-indeterminate", receipt.ErrorCode);
            }, time, cancellationToken);
            return new(result.Outcome, result.Code) { NativeCode = result.NativeCode };
        }
        catch (Exception) { return new(AppsOperationOutcome.Blocked, "package-or-store-unavailable"); }
    }
}

internal static class AppsUninstallValidation
{
    internal static bool Request(UninstallRequest request, bool synthetic, InstalledApplicationKind kind, UninstallMethod method) =>
        synthetic == (request.Entry.Origin == AppsDataOrigin.Synthetic) && request.Entry.Kind == kind && request.Method == method &&
        (request.Entry.StableId == AppsIdentity.Application(kind, request.Entry.Scope,
            kind == InstalledApplicationKind.Msi ? request.Entry.ProductCode ?? "" : request.Entry.PackageFullName ?? "") ||
         kind == InstalledApplicationKind.Msi && request.Entry.StableId == AppsIdentity.Application(kind, request.Entry.Scope,
             "wow64|" + request.Entry.ProductCode)) &&
        request.ExpectedStateToken == request.Entry.StateToken &&
        request.Confirmation == AppsOperationConfirmation.Uninstall(request.Entry, method) &&
        AppsStartupPolicy.Evaluate(request.Entry) is { Eligibility: ChangeEligibility.Eligible, Method: var actual } && actual == method;
}
