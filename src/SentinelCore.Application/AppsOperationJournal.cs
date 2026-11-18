using SentinelCore.Domain;

namespace SentinelCore.Application;

public static class AppsOperationConfirmation
{
    public static string Startup(StartupEntry entry, StartupAction action) =>
        $"CONFIRMAR INICIALIZACAO {(action == StartupAction.Disable ? "DESATIVAR" : "REATIVAR")} {AppsStartupHashing.Short($"{entry.StableId}|{action}|{entry.StateToken}")}";
    public static string Uninstall(InstalledApplicationEntry entry, UninstallMethod method) =>
        $"CONFIRMAR DESINSTALACAO {AppsStartupHashing.Short($"{entry.StableId}|{method}|{entry.StateToken}")}";
}

internal sealed record AppsExecutionResult(AppsOperationOutcome Outcome, string Code, int? NativeCode = null, bool RestartRequired = false);

internal static class AppsOperationJournal
{
    internal static AppsOperationIntent Intent(AppsOperationKind kind, AppsDataOrigin origin, string stableId,
        AppsScope scope, string source, string token, string before, string confirmation, TimeProvider time,
        string? protectedState = null, Guid? reverses = null, string? applicationIdentity = null) =>
        new(Guid.NewGuid(), 1, kind, origin, AppsOperationIdentity.Target(stableId), scope, source, time.GetUtcNow(),
            token, before, AppsOperationIdentity.Hash(confirmation), protectedState, reverses, "SentinelCore.AppsStartup/v1")
            { ApplicationIdentity = applicationIdentity };

    // Caller holds the operation lease through verification and final journal persistence.
    internal static async Task<AppsExecutionResult> ExecuteAsync(IAppsOperationStore store, AppsOperationIntent intent,
        Func<Task<AppsExecutionResult>> execute, TimeProvider time, CancellationToken cancellationToken)
    {
        var started = false;
        try
        {
            await store.SaveAppsIntentAsync(intent, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await store.SaveAppsStartedAsync(intent.Id, time.GetUtcNow(), cancellationToken);
            started = true;
            // Cancellation after the durable start never hides a possibly completed native effect.
            var result = await execute();
            await store.SaveAppsCompletionAsync(new(intent.Id, time.GetUtcNow(), result.Outcome, result.Code,
                result.NativeCode, result.RestartRequired), CancellationToken.None);
            return result;
        }
        catch (Exception)
        {
            var outcome = started ? AppsOperationOutcome.Indeterminate : AppsOperationOutcome.Blocked;
            var code = started ? "execution-or-persistence-indeterminate" : "intent-not-durable";
            try
            {
                await store.SaveAppsCompletionAsync(new(intent.Id, time.GetUtcNow(), outcome, code), CancellationToken.None);
            }
            catch (Exception) { /* An intent without a final record remains indeterminate on reopening. */ }
            return new(outcome, code);
        }
    }

    internal static bool Unresolved(AppsOperationHistory history, string stableId, AppsDataOrigin origin) =>
        history.Warnings.Count != 0 || history.Records.Any(r => r.Intent.TargetKey == AppsOperationIdentity.Target(stableId) &&
            r.Intent.Origin == origin && r.Outcome == AppsOperationOutcome.Indeterminate);
}
