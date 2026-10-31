namespace SentinelCore.Domain;

public enum EventCategory { Graphics, Power, Storage, Network, Applications, Services, Other }
public enum EventWindow { Last24Hours, Last7Days, Last30Days, Custom }
public enum EventInvestigationStatus { Running, Completed, Partial, Failed, Cancelled, Unsupported }
public enum EventSourceStatus { Running, Completed, Partial, Failed, Unsupported, Cancelled }
public enum EventLevel { Critical, Error, Warning, Information, Verbose, Unknown }
public enum EvidenceSeverity { Unknown, Informational, PotentialImpact, ObservedImpact }
public enum EvidenceConfidence { Low, Medium, High }
public enum CausalityStatus { NotAssessed }

public sealed record EventProperty(string Name, string? Value);

public sealed record EventChannelState(string Channel, bool Exists, bool Enabled, bool Accessible, string? Detail);

public sealed record EventQueryWindow(EventWindow Preset, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

public sealed record RawEventRecord(
    string Channel,
    string Provider,
    int EventId,
    long? RecordId,
    DateTimeOffset OccurredUtc,
    EventLevel Level,
    int? RawLevel,
    int? TaskCode,
    Guid? ActivityId,
    string? Message,
    bool MessageTruncated,
    IReadOnlyList<EventProperty> Data);

public sealed record RawWerReport(
    string ReportId,
    bool ReportIdFromFile,
    string Source,
    string Store,
    string FolderName,
    string? EventType,
    string? ProblemCode,
    DateTimeOffset? EventTimeUtc,
    DateTimeOffset? CreationTimeUtc,
    string? ReportStatus,
    string? Application,
    string? ApplicationPath,
    string? Module,
    string? ModuleVersion,
    string? BucketId,
    IReadOnlyList<EventProperty> Data,
    IReadOnlyList<string> Limitations,
    EvidenceConfidence Confidence);

public sealed record EventCollectorResult(
    string CollectorId,
    string CollectorVersion,
    string Source,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    EventSourceStatus Status,
    int EventsRead,
    int ReportsRead,
    bool LimitHit,
    IReadOnlyList<RawEventRecord> Events,
    IReadOnlyList<RawWerReport> Reports,
    IReadOnlyList<EventChannelState> Channels,
    IReadOnlyList<string> Limitations);

public sealed record EventLogEvidence(
    string Id,
    string GroupId,
    string Channel,
    string Provider,
    int EventId,
    long? RecordId,
    DateTimeOffset OccurredUtc,
    EventLevel Level,
    int? RawLevel,
    int? TaskCode,
    Guid? ActivityId,
    string? Message,
    bool MessageTruncated,
    IReadOnlyList<EventProperty> Data,
    IReadOnlyList<string> Limitations);

public sealed record WerEvidence(
    string Id,
    string GroupId,
    string ReportId,
    bool ReportIdFromFile,
    string Source,
    string Store,
    string FolderName,
    string? EventType,
    string? ProblemCode,
    DateTimeOffset? EventTimeUtc,
    DateTimeOffset? CreationTimeUtc,
    string? ReportStatus,
    string? Application,
    string? ApplicationPath,
    string? Module,
    string? ModuleVersion,
    string? BucketId,
    IReadOnlyList<EventProperty> Data,
    IReadOnlyList<string> Limitations,
    EvidenceConfidence Confidence);

public sealed record EvidenceGroup(
    string Id,
    EventCategory Category,
    string Title,
    string? Provider,
    int? EventId,
    string? ProblemCode,
    EvidenceSeverity Severity,
    EvidenceConfidence Confidence,
    CausalityStatus Causality,
    int EventRecordCount,
    int DistinctReportCount,
    int RePresentationCount,
    DateTimeOffset? FirstObservedUtc,
    DateTimeOffset? LastObservedUtc,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Limitations);

public sealed record EventSourceSummary(
    string CollectorId,
    string CollectorVersion,
    string Source,
    EventSourceStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    int EventsRead,
    int ReportsRead,
    bool LimitHit,
    IReadOnlyList<string> Limitations);

public sealed record EventInvestigation(
    int FormatVersion,
    Guid Id,
    string SoftwareVersion,
    string SchemaVersion,
    EventWindow Window,
    DateTimeOffset PeriodFromUtc,
    DateTimeOffset PeriodToUtc,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    EventInvestigationStatus Status,
    IReadOnlyList<EventChannelState> Channels,
    IReadOnlyList<EventSourceSummary> Sources,
    IReadOnlyList<EvidenceGroup> Groups,
    IReadOnlyList<EventLogEvidence> Events,
    IReadOnlyList<WerEvidence> Reports,
    IReadOnlyList<string> Limitations);

public sealed record EventInvestigationProgress(string CollectorId, EventSourceStatus Status, int Finished, int Total);

public sealed record EventStoreSnapshot(IReadOnlyList<EventInvestigation> Investigations, IReadOnlyList<string> Warnings);

// Limits are explicit and configurable so partial counts are never presented as complete.
public sealed record InvestigationLimits(
    int MaxEventsPerChannel = 1500,
    int MaxTotalEvents = 3000,
    int MaxWerReportsPerStore = 1000,
    int MaxTotalWerReports = 1500,
    int MaxMessageChars = 1200,
    int MaxWerFileBytes = 1048576,
    TimeSpan? MaxCollectionTime = null)
{
    public TimeSpan CollectionTime => MaxCollectionTime ?? TimeSpan.FromSeconds(25);
    public static InvestigationLimits Default { get; } = new(MaxCollectionTime: TimeSpan.FromSeconds(25));
}

public interface IEventEvidenceCollector
{
    string Id { get; }
    string Version { get; }
    string Source { get; }
    Task<EventCollectorResult> CollectAsync(Guid investigationId, EventQueryWindow window,
        CancellationToken cancellationToken);
}

public interface IEventInvestigationStore
{
    Task<EventStoreSnapshot> LoadInvestigationsAsync(CancellationToken cancellationToken = default);
    Task SaveInvestigationAsync(EventInvestigation investigation, CancellationToken cancellationToken = default);
    string GetEventReportPath(Guid investigationId);
}
