using System.Security.Principal;
using SentinelCore.Domain;
using Windows.Management.Deployment;

namespace SentinelCore.Infrastructure.Debloat.Removal.Native;

/// <summary>
/// Compilable, unregistered WinRT adapter. Both entry and native call fail closed unconditionally in T3C.
/// Uses exact identity for current-user removal or a separate, explicitly scoped deprovisioning request.
/// </summary>
public sealed class NativeAppxRemovalExecutor(IDebloatNativeInventoryReader reader) : IDebloatRemovalExecutor
{
    public bool SyntheticOnly => false;
    public bool Available => DebloatRemovalBuildGate.AllowsRealExecution();

    public Task<DebloatRemovalReceipt> ExecuteAsync(DebloatRemovalRequest request,
        IProgress<DebloatRemovalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Before any Windows API or SID read. No opt-in exists, including internal calls.
        if (!DebloatRemovalBuildGate.AllowsRealExecution())
            throw new InvalidOperationException(DebloatRemovalMessages.NativeBlocked);
        cancellationToken.ThrowIfCancellationRequested();
        var scope = request.Scope ?? DebloatScope.CurrentUser;
        if (request.Synthetic || scope is not (DebloatScope.CurrentUser or DebloatScope.Provisioned) ||
            !DebloatRemovalIdentityPolicy.HasExactIdentity(request.Package) ||
            request.OperationId == Guid.Empty || request.PlanHash.Length != 64 ||
            !request.PlanHash.All(Uri.IsHexDigit) || request.InventoryRevision.Length != 64 ||
            !request.InventoryRevision.All(Uri.IsHexDigit) || !reader.ReadOnly ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new InvalidOperationException("Requisição nativa inválida.");
        using var identity = WindowsIdentity.GetCurrent();
        if (scope == DebloatScope.CurrentUser && (!DebloatRemovalIdentityPolicy.IsValid(request.Package) ||
                identity.User?.Value != request.Package.UserSecurityId) ||
            scope == DebloatScope.Provisioned && (request.Package.UserSecurityId.Length != 0 ||
                !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)))
            throw new InvalidOperationException("Identidade ou privilégios do token não correspondem ao escopo solicitado.");
        return InvokeBlockedNativeAsync(request, progress, cancellationToken);
    }

    private async Task<DebloatRemovalReceipt> InvokeBlockedNativeAsync(DebloatRemovalRequest request,
        IProgress<DebloatRemovalProgress>? progress, CancellationToken cancellationToken)
    {
        // Defense in depth: even direct invocation of this private method cannot reach PackageManager in T3C.
        if (!DebloatRemovalBuildGate.AllowsRealExecution())
            throw new InvalidOperationException(DebloatRemovalMessages.NativeBlocked);
        var scope = request.Scope ?? DebloatScope.CurrentUser;
        var before = await ReadScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        if (!before.Complete || DebloatRemovalHashing.InventoryRevision(before.Packages) != request.InventoryRevision)
            throw new InvalidOperationException("O inventário mudou ou não pôde ser revalidado.");
        var candidates = before.Packages.Where(p =>
            string.Equals(p.PackageFamilyName, request.Package.PackageFamilyName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates is not [{ } candidate] || DebloatRemovalIdentityPolicy.From(candidate) != request.Package ||
            !candidate.IsMainPackage || candidate.IsFramework || candidate.IsResourcePackage || candidate.IsOptional ||
            candidate.IsBundle || candidate.Kind != DebloatPackageKind.Appx ||
            candidate.Signature is null or DebloatNativeSignature.System or DebloatNativeSignature.Unsigned ||
            scope == DebloatScope.CurrentUser && candidate.InstallState != DebloatNativeInstallState.Available ||
            scope == DebloatScope.Provisioned && candidate.Provisioned != true ||
            candidate.DependencyFullNames is not { Count: 0 } ||
            before.Packages.Any(p => p.DependencyFullNames is null ||
                p.DependencyFullNames.Contains(candidate.PackageFullName, StringComparer.Ordinal)))
            throw new InvalidOperationException("Identidade, estado ou dependências não atendem ao escopo do plano.");
        var expectedRevision = DebloatRemovalHashing.InventoryRevision(before.Packages.Where(p => p != candidate));
        cancellationToken.ThrowIfCancellationRequested();
        var updates = new List<DebloatRemovalProgress>();
        void Report(DebloatRemovalPhase phase, uint? percentage, string information)
        {
            var update = new DebloatRemovalProgress(request.OperationId, phase, percentage, information);
            lock (updates) { if (updates.Count < 256) updates.Add(update); }
            try { progress?.Report(update); } catch { /* An observer cannot abort the Windows request. */ }
        }
        Report(DebloatRemovalPhase.Initial, null, DebloatRemovalMessages.Cancellation);
        try
        {
            var manager = new PackageManager();
            // Deprovisioning takes a family name. Revalidation above requires one exact observed identity.
            var operation = scope == DebloatScope.Provisioned
                ? manager.DeprovisionPackageForAllUsersAsync(request.Package.PackageFamilyName)
                : manager.RemovePackageAsync(request.Package.PackageFullName);
            Report(DebloatRemovalPhase.Started, null, "Solicitação iniciada; não cancelável.");
            operation.Progress = (_, update) => Report(DebloatRemovalPhase.ProgressReported,
                update.percentage <= 100 ? update.percentage : null, update.state.ToString());
            // No CancellationToken, Cancel(), WaitAsync or process termination after initiation.
            var result = await operation.AsTask().ConfigureAwait(false);
            var extended = result.ExtendedErrorCode?.HResult;
            var text = Clean(result.ErrorText);
            var (post, unchanged) = await VerifyAsync(scope, request, expectedRevision).ConfigureAwait(false);
            var apiSucceeded = (extended is null or 0) && (operation.ErrorCode?.HResult is null or 0) && string.IsNullOrEmpty(text);
            var phase = apiSucceeded && post.EffectsMatchPlan ? DebloatRemovalPhase.Completed
                : !apiSucceeded && unchanged ? DebloatRemovalPhase.Failed : DebloatRemovalPhase.Indeterminate;
            Report(phase, null, post.Information);
            DebloatRemovalProgress[] copy;
            lock (updates) copy = updates.ToArray();
            return new(request.OperationId, request.Package, false, phase, true,
                post.Complete && !unchanged && phase != DebloatRemovalPhase.Completed,
                operation.ErrorCode?.HResult ?? 0, extended, text, result.ActivityId,
                copy, DebloatRemovalMessages.Limitations, post) { Scope = request.Scope };
        }
        catch (Exception error)
        {
            var (post, unchanged) = await VerifyAsync(scope, request, expectedRevision).ConfigureAwait(false);
            var phase = unchanged ? DebloatRemovalPhase.Failed : DebloatRemovalPhase.Indeterminate;
            Report(phase, null, post.Information);
            DebloatRemovalProgress[] copy;
            lock (updates) copy = updates.ToArray();
            return new(request.OperationId, request.Package, false, phase,
                true, post.Complete && !unchanged, error.HResult, null, Clean(error.Message), null, copy,
                DebloatRemovalMessages.Limitations, post)
                { Scope = request.Scope };
        }
    }

    private async Task<DebloatInventoryScopeResult> ReadScopeAsync(DebloatScope scope, CancellationToken token)
    {
        var query = scope == DebloatScope.CurrentUser
            ? await reader.ReadCurrentUserAsync(token).ConfigureAwait(false)
            : await reader.ReadInventoryAsync(token).ConfigureAwait(false);
        if (query.Origin != DebloatInventoryOrigin.NativeAppx || !query.IsCurrentUserScoped)
            throw new InvalidOperationException("Observação sem origem WinRT ou com escopo divergente.");
        var observation = scope == DebloatScope.CurrentUser
            ? new DebloatInventoryScopeResult(scope, query.State, query.Completeness, query.Packages, query.Errors)
            : query.AdditionalScopes.Single(s => s.Scope == scope);
        if (observation.Packages.Count > DebloatNativeLimits.MaximumPackages ||
            observation.Packages.Any(p => p.Scope != scope || !DebloatNativeIdentity.HasConsistentMetadata(p) ||
                (scope == DebloatScope.CurrentUser ? p.UserSecurityId != query.UserSecurityId : p.UserSecurityId.Length != 0)))
            throw new InvalidOperationException("Metadados divergentes no escopo observado.");
        return observation;
    }

    private async Task<(DebloatRemovalPostObservation Post, bool Unchanged)> VerifyAsync(DebloatScope scope,
        DebloatRemovalRequest request, string expectedRevision)
    {
        try
        {
            var after = await ReadScopeAsync(scope, CancellationToken.None).ConfigureAwait(false);
            var absent = after.Complete && after.Packages.All(p =>
                !string.Equals(p.PackageFamilyName, request.Package.PackageFamilyName, StringComparison.OrdinalIgnoreCase));
            var revision = after.Complete ? DebloatRemovalHashing.InventoryRevision(after.Packages) : null;
            var unchanged = revision == request.InventoryRevision;
            var matches = absent && revision == expectedRevision;
            return (new(after.Complete, absent, matches, matches
                ? "Ausência e demais identidades do escopo verificadas na consulta posterior."
                : unchanged ? "Nenhuma mudança observada no escopo consultado."
                    : "O estado ou os efeitos no escopo não puderam ser confirmados."), unchanged);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return (new(false, false, false, $"Pós-verificação indisponível (0x{error.HResult:X8})."), false);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrEmpty(value) ? null :
        new string(value.Where(c => !char.IsControl(c)).Take(512).ToArray());
}
