using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SentinelCore.Domain;

public enum DebloatRemovalPhase { Initial, Started, ProgressReported, Completed, Failed, Indeterminate }
public enum DebloatRemovalState { Blocked, CancelledBeforeStart, Failed, PartialFailure, Interrupted, Indeterminate, VerificationFailed, VerifiedSynthetic }
public enum DebloatRemovalScenario { Success, AccessDenied, PartialFailure, Interrupted, Indeterminate, Unverifiable, PostVerificationFailed }

/// <summary>Exact identity. No friendly name, path, command, wildcard or alternate user scope.</summary>
public sealed record DebloatRemovalIdentity(string TechnicalName, string PackageFullName,
    string PackageFamilyName, string Publisher, string PublisherId, string Version,
    string Architecture, string UserSecurityId);

public sealed record DebloatRemovalDependencyEvidence(
    DebloatObservationCompleteness Completeness,
    IReadOnlyList<DebloatRemovalIdentity> Direct,
    IReadOnlyList<DebloatRemovalIdentity> Reverse,
    IReadOnlyList<DebloatRemovalIdentity> Shared,
    IReadOnlyList<DebloatRemovalIdentity> PotentiallyAffected,
    IReadOnlyList<string> ApplicationsMayClose,
    bool EffectsKnown,
    IReadOnlyList<string> UnknownConsequences);

/// <summary>Metadata only; no application content or data backup is represented.</summary>
public sealed record DebloatRemovalObservation(
    bool Synthetic, bool IsWindows11, int WindowsBuild, bool ExecutorAvailable,
    string CurrentUserSecurityId, string CatalogId, string CatalogVersion,
    string CatalogHash, string InventoryRevision,
    DebloatInventoryQueryState QueryState, DebloatObservationCompleteness Completeness,
    IReadOnlyList<DebloatNativePackageMetadata> Packages,
    DebloatRemovalIdentity SelectedPackage,
    bool? Protected, bool? SystemPackage,
    DebloatRemovalDependencyEvidence Dependencies,
    bool ReinstallationDocumented, string ReinstallInformation,
    bool DataLossAcknowledged, IReadOnlyList<string> Consequences);

public sealed record DebloatRemovalPreflight(bool CanSimulate, bool CanExecuteReal,
    RiskLevel RealRisk, IReadOnlyList<string> Blockers, IReadOnlyList<string> RealBlockers);

public sealed record DebloatRemovalSnapshot(int SchemaVersion, DebloatRemovalObservation Observation,
    DebloatCatalogEntry CatalogEntry, DebloatRemovalScenario Scenario, bool MediumReviewConfirmed,
    bool Removed);

public sealed record DebloatRemovalPlan(Guid Id, string OperationId, DebloatRemovalSnapshot Snapshot,
    DebloatRemovalPreflight Preflight, SimulationResult Simulation, string Hash, string Confirmation);

public sealed record DebloatRemovalProgress(Guid OperationId, DebloatRemovalPhase Phase,
    uint? ReportedPercentage, string Information);

public sealed record DebloatRemovalRequest(Guid OperationId, DebloatRemovalIdentity Package,
    string PlanHash, string InventoryRevision, bool Synthetic,
    DebloatRemovalScenario Scenario = DebloatRemovalScenario.Success)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DebloatScope? Scope { get; init; }
}

/// <summary>API completion alone does not verify removal or payload/data deletion.</summary>
public sealed record DebloatRemovalReceipt(Guid OperationId, DebloatRemovalIdentity Package,
    bool Synthetic, DebloatRemovalPhase Phase, bool EffectMayHaveOccurred,
    bool PartialFailure, int? HResult, int? ExtendedHResult, string? ErrorText,
    Guid? ActivityId, IReadOnlyList<DebloatRemovalProgress> Progress,
    IReadOnlyList<string> Limitations, DebloatRemovalPostObservation? PostVerification = null,
    bool CancelledBeforeStart = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DebloatScope? Scope { get; init; }
}

public sealed record DebloatRemovalPostObservation(bool Complete, bool MainPackageAbsent,
    bool EffectsMatchPlan, string Information);

public sealed record DebloatRemovalResult(DebloatRemovalState State, bool Succeeded,
    bool RequiresIntervention, DebloatRemovalReceipt? Receipt,
    DebloatRemovalPostObservation? PostVerification, TransactionExecutionResult Transaction,
    IReadOnlyList<string> Limitations);

public sealed record DebloatRemovalHistoryItem(Guid Id, DebloatRemovalSnapshot Snapshot,
    TransactionRecord Transaction, DebloatRemovalReceipt? Receipt, string ReportPath);
public sealed record DebloatRemovalHistory(IReadOnlyList<DebloatRemovalHistoryItem> Items,
    IReadOnlyList<string> Warnings);

public interface IDebloatRemovalObservationSource
{
    bool SyntheticOnly { get; }
    Task<DebloatRemovalObservation> ObserveAsync(CancellationToken cancellationToken = default);
    Task<DebloatRemovalPostObservation> VerifyAsync(DebloatRemovalRequest request,
        CancellationToken cancellationToken = default);
}

public interface IDebloatRemovalExecutor
{
    bool SyntheticOnly { get; }
    bool Available { get; }
    // Cancellation is checked only before initiating the removal. It must not cancel an initiated request.
    Task<DebloatRemovalReceipt> ExecuteAsync(DebloatRemovalRequest request,
        IProgress<DebloatRemovalProgress>? progress = null, CancellationToken cancellationToken = default);
}

public static class DebloatRemovalBuildGate
{
    // Intentionally not controlled by MSBuild, configuration, environment or arguments.
    public static bool AllowsRealExecution(IEnumerable<string>? arguments = null) => false;
}

public static partial class DebloatRemovalIdentityPolicy
{
    public static DebloatRemovalIdentity From(DebloatNativePackageMetadata p) => new(p.TechnicalName,
        p.PackageFullName, p.PackageFamilyName, p.Publisher, p.PublisherId, p.Version, p.Architecture, p.UserSecurityId);

    public static bool IsValid(DebloatRemovalIdentity? p) =>
        p is not null && HasExactIdentity(p) && DebloatNativeIdentity.Safe(p.UserSecurityId) &&
        DebloatNativeIdentity.UserSidPattern().IsMatch(p.UserSecurityId);

    public static bool HasExactIdentity(DebloatRemovalIdentity p)
    {
        if (!new[] { p.TechnicalName, p.PackageFullName, p.PackageFamilyName, p.Publisher,
                p.PublisherId, p.Version, p.Architecture }.All(DebloatNativeIdentity.Safe)) return false;
        var parts = p.PackageFullName.Split('_');
        return parts.Length == 5 && DebloatNativeIdentity.TechnicalNamePattern().IsMatch(p.TechnicalName) &&
            DebloatNativeIdentity.PublisherIdPattern().IsMatch(p.PublisherId) &&
            p.Architecture is "x64" or "x86" or "arm64" or "arm" or "neutral" &&
            VersionPattern().IsMatch(p.Version) && p.Version.Split('.').All(s => ushort.TryParse(s, out _)) &&
            parts[0] == p.TechnicalName && parts[1] == p.Version && parts[2] == p.Architecture &&
            ResourceIdPattern().IsMatch(parts[3]) && parts[4] == p.PublisherId &&
            p.PackageFamilyName == p.TechnicalName + "_" + p.PublisherId;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^[A-Za-z0-9.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIdPattern();
}

public static class DebloatRemovalHashing
{
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static string PlanHash(DebloatRemovalPlan p) => Hash(new
        { p.Id, p.OperationId, p.Snapshot, p.Preflight, p.Simulation });
    public static string InventoryRevision(IEnumerable<DebloatNativePackageMetadata> packages) => Hash(packages
        .OrderBy(p => p.PackageFullName, StringComparer.Ordinal).Select(p => new
        {
            Identity = DebloatRemovalIdentityPolicy.From(p), p.Scope, p.Kind, p.InstallState, p.Signature,
            p.IsFramework, p.IsResourcePackage, p.IsMainPackage, p.IsOptional, p.IsBundle,
            Dependencies = p.DependencyFullNames?.OrderBy(name => name, StringComparer.Ordinal)
        }));
}

public static class DebloatRemovalMessages
{
    public const string NativeBlocked = "T3C — motor nativo compilável; execução real bloqueada e não validada.";
    public const string IndirectEffects = "A remoção pode afetar dependências e encerrar aplicativos associados; não se promete efeito limitado ao pacote principal.";
    public const string Cancellation = "Antes do início, o plano pode ser abandonado. Depois do início, a solicitação não pode ser cancelada; acompanhar até resultado verificável ou indeterminado.";
    public static readonly IReadOnlyList<string> Limitations = Array.AsReadOnly(new[]
    {
        DebloatLimits.SyntheticWarning, NativeBlocked, IndirectEffects, Cancellation,
        DebloatLimits.RecoveryWarning, "Snapshot de metadados não recupera arquivos ou dados pessoais. Nenhuma reinstalação ou restauração automática disponível."
    });
}
