namespace SentinelCore.Domain;

public enum RecoveryModule { Tweaks, Debloat, ControlledRemoval, WindowsRepair }
public enum RecoveryEvidenceStatus { Complete, Partial, Unavailable }
public enum RecoveryOperationOutcome { Completed, Failed, Indeterminate, Blocked, Cancelled, NotStarted }
public enum RecoveryCapability { PotentiallyAvailable, Blocked, Unsupported, InterventionRequired, Indeterminate, RequiresAnalysis }
public enum RecoveryOrigin { Synthetic, Real, Unknown }

/// <summary>Ephemeral read model. Original Markdown and existing journals remain authoritative.</summary>
public sealed record RecoveryHistorySource(
    RecoveryModule Module, RecoveryEvidenceStatus Status,
    IReadOnlyList<TransactionRecord> Transactions, IReadOnlyList<RepairRecord> Repairs,
    IReadOnlyList<RepairIntent> UnfinishedRepairs, IReadOnlyList<string> Warnings);

/// <summary>No write or execution capability. Must preserve unavailable versus empty sources.</summary>
public interface IRecoveryHistoryReader
{
    Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default);
    string GetRecoveryReportPath(RecoveryModule module, Guid id);
}

public sealed record RecoveryTimelineEvent(int Sequence, DateTimeOffset AtUtc, string State,
    string? Detail, string? Error);

public sealed record RecoveryOperation(
    Guid Id, RecoveryModule Module, string OperationId, string Type, string Target,
    DateTimeOffset AtUtc, string State, RecoveryOperationOutcome Outcome, RecoveryOrigin Origin,
    RiskLevel? Risk, RecoveryCapability Capability, bool HasConflict, bool RequiresIntervention,
    bool EffectMayHaveOccurred, bool TargetPersistentlyBlocked, string Authorization,
    string Backup, string Verification, IReadOnlyList<string> Consequences,
    IReadOnlyList<string> Limitations, IReadOnlyList<string> Guidance,
    IReadOnlyList<RecoveryTimelineEvent> Events, IReadOnlyList<RecoveryResult> Recoveries,
    string ReportPath, TransactionRecord? Transaction = null, RepairRecord? Repair = null,
    RecoveryPreview? Preview = null)
{
    public bool Interrupted => State is nameof(TransactionState.Interrupted) or nameof(TransactionState.ExecutionStarted)
        or nameof(TransactionState.RecoveryRequested) or nameof(TransactionState.RecoveryStarted) ||
        Repair?.State is RepairState.Interrupted or RepairState.Indeterminate ||
        Module == RecoveryModule.WindowsRepair && Repair is null;

    public bool PartiallyApplied => Module == RecoveryModule.Tweaks && Transaction?.Events.Any(entry =>
        entry.State is (TransactionState.ExecutionFailed or TransactionState.ExecutionIndeterminate) &&
        entry.Results?.Any(step => step.Applied && step.Changed) == true) == true;
}

public sealed record RecoveryCenterSnapshot(IReadOnlyList<RecoveryOperation> Operations,
    IReadOnlyList<RecoveryHistorySource> Sources, IReadOnlyList<string> Warnings)
{
    public StoreSnapshot? InterruptedAudits { get; init; }

    public bool IsComplete => Sources.Count == 4 && Sources.All(s => s.Status == RecoveryEvidenceStatus.Complete)
        && Warnings.Count == 0;
}

public sealed record RecoveryCenterFilter(
    string? Search = null, RecoveryModule? Module = null, string? Type = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, string? State = null,
    RiskLevel? Risk = null, RecoveryOperationOutcome? Outcome = null, RecoveryOrigin? Origin = null,
    RecoveryCapability? Capability = null, bool? RecoveryAvailable = null,
    bool? Conflict = null, bool? Intervention = null);

/// <summary>Counts describe loaded, unique records only. Incomplete history never implies a global zero.</summary>
public sealed record RecoveryCenterSummary(int Registered, int Completed, int Failed, int Indeterminate,
    int Conflicts, int Intervention, int RecoveriesCompleted, int RecoveriesPartial,
    int RecoveriesBlocked, int RecoveriesFailed, bool IsComplete);
