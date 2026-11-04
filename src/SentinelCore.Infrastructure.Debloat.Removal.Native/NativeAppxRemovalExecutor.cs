using System.Security.Principal;
using SentinelCore.Domain;
using Windows.Management.Deployment;

namespace SentinelCore.Infrastructure.Debloat.Removal.Native;

/// <summary>
/// Compilable, unregistered WinRT adapter. Both entry and native call fail closed unconditionally in T3C.
/// No inventory capability, removal options, other-user SID, provisioning, process control or shell.
/// </summary>
public sealed class NativeAppxRemovalExecutor : IDebloatRemovalExecutor
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
        if (request.Synthetic || !DebloatRemovalIdentityPolicy.IsValid(request.Package) ||
            request.OperationId == Guid.Empty || request.PlanHash.Length != 64 ||
            !request.PlanHash.All(Uri.IsHexDigit) || !DebloatNativeIdentity.Safe(request.InventoryRevision) ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new InvalidOperationException("Requisição nativa inválida.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != request.Package.UserSecurityId)
            throw new InvalidOperationException("Somente o usuário do token atual pode ser alvo.");
        return InvokeBlockedNativeAsync(request, progress);
    }

    private static async Task<DebloatRemovalReceipt> InvokeBlockedNativeAsync(DebloatRemovalRequest request,
        IProgress<DebloatRemovalProgress>? progress)
    {
        // Defense in depth: even direct invocation of this private method cannot reach PackageManager in T3C.
        if (!DebloatRemovalBuildGate.AllowsRealExecution())
            throw new InvalidOperationException(DebloatRemovalMessages.NativeBlocked);
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
            // Exact full name, current user, no expanded RemovalOptions. Never invoked in this stage.
            var operation = manager.RemovePackageAsync(request.Package.PackageFullName);
            Report(DebloatRemovalPhase.Started, null, "Solicitação iniciada; não cancelável.");
            operation.Progress = (_, update) => Report(DebloatRemovalPhase.ProgressReported,
                update.percentage <= 100 ? update.percentage : null, update.state.ToString());
            // No CancellationToken, Cancel(), WaitAsync or process termination after initiation.
            var result = await operation.AsTask().ConfigureAwait(false);
            var extended = result.ExtendedErrorCode?.HResult;
            var text = Clean(result.ErrorText);
            var phase = extended is null or 0 && string.IsNullOrEmpty(text)
                ? DebloatRemovalPhase.Completed : DebloatRemovalPhase.Indeterminate;
            Report(phase, null, "Resultado da API; pós-verificação independente obrigatória.");
            DebloatRemovalProgress[] copy;
            lock (updates) copy = updates.ToArray();
            return new(request.OperationId, request.Package, false, phase, true, false,
                operation.ErrorCode?.HResult ?? 0, extended, text, result.ActivityId,
                copy, DebloatRemovalMessages.Limitations);
        }
        catch (Exception error)
        {
            Report(DebloatRemovalPhase.Indeterminate, null, "Falha após possível início; intervenção necessária.");
            DebloatRemovalProgress[] copy;
            lock (updates) copy = updates.ToArray();
            return new(request.OperationId, request.Package, false, DebloatRemovalPhase.Indeterminate,
                true, false, error.HResult, null, Clean(error.Message), null, copy,
                DebloatRemovalMessages.Limitations);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrEmpty(value) ? null :
        new string(value.Where(c => !char.IsControl(c)).Take(512).ToArray());
}
