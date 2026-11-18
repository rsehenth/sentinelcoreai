namespace SentinelCore.Domain;

// Módulo "Aplicativos e Inicialização". Contratos tipados; execução operacional desabilitada e não composta no App.

/// <summary>Origem do dado. Um executor sintético só age sobre itens sintéticos, e vice-versa.</summary>
public enum AppsDataOrigin { Synthetic, Operational }

public enum StartupSourceKind
{
    RegistryRun, StartupFolder, PackagedStartupTask, ScheduledTask,
    // Somente leitura e não tratados como aplicativos comuns.
    RegistryRunOnce, Service, Driver, SecurityComponent, WindowsInternalTask, Other
}

public enum AppsScope { CurrentUser, Machine }

public enum StartupConfiguredState { Enabled, Disabled, Unknown }

/// <summary>Configurado, em execução e estado operacional não verificado são eixos distintos.</summary>
public enum StartupRuntimeState { NotVerified, NotRunning, Running }

public enum ChangeEligibility { Eligible, ReadOnly, Unavailable }

public enum ChangeBlockReason
{
    None,
    NotAnApplication,
    ProtectedEntry,
    ConservativeScheduledTask,
    NoReversibleMechanism,
    NoReliableUninstallMechanism,
    SystemOrFramework,
    AllUsersNotSupported,
    IdentityIncomplete,
    UnknownState
}

public sealed record StartupEntry(
    string StableId, string Name, string? Publisher, StartupSourceKind Source, string Reference,
    AppsScope Scope, StartupConfiguredState State, StartupRuntimeState Runtime, string EntryType,
    AppsDataOrigin Origin)
{
    public StartupRegistryIdentity? RegistryIdentity { get; init; }
    public bool? ReferenceReadable { get; init; }
    /// <summary>Fingerprint do estado observado; muda se estado ou referência mudarem (detecção de concorrência).</summary>
    public string StateToken => AppsStartupHashing.Short($"{StableId}|{State}|{Reference}|{RegistryIdentity}");
}

public sealed record StartupRegistryIdentity(string Hive, string Path, string View, string ValueName, string ValueKind);

public sealed record StartupEligibility(ChangeEligibility Eligibility, ChangeBlockReason Reason, bool RequiresPrivilege);

public enum StartupAction { Disable, Enable }

public enum InstalledApplicationKind { Win32Registry, Msi, Msix }

public enum UninstallMethod { None, MsiProductCode, MsixPackageFullName }

public sealed record InstalledApplicationEntry(
    string StableId, string Name, string? Publisher, string? Version, InstalledApplicationKind Kind,
    string RegistrySource, AppsScope Scope, string? Location, string? ProductCode, string? PackageFullName,
    bool IsSystemOrFramework, AppsDataOrigin Origin)
{
    public string? PackageFamilyName { get; init; }
    public string? PackageType { get; init; }
    public bool? PackageAvailable { get; init; }
    public string? PackageClassification { get; init; }
    public string StateToken => AppsStartupHashing.Short($"{StableId}|{Version}|{Location}|{ProductCode}|{PackageFullName}|{PackageFamilyName}|{IsSystemOrFramework}|{PackageAvailable}|{PackageClassification}|{Scope}|{RegistrySource}");
}

public sealed record UninstallEligibility(ChangeEligibility Eligibility, ChangeBlockReason Reason,
    UninstallMethod Method, bool RequiresPrivilege);

public enum AppsOperationOutcome { Succeeded, Failed, Indeterminate, Conflict, Blocked, GateClosed }

/// <summary>Uma entrada por operação. Não existe contrato com coleção de alvos.</summary>
public sealed record StartupChangeRequest(StartupEntry Entry, StartupAction Action, string ExpectedStateToken)
{
    public string? Confirmation { get; init; }
}

public sealed record StartupChangeResult(AppsOperationOutcome Outcome, string Detail, string? OriginalDataPreserved);

public sealed record UninstallRequest(InstalledApplicationEntry Entry, UninstallMethod Method, string ExpectedStateToken)
{
    public string? Confirmation { get; init; }
}

public sealed record UninstallResult(AppsOperationOutcome Outcome, string Detail)
{
    public int? NativeCode { get; init; }
    public bool RestartRequired { get; init; }
}

public sealed record StartupInventoryResult(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings, bool Complete);

public sealed record InstalledApplicationInventoryResult(IReadOnlyList<InstalledApplicationEntry> Entries,
    IReadOnlyList<string> Warnings, bool Complete);

public interface IStartupInventoryProvider
{
    Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken);
}

public interface IInstalledApplicationProvider
{
    Task<InstalledApplicationInventoryResult> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Altera a inicialização de UMA entrada. Nunca encerra processos; preserva os dados originais.</summary>
public interface IStartupChangeExecutor
{
    bool IsSynthetic { get; }
    Task<StartupChangeResult> ExecuteAsync(StartupChangeRequest request, CancellationToken cancellationToken);
}

/// <summary>Desinstala UM aplicativo por mecanismo tipado. Nunca executa comando obtido de UninstallString.</summary>
public interface IApplicationUninstallExecutor
{
    bool IsSynthetic { get; }
    Task<UninstallResult> ExecuteAsync(UninstallRequest request, CancellationToken cancellationToken);
}

public static class AppsIdentity
{
    /// <summary>Identificador estável: tipo, escopo e chave exata da origem (nunca apenas o nome).</summary>
    public static string Startup(StartupSourceKind source, AppsScope scope, string key) =>
        $"startup|{source}|{scope}|{key.Trim().ToLowerInvariant()}";

    public static string Application(InstalledApplicationKind kind, AppsScope scope, string key) =>
        $"app|{kind}|{scope}|{key.Trim().ToLowerInvariant()}";
}

public static class AppsStartupHashing
{
    public static string Short(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];
}
