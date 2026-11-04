namespace SentinelCore.Domain;

public enum DebloatPackageKind { Unknown, Appx, Framework, Resource, Win32 }
public enum DebloatScope { Unknown, CurrentUser, OtherUser, Provisioned, AllUsers }
public enum DebloatInstallState { Unknown, Installed, NotFound, RemovedSynthetic }
public enum DebloatDependencyStatus { NotInvestigated, Unknown, KnownNone, Known }

/// <summary>
/// Explicit separation between documentary catalog presence and an actual inventory observation.
/// A catalog entry without observation is never reported as absent.
/// </summary>
public enum DebloatPresence
{
    NotVerified,
    PresentInSyntheticInventory,
    AbsentInConsultedInventory,
    IdentityDivergent,
    Indeterminate
}

public enum DebloatEligibility
{
    SimulationAvailable,
    UnavailableForSimulation,
    ProtectedOrBlocked
}
public enum DebloatScenario
{
    Success, NotFound, AccessDenied, Protected, DependencyDetected, IdentityMismatch,
    UpdatedBeforeExecution, InUse, PartialFailure, Interrupted, Cancelled,
    Indeterminate, PersistenceUnavailable, Unverifiable
}
public enum DebloatExecutionMode { Synthetic, Real }

public sealed record DebloatDependency(string PackageFamilyName, bool Critical, bool RequiredByOtherPackage);

/// <summary>Metadata only. No application data, credentials, documents or installed-package queries.</summary>
public sealed record DebloatPackage(
    string CatalogId,
    string FriendlyName,
    string TechnicalName,
    string PackageFamilyName,
    string PackageFullName,
    string Publisher,
    string Version,
    string Architecture,
    string UserId,
    DebloatPackageKind Kind,
    DebloatScope Scope,
    DebloatInstallState State,
    bool? Provisioned,
    bool Protected,
    bool SystemPackage,
    DebloatDependencyStatus DependencyStatus,
    IReadOnlyList<DebloatDependency> Dependencies,
    bool Synthetic,
    string Source,
    IReadOnlyList<string> Limitations);

public sealed record DebloatCatalogEntry(
    string Id, string TechnicalName, string FriendlyName, string Category,
    string RemovalMethod, RiskLevel PreliminaryRisk, RiskLevel RealRisk,
    string Consequence, string ReinstallInformation, string DataLossWarning,
    string UpstreamSource, string UpstreamCommit);

public sealed record DebloatAssessment(
    DebloatPackage Package, DebloatCatalogEntry? CatalogEntry, RiskLevel EffectiveRisk,
    DebloatPresence Presence, DebloatEligibility Eligibility,
    bool CanSimulate, bool CanExecuteReal, IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Limitations);

public sealed record DebloatPreparedPlan(
    Guid Id, DebloatPackage Package, DebloatCatalogEntry CatalogEntry,
    RiskLevel Risk, DebloatScenario Scenario, bool MediumReviewConfirmed,
    SimulationResult Simulation, string PlanHash, string Confirmation,
    IReadOnlyList<string> Consequences, IReadOnlyList<string> Limitations);

public sealed record DebloatExecutionResult(
    bool Synthetic, bool Succeeded, TransactionExecutionResult Transaction,
    string Summary, DebloatPackage? CurrentPackage);

public sealed record DebloatHistoryItem(Guid Id, string Name, string PackageFullName,
    string Publisher, string PreviousVersion, string UserId, string State,
    bool Succeeded, bool Indeterminate, DateTimeOffset AtUtc, string ReinstallInformation,
    string Limitations, string ReportPath);

public sealed record DebloatHistorySnapshot(IReadOnlyList<DebloatHistoryItem> Items, IReadOnlyList<string> Warnings);

public interface IDebloatInventory
{
    bool SyntheticOnly { get; }
    string CurrentUserId { get; }
    Task<IReadOnlyList<DebloatPackage>> ReadAsync(CancellationToken cancellationToken = default);
}

public static class DebloatFixtureIdentity
{
    public const string Publisher = "CN=SentinelCore Synthetic Fixture";
    public const string User = "S-1-5-21-100-200-300-1001";
}

public sealed record DebloatTargetSnapshot(DebloatPackage Package, DebloatCatalogEntry CatalogEntry,
    DebloatScenario Scenario, bool MediumReviewConfirmed);

public interface IDebloatSyntheticRuntime : ITargetStateReader, IOperationExecutor
{
    void AdvanceFixtureBeforeExecution();
}

public interface IDebloatRuntimeFactory
{
    bool SyntheticOnly { get; }
    IDebloatSyntheticRuntime Create(DebloatPackage package, DebloatCatalogEntry entry, DebloatScenario scenario,
        bool mediumReviewConfirmed, string operationId, OperationTarget target, TimeProvider time);
}

public static class DebloatLimits
{
    public const int MaximumInventory = 512;
    public const int MaximumText = 512;
    public const string SyntheticWarning = "SIMULAÇÃO — NENHUM APLICATIVO REAL SERÁ REMOVIDO.";
    public const string RecoveryWarning = "Reinstalação não equivale a rollback. Dados, preferências, licenças, contas e integrações não possuem recuperação garantida.";
}
