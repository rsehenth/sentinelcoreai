namespace SentinelCore.Domain;

// Sem símbolo, configuração ou argumento de ativação. Exige nova decisão de distribuição.
public static class AppsOperationalExecutionGate
{
    public static bool PermitsExecution => false;
}

public enum AppsOperationKind { StartupDisable, StartupEnable, MsiUninstall, MsixUninstall }

/// <summary>Somente metadados redigidos. Identidades completas de reversão ficam cifradas.</summary>
public sealed record AppsOperationIntent(Guid Id, int ContractVersion, AppsOperationKind Kind,
    AppsDataOrigin Origin, string TargetKey, AppsScope Scope, string Source, DateTimeOffset AtUtc,
    string StateToken, string BeforeState, string ConfirmationHash, string? ProtectedStartupState,
    Guid? ReversesOperationId, string Provenance)
{
    // GUID MSI ou PackageFullName validado; nunca comando, localização ou argumentos.
    public string? ApplicationIdentity { get; init; }
}

public sealed record AppsOperationCompletion(Guid Id, DateTimeOffset CompletedUtc, AppsOperationOutcome Outcome,
    string Code, int? NativeCode = null, bool RestartRequired = false);

public sealed record AppsOperationRecord(AppsOperationIntent Intent, DateTimeOffset? StartedUtc,
    AppsOperationCompletion? Completion)
{
    public AppsOperationOutcome Outcome => Completion?.Outcome ?? AppsOperationOutcome.Indeterminate;
}

public sealed record AppsOperationHistory(IReadOnlyList<AppsOperationRecord> Records, IReadOnlyList<string> Warnings);

public interface IAppsSecretProtector
{
    bool IsSynthetic { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public interface IAppsOperationStore
{
    // Retido até a releitura final; store.lock continua protegendo cada escrita imutável.
    IDisposable AcquireAppsOperationLease();
    Task<AppsOperationHistory> ReadAppsOperationsAsync(CancellationToken cancellationToken);
    Task SaveAppsIntentAsync(AppsOperationIntent intent, CancellationToken cancellationToken);
    Task SaveAppsStartedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken);
    Task SaveAppsCompletionAsync(AppsOperationCompletion completion, CancellationToken cancellationToken);
    string ProtectStartupState(StartupEntry entry);
    StartupEntry UnprotectStartupState(string ciphertext, AppsDataOrigin origin);
}

/// <summary>O executor persiste intenção e resultado na própria fronteira.</summary>
public interface IJournaledAppsExecutor { }

public sealed record RunValueState(bool Exists, string? Content, string? ValueKind, bool Eligible);

public interface IRunStartupAccess
{
    bool IsSynthetic { get; }
    RunValueState Read(StartupRegistryIdentity identity);
    // Implementações devem conferir o estado esperado imediatamente antes de escrever.
    bool DeleteIfUnchanged(StartupRegistryIdentity identity, RunValueState expected);
    bool RestoreIfAbsent(StartupRegistryIdentity identity, string content);
}

public sealed record MsiInstallation(string ProductCode, AppsScope Scope, string Context, string Name,
    string Version, string Publisher, bool Installed, bool Eligible, bool Unambiguous);

public interface IMsiUninstallApi
{
    bool IsSynthetic { get; }
    MsiInstallation? Inspect(string productCode);
    uint Uninstall(string productCode);
}

public sealed record MsixRemovalIdentity(string FullName, string FamilyName, string Name, string Version,
    bool CurrentUser, bool Available, bool Framework, bool Dependency, bool Protected, bool IdentityKnown);

public sealed record MsixRemovalReceipt(bool Completed, int ErrorCode);

public interface IMsixUninstallApi
{
    bool IsSynthetic { get; }
    MsixRemovalIdentity? Inspect(string packageFullName);
    Task<MsixRemovalReceipt> RemoveAsync(string packageFullName);
}

public static class AppsOperationIdentity
{
    public static string Hash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static string Target(string stableId) => Hash(stableId.ToUpperInvariant());
    public static bool ValidPackageFullName(string? value) => value is { Length: <= 512 } &&
        System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^[A-Za-z0-9][A-Za-z0-9.-]{2,49}_[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}_(x64|x86|arm|arm64|neutral)_[A-Za-z0-9.-]{0,30}_[a-z0-9]{1,13}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static bool ValidRunIdentity(StartupEntry entry) => entry.RegistryIdentity is { } id &&
        entry.Source == StartupSourceKind.RegistryRun && entry.Scope == AppsScope.CurrentUser &&
        id.Hive == "CurrentUser" && id.Path == @"Software\Microsoft\Windows\CurrentVersion\Run" &&
        id.View == "Registry64" && id.ValueKind is "String" or "ExpandString" &&
        !string.IsNullOrWhiteSpace(id.ValueName) && id.ValueName.Length <= 16383 && !id.ValueName.Contains('\0') &&
        entry.StableId == AppsIdentity.Startup(entry.Source, entry.Scope, $"{id.Hive}|{id.Path}|{id.View}|{id.ValueName}");
}
