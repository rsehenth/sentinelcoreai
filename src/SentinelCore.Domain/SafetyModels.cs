using System.Text.Json.Serialization;

namespace SentinelCore.Domain;

public enum RiskLevel { Low, Medium, High, NotValidated }
public enum OperationScope { CurrentUser, Machine, AllUsers, ServiceAccount }
public enum ChangeKind { SetValue, DeleteValue, DeleteKey }
public enum OperationAvailability { Enabled, Disabled, Blocked }

public enum BlockerCode
{
    None,
    UnsupportedWindows,
    PolicyConflict,
    RiskBlocked,
    NotValidated,
    OperationBlocked,
    ElevationRequired,
    BackupFailed,
    ProtectionUnavailable,
    StateChanged,
    AccessDenied,
    TargetOutsideCatalog,
    VerificationFailed,
    AuthorizationMissing,
    AuthorizationExpired,
    AuthorizationReused,
    PlanMismatch,
    DependencyMissing,
    Unrecoverable,
    TargetLocked,
    IndeterminateState,
    JournalUnavailable,
    ExecutionFailed
}

public sealed record OperationTarget(string Root, string Path, string? ValueName)
{
    [JsonIgnore]
    public string Identity => ValueName is null ? $"{Root}\\{Path}" : $"{Root}\\{Path}#{ValueName}";
}

public sealed record TargetValueState(bool Exists, string Kind, string? Data)
{
    [JsonIgnore]
    public bool IsEmpty => Exists && Data is "";
}

public sealed record ObservedTargetState(string TargetIdentity, bool IsKnown, bool TargetExists,
    TargetValueState? Value, DateTimeOffset ObservedAtUtc, string? Limitation)
{
    public static ObservedTargetState Unknown(string targetIdentity, DateTimeOffset atUtc, string limitation) =>
        new(targetIdentity, false, false, null, atUtc, limitation);
}

public sealed record OperationCompatibility(string MinimumBuild, string? MaximumBuild, bool Windows11Only);

public sealed record ProtectionPolicy(bool RequiresBackup, bool RequiresFullTarget, bool RequiresRestorePoint);

public sealed record PlannedTarget(OperationTarget Target, TargetValueState DesiredValue);

public sealed record OperationDescriptor(
    string Id,
    int Version,
    string Category,
    string DisplayName,
    OperationScope Scope,
    ChangeKind Kind,
    RiskLevel Risk,
    OperationAvailability Availability,
    OperationCompatibility Compatibility,
    IReadOnlyList<OperationTarget> Targets,
    IReadOnlyList<PlannedTarget> Changes,
    ProtectionPolicy Protection,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Consequences,
    IReadOnlyList<string> KnownRisks,
    bool RequiresExplorerRestart,
    bool RequiresSystemRestart,
    IReadOnlyList<string> Limitations,
    string ReuseAttribution);

public interface IOperationCatalog
{
    string Version { get; }
    string ContentHash { get; }
    OperationDescriptor Get(string operationId);
    bool TryGet(string operationId, out OperationDescriptor? descriptor);
    IReadOnlyList<OperationDescriptor> All { get; }
}

public sealed record PlannedChange(string TargetIdentity, OperationTarget Target, ChangeKind Kind,
    bool BeforeTargetExists, bool AfterTargetExists, TargetValueState Before, TargetValueState After);

public sealed record OperationPlan(
    Guid PlanId,
    Guid TransactionId,
    string OperationId,
    int OperationVersion,
    string CatalogVersion,
    string CatalogContentHash,
    RiskLevel Risk,
    OperationScope Scope,
    ProtectionPolicy Protection,
    IReadOnlyList<PlannedChange> Changes,
    string ObservedStateHash,
    string PlanHash,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Limitations);

public sealed record SystemProfile(
    string ProductName,
    int Build,
    bool IsWindows11,
    bool IsElevated,
    IReadOnlyList<string> BlockedOperationIds,
    IReadOnlyList<string> BlockedCategories,
    IReadOnlyList<string> AppliedOperations,
    string Source,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<string> Limitations);

public interface ISystemProfileReader
{
    SystemProfile Read();
}

public interface ITargetStateReader
{
    Task<ObservedTargetState> ReadAsync(OperationTarget target, CancellationToken cancellationToken = default);
}

public sealed record ProtectionStatus(bool RestorePointAvailable, string? Reason,
    IReadOnlyList<string> Limitations);

public interface IProtectionProvider
{
    Task<ProtectionStatus> ProbeAsync(ProtectionPolicy policy, CancellationToken cancellationToken = default);
}

public sealed record PreflightResult(
    bool Passed,
    IReadOnlyList<BlockerCode> Blockers,
    IReadOnlyList<string> Details,
    IReadOnlyList<ObservedTargetState> ObservedStates,
    string ObservedStateHash,
    string CatalogVersion,
    string ProfileSource,
    IReadOnlyList<string> Limitations)
{
    public bool Contains(BlockerCode blocker) => Blockers.Contains(blocker);
}

public sealed record SimulationResult(
    bool CanExecute,
    OperationDescriptor Operation,
    OperationPlan Plan,
    IReadOnlyList<BlockerCode> Blockers,
    IReadOnlyList<string> Consequences,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Protections,
    IReadOnlyList<string> Limitations,
    string ExpectedResult)
{
    public bool HasEffects => false;
}

public sealed record AuthorizationToken(
    Guid AuthorizationId,
    Guid TransactionId,
    Guid PlanId,
    string OperationId,
    int OperationVersion,
    string PlanHash,
    string ObservedStateHash,
    string UserConfirmationId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool Consumed);

public sealed record AuthorizationValidation(bool Valid, BlockerCode Blocker, string? Reason);

public sealed record ExecutionRequest(
    Guid TransactionId,
    Guid PlanId,
    string OperationId,
    int OperationVersion,
    ChangeKind Kind,
    OperationTarget Target,
    TargetValueState Before,
    TargetValueState After);

public sealed record ExecutionOutcome(bool Applied, string? Error, bool Indeterminate)
{
    public static ExecutionOutcome Success() => new(true, null, false);
    public static ExecutionOutcome Failed(string error) => new(false, error, false);
    public static ExecutionOutcome Unknown(string error) => new(false, error, true);
}

public interface IOperationExecutor
{
    Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
}

public sealed record StepResult(string TargetIdentity, bool Changed, bool Applied, bool Verified,
    string? Error, IReadOnlyList<string> Effects);

public sealed record VerificationResult(bool Passed, string PostStateHash,
    IReadOnlyList<StepResult> Steps, IReadOnlyList<string> Limitations);

public enum BackupIntegrity { Valid, Corrupt, Incomplete, Missing, IdentityMismatch }

public sealed record BackupEntry(string TargetIdentity, bool TargetExists, TargetValueState? Value,
    string Checksum, DateTimeOffset? ObservedAtUtc = null);

public sealed record BackupRecord(
    Guid BackupId,
    Guid TransactionId,
    Guid PlanId,
    string OperationId,
    int OperationVersion,
    string PlanHash,
    int Sequence,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupEntry> Entries,
    string ContentHash,
    bool IsComplete,
    IReadOnlyList<string> Limitations);

public sealed record BackupValidation(bool Valid, BackupIntegrity Integrity, BackupRecord? Backup, string? Reason)
{
    public static BackupValidation Ok(BackupRecord backup) => new(true, BackupIntegrity.Valid, backup, null);
    public static BackupValidation Fail(BackupIntegrity integrity, string reason) =>
        new(false, integrity, null, reason);
}

public enum RecoveryOutcome
{
    NotAttempted,
    Completed,
    PartiallyCompleted,
    Blocked,
    Conflict,
    Failed,
    AlreadyRestored,
    NotRecoverable,
    Interrupted
}

public enum RecoveryConflictKind
{
    None,
    StateChangedAfterTransaction,
    TargetMissing,
    KindChanged,
    UnknownState
}

public sealed record RecoveryConflict(string TargetIdentity, RecoveryConflictKind Kind,
    TargetValueState? Observed, TargetValueState? ExpectedPost, TargetValueState? Current, string Description);

public sealed record RecoveryOptions(string UserConfirmationId);

public sealed record RecoveryPreview(Guid TransactionId, bool CanRecover, RecoveryOutcome Outcome,
    IReadOnlyList<RecoveryConflict> Conflicts, string? Reason,
    IReadOnlyList<StepResult> CurrentStates, IReadOnlyList<string> Limitations);

public sealed record RecoveryResult(
    Guid RecoveryId,
    Guid TransactionId,
    RecoveryOutcome Outcome,
    bool FullyRecovered,
    bool EffectMayHaveOccurred,
    IReadOnlyList<StepResult> Steps,
    IReadOnlyList<RecoveryConflict> Conflicts,
    string? Error,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string> Limitations);

public sealed record SafeOperationPresentation(
    string OperationId,
    string DisplayName,
    RiskLevel Risk,
    IReadOnlyList<string> Consequences,
    IReadOnlyList<string> Protections,
    string PlanHash,
    bool RequiresAuthorization,
    IReadOnlyList<string> RecoveryOptions,
    string ResultSummary,
    string RecoverySummary);

public static class SafetyText
{
    public static bool IsValidConfirmation(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => !char.IsControl(character)) &&
        !value.Contains("<!--", StringComparison.Ordinal) &&
        !value.Contains("-->", StringComparison.Ordinal) &&
        !value.Contains("```", StringComparison.Ordinal);
}

public interface IExclusiveLockFactory
{
    IDisposable Acquire(string key);
}

public sealed class TargetLockedException : Exception
{
    public TargetLockedException(string message) : base(message) { }
    public TargetLockedException(string message, Exception inner) : base(message, inner) { }
}
