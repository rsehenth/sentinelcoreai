using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed partial class MemoryStore : IAppsOperationStore
{
    private readonly IAppsSecretProtector? appsProtector;
    public MemoryStore(string dataRoot, IAppsSecretProtector protector) : this(dataRoot) => appsProtector = protector;
    private string AppsDirectory => Path.Combine(root, "memory", "apps-startup");
    private string AppsPath(Guid id, string file) => Path.Combine(AppsDirectory, id.ToString("D"), file + ".md");

    private void PrepareAppsPaths()
    {
        CheckRecoveryPath(AppsDirectory);
        CheckRecoveryPath(Path.Combine(root, "data", "store.lock"));
        Directory.CreateDirectory(AppsDirectory);
        Directory.CreateDirectory(Path.Combine(root, "data"));
    }

    public IDisposable AcquireAppsOperationLease()
    {
        PrepareAppsPaths();
        var path = Path.Combine(root, "data", "apps-operation.lock");
        CheckRecoveryPath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public string ProtectStartupState(StartupEntry entry)
    {
        if (appsProtector is null || appsProtector.IsSynthetic != (entry.Origin == AppsDataOrigin.Synthetic))
            throw new InvalidOperationException("startup-protection-unavailable");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entry);
        try { return Convert.ToBase64String(appsProtector.Protect(plaintext)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public StartupEntry UnprotectStartupState(string ciphertext, AppsDataOrigin origin)
    {
        if (appsProtector is null || appsProtector.IsSynthetic != (origin == AppsDataOrigin.Synthetic))
            throw new InvalidOperationException("startup-protection-unavailable");
        var plaintext = appsProtector.Unprotect(Convert.FromBase64String(ciphertext));
        try
        {
            var entry = JsonSerializer.Deserialize<StartupEntry>(plaintext) ?? throw new InvalidDataException("startup-state-invalid");
            if (entry.Origin != origin || !AppsOperationIdentity.ValidRunIdentity(entry))
                throw new InvalidDataException("startup-state-invalid");
            return entry;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task<AppsOperationHistory> ReadAppsOperationsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        PrepareAppsPaths();
        using var storeLock = Lock();
        return ReadAppsHistory(cancellationToken);
    }, cancellationToken);

    private AppsOperationHistory ReadAppsHistory(CancellationToken cancellationToken)
    {
        var records = new List<AppsOperationRecord>();
        var warnings = new List<string>();
        foreach (var directory in Directory.GetDirectories(AppsDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CheckRecoveryPath(directory);
                if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var id))
                    throw new InvalidDataException();
                var intent = ReadAppsDocument<AppsOperationIntent>(AppsPath(id, "intent"));
                ValidateAppsIntent(intent);
                if (intent.Id != id) throw new InvalidDataException();
                var startedPath = AppsPath(id, "started");
                var resultPath = AppsPath(id, "result");
                CheckRecoveryPath(startedPath);
                CheckRecoveryPath(resultPath);
                var started = File.Exists(startedPath) ? ReadAppsDocument<AppsStarted>(startedPath) : null;
                if (started is not null && (started.Id != id || started.AtUtc.Offset != TimeSpan.Zero || started.AtUtc < intent.AtUtc))
                    throw new InvalidDataException();
                var completion = File.Exists(resultPath) ? ReadAppsDocument<AppsOperationCompletion>(resultPath) : null;
                if (completion is not null) ValidateAppsCompletion(intent, started?.AtUtc, completion);
                records.Add(new(intent, started?.AtUtc, completion));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { warnings.Add("apps-history:invalid-record-preserved"); }
        }
        return new(records.OrderBy(r => r.Intent.AtUtc).ThenBy(r => r.Intent.Id).ToArray(), warnings);
    }

    public Task SaveAppsIntentAsync(AppsOperationIntent intent, CancellationToken cancellationToken) => Task.Run(() =>
    {
        ValidateAppsIntent(intent);
        PrepareAppsPaths();
        using var storeLock = Lock();
        var path = AppsPath(intent.Id, "intent");
        CheckRecoveryPath(path);
        if (File.Exists(path)) { WriteAppsDocument(path, intent); return; }
        var history = ReadAppsHistory(cancellationToken);
        if (history.Warnings.Count != 0 || history.Records.Any(r => (r.Intent.TargetKey == intent.TargetKey ||
                intent.ApplicationIdentity is not null && r.Intent.Scope == intent.Scope &&
                string.Equals(r.Intent.ApplicationIdentity, intent.ApplicationIdentity, StringComparison.OrdinalIgnoreCase)) &&
                r.Intent.Origin == intent.Origin && r.Outcome == AppsOperationOutcome.Indeterminate))
            throw new InvalidDataException("apps-history:reconciliation-required");
        if (intent.ProtectedStartupState is { } encrypted)
        {
            var original = UnprotectStartupState(encrypted, intent.Origin);
            if (AppsOperationIdentity.Target(original.StableId) != intent.TargetKey || original.Scope != intent.Scope ||
                original.Source.ToString() != intent.Source || original.State != StartupConfiguredState.Enabled)
                throw new InvalidDataException("apps-history:restoration-state-mismatch");
        }
        if (intent.ReversesOperationId is { } reversal && !history.Records.Any(r => r.Intent.Id == reversal &&
                r.Intent.Kind == AppsOperationKind.StartupDisable && r.Intent.TargetKey == intent.TargetKey &&
                r.Intent.Origin == intent.Origin && r.Outcome == AppsOperationOutcome.Succeeded &&
                !history.Records.Any(e => e.Intent.ReversesOperationId == reversal && e.Outcome == AppsOperationOutcome.Succeeded)))
            throw new InvalidDataException("apps-history:invalid-reversal");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAppsDocument(path, intent);
    }, cancellationToken);

    public Task SaveAppsStartedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken) => Task.Run(() =>
    {
        PrepareAppsPaths();
        using var storeLock = Lock();
        var intent = ReadAppsDocument<AppsOperationIntent>(AppsPath(id, "intent"));
        ValidateAppsIntent(intent);
        if (intent.Id != id || atUtc.Offset != TimeSpan.Zero || atUtc < intent.AtUtc || File.Exists(AppsPath(id, "result")))
            throw new InvalidDataException("apps-history:invalid-start");
        WriteAppsDocument(AppsPath(id, "started"), new AppsStarted(id, atUtc));
    }, cancellationToken);

    public Task SaveAppsCompletionAsync(AppsOperationCompletion completion, CancellationToken cancellationToken) => Task.Run(() =>
    {
        PrepareAppsPaths();
        using var storeLock = Lock();
        var intent = ReadAppsDocument<AppsOperationIntent>(AppsPath(completion.Id, "intent"));
        ValidateAppsIntent(intent);
        var startedPath = AppsPath(completion.Id, "started");
        CheckRecoveryPath(startedPath);
        var started = File.Exists(startedPath) ? ReadAppsDocument<AppsStarted>(startedPath) : null;
        ValidateAppsCompletion(intent, started?.AtUtc, completion);
        WriteAppsDocument(AppsPath(completion.Id, "result"), completion);
    }, cancellationToken);

    private T ReadAppsDocument<T>(string path)
    {
        CheckRecoveryPath(path);
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes) throw new InvalidDataException();
        return JsonSerializer.Deserialize<T>(RecordEnvelope.Unwrap(File.ReadAllText(path, Encoding.UTF8)))
            ?? throw new InvalidDataException("apps-history:missing-payload");
    }

    private void WriteAppsDocument<T>(string path, T value)
    {
        CheckRecoveryPath(path);
        var json = JsonSerializer.Serialize(value);
        if (File.Exists(path))
        {
            if (JsonSerializer.Serialize(ReadAppsDocument<T>(path)) != json)
                throw new InvalidDataException("apps-history:duplicate-conflict");
            return;
        }
        var document = RecordEnvelope.Wrap("# SentinelCore AI — Aplicativos e Inicialização\n\nContrato v1; dados redigidos.\n", json);
        if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes) throw new InvalidDataException();
        RecordEnvelope.AtomicWrite(path, document, overwrite: false);
    }

    private static bool AppsHash(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigit);
    private static void ValidateAppsIntent(AppsOperationIntent intent)
    {
        if (intent.Id == Guid.Empty || intent.ContractVersion != 1 || !Enum.IsDefined(intent.Kind) ||
            !Enum.IsDefined(intent.Origin) || !Enum.IsDefined(intent.Scope) || intent.AtUtc.Offset != TimeSpan.Zero ||
            !AppsHash(intent.TargetKey, 64) || !AppsHash(intent.StateToken, 12) || !AppsHash(intent.ConfirmationHash, 64) ||
            intent.Provenance != "SentinelCore.AppsStartup/v1" ||
            intent.Source is not ("RegistryRun" or "StartupFolder" or "PackagedStartupTask" or "Msi" or "Msix") ||
            intent.BeforeState is not ("Enabled" or "Disabled" or "Installed") ||
            intent.ProtectedStartupState is { Length: > 1048576 } ||
            intent.ApplicationIdentity is not null && (intent.Kind == AppsOperationKind.MsiUninstall ?
                !Guid.TryParseExact(intent.ApplicationIdentity, "B", out _) : intent.Kind != AppsOperationKind.MsixUninstall ||
                !AppsOperationIdentity.ValidPackageFullName(intent.ApplicationIdentity)) ||
            intent.Origin == AppsDataOrigin.Operational && intent.Kind is AppsOperationKind.MsiUninstall or AppsOperationKind.MsixUninstall &&
                intent.ApplicationIdentity is null ||
            intent.ReversesOperationId is not null && intent.Kind != AppsOperationKind.StartupEnable ||
            intent.Origin == AppsDataOrigin.Operational && intent.Kind is AppsOperationKind.StartupDisable or AppsOperationKind.StartupEnable &&
                string.IsNullOrWhiteSpace(intent.ProtectedStartupState))
            throw new InvalidDataException("apps-history:invalid-contract");
    }

    private static void ValidateAppsCompletion(AppsOperationIntent intent, DateTimeOffset? started, AppsOperationCompletion result)
    {
        if (result.Id != intent.Id || result.CompletedUtc.Offset != TimeSpan.Zero ||
            result.CompletedUtc < (started ?? intent.AtUtc) || !Enum.IsDefined(result.Outcome) ||
            string.IsNullOrEmpty(result.Code) || result.Code.Length > 80 ||
            result.Code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            result.Outcome == AppsOperationOutcome.Succeeded && started is null)
            throw new InvalidDataException("apps-history:invalid-result");
    }

    private sealed record AppsStarted(Guid Id, DateTimeOffset AtUtc);
}
