namespace SentinelCore.Domain;

public enum PerformanceSessionPreset { Seconds30, Minute1, Minutes3, Custom }
public enum PerformanceSessionStatus { Running, Completed, Partial, Failed, Cancelled, Unsupported }
public enum PerformanceSourceStatus { Running, Completed, Partial, Failed, Unsupported, Cancelled }
public enum PerformanceSourceCategory { Cpu, Memory, Processes, Storage }
public enum StorageScope { Physical, Volume }
public enum PerformanceObservationKind
{
    SustainedCpu, LowAvailableMemory, CommitMargin, PrivateMemoryGrowth, DiskLatency, IoPeak
}

public sealed record PerformanceLimits
{
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxSamples { get; init; } = 300;
    public int MaxTrackedProcesses { get; init; } = 20;
    public int MaxStorageInstances { get; init; } = 12;
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromMinutes(3);
    public static PerformanceLimits Default { get; } = new();
}

public sealed record PerformanceSessionRequest(
    PerformanceSessionPreset Preset, TimeSpan Duration, TimeSpan SampleInterval)
{
    public static PerformanceSessionRequest ForPreset(PerformanceSessionPreset preset) => preset switch
    {
        PerformanceSessionPreset.Seconds30 => new(preset, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)),
        PerformanceSessionPreset.Minute1 => new(preset, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)),
        PerformanceSessionPreset.Minutes3 => new(preset, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), "Presets suportados: 30 s, 1 min e 3 min.")
    };

    public int ExpectedSamples => Duration <= TimeSpan.Zero || SampleInterval <= TimeSpan.Zero
        ? 0 : (int)Math.Floor(Duration / SampleInterval) + 1;
}

public sealed record PerformanceCollectionContext(
    Guid SessionId,
    IReadOnlyDictionary<int, string>? HardwareDiskIds,
    IReadOnlyDictionary<string, string>? HardwareVolumeIds,
    PerformanceLimits? Limits = null);

public sealed record CpuReading(
    double TotalPercent, double UserPercent, double KernelPercent,
    double? ProcessorQueueLength, int LogicalProcessors, bool Valid,
    IReadOnlyList<string> Limitations);

public sealed record MemoryReading(
    ulong InstalledBytes, ulong AvailableBytes, ulong UsedBytes, double? AvailablePercent,
    ulong? CommitLimitBytes, ulong? CommittedBytes, double? CommitUsagePercent, ulong? CommitHeadroomBytes,
    double? PagesPerSecond, ulong? SystemCacheBytes, bool Valid,
    IReadOnlyList<string> Limitations);

public sealed record ProcessReading(
    string Identity, int Pid, string Name, DateTimeOffset? StartTimeUtc,
    double CpuPercent, ulong WorkingSetBytes, ulong PrivateBytes,
    double IoReadBytesPerSecond, double IoWriteBytesPerSecond,
    bool CpuValid, bool IoValid, bool NewInSession);

public sealed record ProcessExitReading(
    string Identity, int Pid, string Name, DateTimeOffset ObservedUtc, string Reason);

public sealed record StorageReading(
    string Id, StorageScope Scope, string InstanceName, int? DiskNumber, string? VolumeLetter,
    double? ReadBytesPerSecond, double? WriteBytesPerSecond,
    double? ReadOperationsPerSecond, double? WriteOperationsPerSecond,
    double? ReadLatencyMs, double? WriteLatencyMs, double? QueueLength, double? PercentActiveTime,
    string? HardwareStableId, bool Valid);

public sealed record PerformanceCollectorTick(
    string CollectorId, DateTimeOffset TimestampUtc, bool Unsupported,
    CpuReading? Cpu, MemoryReading? Memory,
    IReadOnlyList<ProcessReading> Processes, IReadOnlyList<ProcessExitReading> Exits,
    IReadOnlyList<StorageReading> Storage, IReadOnlyList<string> Limitations)
{
    public static PerformanceCollectorTick Empty(string collectorId, DateTimeOffset at, string limitation) =>
        new(collectorId, at, false, null, null, [], [], [], [limitation]);

    public static PerformanceCollectorTick NotSupported(string collectorId, DateTimeOffset at, string limitation) =>
        new(collectorId, at, true, null, null, [], [], [], [limitation]);
}

public sealed record PerformanceSample(
    int Index, DateTimeOffset TimestampUtc, double ElapsedSeconds, bool Warmup,
    CpuReading? Cpu, MemoryReading? Memory,
    IReadOnlyList<ProcessReading> Processes, IReadOnlyList<ProcessExitReading> Exits,
    IReadOnlyList<StorageReading> Storage,
    long CollectionDurationMilliseconds, IReadOnlyList<string> Limitations);

public sealed record PerformanceSourceSummary(
    string CollectorId, string CollectorVersion, PerformanceSourceCategory Category, string Source,
    PerformanceSourceStatus Status, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    TimeSpan Duration, int SamplesRead, bool LimitHit, IReadOnlyList<string> Limitations)
{
    public double? MeanReadMilliseconds { get; init; }
    public double? MaxReadMilliseconds { get; init; }
}

public sealed record MetricStatistics(
    string Metric, string Unit, int ValidSamples, int TotalSamples,
    double? Min, double? Max, double? Mean, double? Median, double? P95, double? P99,
    double? First, double? Last, double? GrowthPercent, double? SlopePerMinute,
    double? Threshold, bool ThresholdIsUpperBound, double? TimeBeyondThresholdSeconds,
    IReadOnlyList<string> Limitations);

public sealed record ProcessStatistics(
    string Identity, int Pid, string Name,
    MetricStatistics CpuPercent, MetricStatistics PrivateBytes, MetricStatistics WorkingSetBytes,
    MetricStatistics IoReadBytesPerSecond, MetricStatistics IoWriteBytesPerSecond)
{
    public int Samples => CpuPercent.ValidSamples;
}

public sealed record StorageStatistics(
    string Id, StorageScope Scope, string InstanceName, int? DiskNumber, string? VolumeLetter,
    string? HardwareStableId,
    MetricStatistics ReadBytesPerSecond, MetricStatistics WriteBytesPerSecond,
    MetricStatistics ReadOperationsPerSecond, MetricStatistics WriteOperationsPerSecond,
    MetricStatistics ReadLatencyMs, MetricStatistics WriteLatencyMs,
    MetricStatistics QueueLength, MetricStatistics PercentActiveTime);

public sealed record PerformanceStatistics(
    TimeSpan Duration, int TotalSamples, int ValidCpuSamples, int ValidMemorySamples,
    MetricStatistics CpuTotalPercent, MetricStatistics CpuUserPercent, MetricStatistics CpuKernelPercent,
    MetricStatistics ProcessorQueueLength,
    MetricStatistics MemoryAvailablePercent, MetricStatistics MemoryUsedBytes,
    MetricStatistics CommitUsagePercent, MetricStatistics PagesPerSecond,
    IReadOnlyList<ProcessStatistics> Processes, IReadOnlyList<StorageStatistics> Storage,
    IReadOnlyList<string> Limitations);

public sealed record PerformanceEvidencePoint(
    DateTimeOffset TimestampUtc, int SampleIndex, double Value, string Unit);

public sealed record PerformanceObservation(
    string Id, string RuleId, string RuleVersion, PerformanceObservationKind Kind,
    string Title, string Statement,
    EvidenceSeverity Severity, EvidenceConfidence Confidence, CausalityStatus Causality,
    string Metric, string Unit, double? Threshold, string? Subject, string? SubjectName,
    DateTimeOffset WindowFromUtc, DateTimeOffset WindowToUtc, TimeSpan Duration,
    int SupportingSampleCount, int EvaluatedSampleCount,
    double? ObservedMin, double? ObservedMax, double? ObservedMean, double? ObservedP95,
    string Source, IReadOnlyList<PerformanceEvidencePoint> Points, IReadOnlyList<string> Limitations);

public enum PerformanceNoteReason
{
    NotSpecified, Unavailable, InvalidSamples, InsufficientSamples,
    PreconditionNotObserved, ConditionNotObserved, Observed
}

public sealed record PerformanceAnalysisNote(
    string RuleId, string RuleVersion, bool Evaluated, string Detail,
    PerformanceNoteReason Reason = PerformanceNoteReason.NotSpecified);

public sealed record SelfOverheadSample(
    double CpuPercent, ulong WorkingSetBytes, ulong PrivateBytes, long CollectionDurationMilliseconds);

public sealed record SelfOverheadSummary(
    int Samples, double? CpuPercentMean, double? CpuPercentMax,
    ulong? WorkingSetBytesMax, ulong? PrivateBytesMax,
    double? CollectionDurationMeanMilliseconds, double? CollectionDurationMaxMilliseconds);

public enum PerformanceExecutionStatus { Running, Completed, Cancelled, Failed, Unsupported }
public enum PerformanceCoverageStatus { Complete, Partial, Unavailable }

public sealed record PerformanceCoverage(
    PerformanceExecutionStatus Execution,
    PerformanceCoverageStatus Status,
    int ExpectedSamples, int CollectedSamples, int MissedOpportunities,
    bool ProcessLimitReached, bool StorageLimitReached, bool SampleLimitReached,
    IReadOnlyList<string> UnavailableCollectors,
    IReadOnlyList<string> Limitations)
{
    public static PerformanceCoverage FromLegacy(PerformanceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var expected = session.RequestedDuration <= TimeSpan.Zero || session.SampleInterval <= TimeSpan.Zero
            ? 0 : (int)Math.Floor(session.RequestedDuration / session.SampleInterval) + 1;
        var collected = session.Samples.Count;
        var unavailable = session.Sources
            .Where(source => source.Status is PerformanceSourceStatus.Failed or PerformanceSourceStatus.Unsupported)
            .Select(source => source.CollectorId).ToArray();
        bool Has(string prefix, string category) => session.Limitations.Any(limitation =>
            limitation.StartsWith(prefix, StringComparison.Ordinal) &&
            limitation.Contains(category, StringComparison.OrdinalIgnoreCase));
        var processLimit = session.Sources.Any(source => source.LimitHit &&
                source.Category == PerformanceSourceCategory.Processes) ||
            Has("Limite de", "processos");
        var storageLimit = session.Sources.Any(source => source.LimitHit &&
                source.Category == PerformanceSourceCategory.Storage) ||
            Has("Limite de", "instâncias de armazenamento");
        var sampleLimit = Has("Limite de", "amostras");
        var execution = session.Status switch
        {
            PerformanceSessionStatus.Cancelled => PerformanceExecutionStatus.Cancelled,
            PerformanceSessionStatus.Failed => PerformanceExecutionStatus.Failed,
            PerformanceSessionStatus.Unsupported => PerformanceExecutionStatus.Unsupported,
            _ => PerformanceExecutionStatus.Completed
        };
        var anyPartial = session.Sources.Any(source => source.Status == PerformanceSourceStatus.Partial);
        var complete = execution == PerformanceExecutionStatus.Completed &&
            collected >= expected && !processLimit && !storageLimit && !sampleLimit &&
            unavailable.Length == 0 && !anyPartial;
        var status = collected == 0 || execution is PerformanceExecutionStatus.Failed
                or PerformanceExecutionStatus.Unsupported
            ? PerformanceCoverageStatus.Unavailable
            : complete ? PerformanceCoverageStatus.Complete : PerformanceCoverageStatus.Partial;
        var limitations = new List<string>
        {
            "Cobertura derivada do formato 1: oportunidades perdidas estimadas pela diferença entre " +
            "amostras esperadas e coletadas; ausência de registro de limitação não amplia a cobertura."
        };
        limitations.AddRange(session.Limitations);
        return new(execution, status, expected, collected, Math.Max(0, expected - collected),
            processLimit, storageLimit, sampleLimit, unavailable,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }
}

public sealed record PerformanceQualityAnnotation(
    string DefectId, string Reason, string AffectedVersion, string FixedVersion,
    IReadOnlyList<string> AffectedMetrics, bool ComparisonBlocked);

public static class PerformanceQualityCatalog
{
    // Identificadores completos das sete sessões gravadas antes da correção do intervalo cumulativo,
    // conforme docs/VALIDATION.md. A anotação é derivada em leitura e nunca altera o registro durável.
    private static readonly Guid[] CumulativeIntervalDefectSessions =
    [
        Guid.Parse("414b5cc7-3b10-46fe-9626-f161f45afa9b"),
        Guid.Parse("d413216f-e257-42c3-b08f-970a816e3efe"),
        Guid.Parse("4b4f6f0f-49f7-485b-98eb-4c9afdd3c34e"),
        Guid.Parse("0e555edf-3667-455b-b2f5-7f99f5935bc4"),
        Guid.Parse("000a1878-8c23-40f5-8a18-ae9654d0e1fa"),
        Guid.Parse("3d27d713-0a42-4b92-bf8c-6c0b718ab45c"),
        Guid.Parse("63257da5-d49b-47f3-b0f9-da354135b1c9")
    ];

    private static readonly PerformanceQualityAnnotation CumulativeIntervalDefect = new(
        "PERF-INTERVAL-CUMULATIVE",
        "O intervalo acumulado desde o início da sessão foi usado como denominador das taxas, " +
        "subnotificando CPU total, CPU por processo e I/O.",
        "0.3.0-preview.2", "0.3.0-preview.3",
        ["cpu.total.percent", "process.cpu.percent", "process.io", "storage.throughput"],
        ComparisonBlocked: true);

    public static PerformanceQualityAnnotation? Find(Guid sessionId) =>
        CumulativeIntervalDefectSessions.Contains(sessionId) ? CumulativeIntervalDefect : null;
}

public sealed record PerformanceSession(
    int FormatVersion, Guid Id, string SoftwareVersion, string SchemaVersion,
    PerformanceSessionPreset Preset, TimeSpan RequestedDuration, TimeSpan SampleInterval,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, TimeSpan Duration,
    PerformanceSessionStatus Status,
    IReadOnlyList<PerformanceSourceSummary> Sources,
    PerformanceStatistics Statistics,
    IReadOnlyList<PerformanceObservation> Observations,
    IReadOnlyList<PerformanceAnalysisNote> AnalysisNotes,
    IReadOnlyList<PerformanceSample> Samples,
    SelfOverheadSummary SelfOverhead,
    IReadOnlyList<string> Limitations,
    PerformanceCoverage? Coverage = null);

public sealed record PerformanceProgress(
    int SamplesCollected, int ExpectedSamples, TimeSpan Elapsed, TimeSpan Requested,
    PerformanceSample? Latest);

public sealed record PerformanceStoreSnapshot(
    IReadOnlyList<PerformanceSession> Sessions, IReadOnlyList<string> Warnings);

public interface IPerformanceCollector
{
    string Id { get; }
    string Version { get; }
    string Source { get; }
    PerformanceSourceCategory Category { get; }
    IPerformanceCollectorSession StartSession(PerformanceCollectionContext context);
}

public interface IPerformanceCollectorSession : IDisposable
{
    Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
        CancellationToken cancellationToken);
}

public interface IPerformanceSessionStore
{
    Task<PerformanceStoreSnapshot> LoadSessionsAsync(CancellationToken cancellationToken = default);
    Task<long> SaveSessionAsync(PerformanceSession session, CancellationToken cancellationToken = default);
    string GetPerformanceReportPath(Guid sessionId);
}
