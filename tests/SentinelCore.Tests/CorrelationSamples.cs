using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.Tests;

/// <summary>
/// Fixtures sintéticas da Fase 3.4. Nenhum dado real é lido e nenhuma configuração do Windows é alterada.
/// </summary>
public static class CorrelationSamples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset NetworkAt = new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero);

    public static FixedTime Clock(DateTimeOffset? now = null) => new() { Now = now ?? Now };

    public static AuditRecord AuditWithBoot(DateTimeOffset boot, DateTimeOffset? collectedUtc = null,
        Guid? id = null)
    {
        var auditId = id ?? Guid.NewGuid();
        var at = collectedUtc ?? Now.AddHours(-1);
        var data = new InventoryData(OperatingSystem: new OperatingSystemInfo(
            "Windows 11", "Pro", "10.0", "26100", "x64", boot));
        var result = new CollectorResult(auditId, "windows", "1.0.0", at, at, TimeSpan.Zero,
            DiagnosticStatus.Completed, "Fake: correlation test", data, []);
        return new(1, auditId, AuditKind.Basic, SoftwareInfo.Version, at, at, TimeSpan.Zero,
            DiagnosticStatus.Completed, [result]);
    }

    public static Task<EventInvestigation> EventsAsync(DateTimeOffset? now = null,
        IReadOnlyList<RawEventRecord>? events = null, IReadOnlyList<RawWerReport>? reports = null) =>
        new EventInvestigationRunner(GraphicsSamples.RealisticEvents(events ?? [], reports ?? []),
                Clock(now))
            .RunAsync(EventWindow.Last24Hours);

    public static Task<GraphicsInvestigation> GraphicsAsync(IReadOnlyList<RawEventRecord>? events = null,
        IReadOnlyList<RawWerReport>? reports = null,
        IReadOnlyList<IHardwareCollector>? hardware = null)
    {
        var runner = GraphicsSamples.Runner(Clock(), hardware: hardware,
            events: GraphicsSamples.RealisticEvents(events ?? [], reports ?? []));
        return runner.RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));
    }

    public static HardwareSnapshot Snapshot(DateTimeOffset at, params HardwareItem[] items)
    {
        var results = items.GroupBy(item => item.Category)
            .Select(group => new HardwareCollectorResult(group.Key.ToString().ToLowerInvariant(), "1.0.0",
                group.Key, "Fake: correlation test", at, at, TimeSpan.Zero, HardwareCollectionStatus.Completed,
                group.ToArray(), []))
            .ToArray();
        if (results.Length == 0)
            results = [new HardwareCollectorResult("cpu", "1.0.0", HardwareCategory.Cpu, "Fake",
                at, at, TimeSpan.Zero, HardwareCollectionStatus.Completed,
                [new HardwareItem(HardwareCategory.Cpu, "cpu", "synthetic-cpu", "CPU sintética", null, null,
                    null, null, null, null, null, null, null, null, DevicePresence.Present, [], [])], [])];
        return new(1, Guid.NewGuid(), SoftwareInfo.Version, SoftwareInfo.SnapshotsSchemaVersion, at, at,
            TimeSpan.Zero, HardwareCollectionStatus.Completed, results);
    }

    public static PerformanceSession Performance(string subject = "4242:100", Guid? id = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        var session = PerformanceSamples.Session();
        var from = fromUtc ?? Now.AddHours(-2);
        var to = toUtc ?? from.AddMinutes(1);
        var observation = new PerformanceObservation(
            $"PERF-PROC-PRIVATE-GROWTH@{from.UtcDateTime:O}", "PERF-PROC-PRIVATE-GROWTH", "1.0",
            PerformanceObservationKind.PrivateMemoryGrowth,
            "Crescimento de memória privada observado",
            "O processo acompanhado apresentou crescimento de memória privada na janela; causalidade não avaliada.",
            EvidenceSeverity.PotentialImpact, EvidenceConfidence.Low, CausalityStatus.NotAssessed,
            "process.private.bytes", "bytes", 64d * 1048576d, subject, "app.exe",
            from, to, to - from, 10, 10, 100d, 200d, 150d, 190d, "performance-process",
            [], ["Crescimento observado não comprova vazamento nem causa."]);
        return session with
        {
            Id = id ?? Guid.NewGuid(),
            StartedUtc = from,
            CompletedUtc = to,
            Duration = to - from,
            Observations = [observation],
            AnalysisNotes = [new("PERF-PROC-PRIVATE-GROWTH", "1.0", true, "Observado.", PerformanceNoteReason.Observed)]
        };
    }

    public static NetworkSession Network(string subject = "4242:100", Guid? id = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        var session = NetworkPersistenceTests.CreateValidSession(sampleCount: 3, includeObservation: true);
        var from = fromUtc ?? NetworkAt;
        var to = toUtc ?? from.AddMinutes(1);
        var template = session.Observations[0];
        var observation = template with
        {
            Subject = subject,
            SubjectName = "app.exe",
            WindowFromUtc = from,
            WindowToUtc = to,
            Duration = to - from
        };
        return session with
        {
            Id = id ?? Guid.NewGuid(),
            StartedUtc = from,
            CompletedUtc = to,
            Duration = to - from,
            Observations = [observation]
        };
    }

    public static CorrelationEvidenceInput Input(
        IReadOnlyList<AuditRecord>? audits = null,
        IReadOnlyList<HardwareSnapshot>? hardware = null,
        IReadOnlyList<EventInvestigation>? events = null,
        IReadOnlyList<PerformanceSession>? performance = null,
        IReadOnlyList<NetworkSession>? network = null,
        IReadOnlyList<GraphicsInvestigation>? graphics = null,
        CorrelationSources sources = CorrelationSources.All,
        CorrelationSourceStatus status = CorrelationSourceStatus.Completed)
    {
        var reads = Enum.GetValues<CorrelationSource>()
            .Where(source => source != CorrelationSource.Tweaks || sources.HasFlag(CorrelationSources.Tweaks))
            .Select(source =>
        {
            if (!sources.HasFlag(FlagFor(source)))
                return new CorrelationSourceRead(source, CorrelationSourceStatus.Skipped, 0, false, []);
            var count = source switch
            {
                CorrelationSource.Audits => audits?.Count ?? 0,
                CorrelationSource.Hardware => hardware?.Count ?? 0,
                CorrelationSource.Events => events?.Count ?? 0,
                CorrelationSource.Performance => performance?.Count ?? 0,
                CorrelationSource.Network => network?.Count ?? 0,
                _ => graphics?.Count ?? 0
            };
            return new CorrelationSourceRead(source, status, count, false, []);
        }).ToArray();
        return new(audits ?? [], hardware ?? [], events ?? [], performance ?? [], network ?? [], graphics ?? [],
            reads);
    }

    public static CorrelationInvestigation NormalizeAndAnalyze(CorrelationEvidenceInput input,
        CorrelationLimits? limits = null, DateTimeOffset? now = null)
    {
        var clock = Clock(now);
        var window = EventWindows.Resolve(EventWindow.Last24Hours, clock.GetUtcNow());
        var options = limits ?? CorrelationLimits.Default;
        var normalized = new CorrelationEvidenceNormalizer().Normalize(input, window, options);
        var analysis = new CorrelationEngine().Analyze(normalized.Evidence, normalized.Sources, normalized.Boot,
            window, options);
        var coverage = CorrelationCoverageCalculator.Build(normalized, analysis, cancelled: false);
        var status = coverage.Status == CorrelationCoverageStatus.Complete
            ? CorrelationStatus.Completed
            : coverage.Status == CorrelationCoverageStatus.Unavailable
                ? CorrelationStatus.Failed
                : CorrelationStatus.Partial;
        return new CorrelationInvestigation(1, Guid.NewGuid(), SoftwareInfo.Version,
            SoftwareInfo.CorrelationSchemaVersion, window.Preset, window.FromUtc, window.ToUtc,
            clock.GetUtcNow(), clock.GetUtcNow(), TimeSpan.Zero, status,
            CorrelationSources.All, normalized.Sources, normalized.Boot, normalized.Evidence,
            normalized.Timeline, analysis.Relations, analysis.Notes, coverage, [
                CorrelationGuidance.CausalityLimitation,
                CorrelationGuidance.ScopeLimitation,
                CorrelationGuidance.NonRenewalLimitation,
                CorrelationGuidance.AbsenceLimitation,
                normalized.Boot.Note
            ]);
    }

    public static CorrelationSources FlagFor(CorrelationSource source) => source switch
    {
        CorrelationSource.Audits => CorrelationSources.Audits,
        CorrelationSource.Hardware => CorrelationSources.Hardware,
        CorrelationSource.Events => CorrelationSources.Events,
        CorrelationSource.Performance => CorrelationSources.Performance,
        CorrelationSource.Network => CorrelationSources.Network,
        CorrelationSource.Graphics => CorrelationSources.Graphics,
        CorrelationSource.Tweaks => CorrelationSources.Tweaks,
        _ => CorrelationSources.None
    };

    public static CorrelationRelation? Relation(CorrelationInvestigation investigation, string ruleId) =>
        investigation.Relations.FirstOrDefault(relation => relation.RuleId == ruleId);

    public sealed class FakeAuditStore : IAuditStore
    {
        public List<AuditRecord> Records { get; } = [];
        public List<string> Warnings { get; } = [];
        public Exception? Failure { get; set; }

        public Task<StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(new StoreSnapshot(Records.ToArray(), Warnings.ToArray()))
                : Task.FromException<StoreSnapshot>(Failure);

        public Task SaveAsync(AuditRecord audit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetReportPath(Guid auditId) => $@"synthetic\{auditId:D}.md";
    }

    public sealed class FakeHardwareStore : IHardwareSnapshotStore
    {
        public List<HardwareSnapshot> Records { get; } = [];
        public List<string> Warnings { get; } = [];
        public Exception? Failure { get; set; }

        public Task<HardwareStoreSnapshot> LoadSnapshotsAsync(CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(new HardwareStoreSnapshot(Records.ToArray(), Warnings.ToArray()))
                : Task.FromException<HardwareStoreSnapshot>(Failure);

        public Task SaveSnapshotAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetHardwareReportPath(Guid snapshotId) => $@"synthetic\{snapshotId:D}.md";
    }

    public sealed class FakeEventStore : IEventInvestigationStore
    {
        public List<EventInvestigation> Records { get; } = [];
        public List<string> Warnings { get; } = [];
        public Exception? Failure { get; set; }

        public Task<EventStoreSnapshot> LoadInvestigationsAsync(CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(new EventStoreSnapshot(Records.ToArray(), Warnings.ToArray()))
                : Task.FromException<EventStoreSnapshot>(Failure);

        public Task SaveInvestigationAsync(EventInvestigation investigation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string GetEventReportPath(Guid investigationId) => $@"synthetic\{investigationId:D}.md";
    }

    public sealed class FakePerformanceStore : IPerformanceSessionStore
    {
        public List<PerformanceSession> Records { get; } = [];
        public List<string> Warnings { get; } = [];
        public Exception? Failure { get; set; }

        public Task<PerformanceStoreSnapshot> LoadSessionsAsync(CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(new PerformanceStoreSnapshot(Records.ToArray(), Warnings.ToArray()))
                : Task.FromException<PerformanceStoreSnapshot>(Failure);

        public Task<long> SaveSessionAsync(PerformanceSession session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string GetPerformanceReportPath(Guid sessionId) => $@"synthetic\{sessionId:D}.md";
    }

    public sealed class FakeNetworkStore : INetworkSessionStore
    {
        public List<NetworkSession> Records { get; } = [];
        public List<string> Warnings { get; } = [];
        public Exception? Failure { get; set; }

        public Task<NetworkStoreSnapshot> LoadNetworkSessionsAsync(CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(new NetworkStoreSnapshot(Records.ToArray(), Warnings.ToArray()))
                : Task.FromException<NetworkStoreSnapshot>(Failure);

        public Task<long> SaveNetworkSessionAsync(NetworkSession session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string GetNetworkReportPath(Guid sessionId) => $@"synthetic\{sessionId:D}.md";
    }

    public sealed class FakeGraphicsStore : IGraphicsInvestigationStore
    {
        public List<GraphicsInvestigation> Records { get; } = [];
        public List<string> Warnings { get; } = [];
        public Exception? Failure { get; set; }

        /// <summary>Permite cancelar a investigação depois que a leitura terminou, para exercitar o caminho
        /// de cancelamento no processamento sem descartar as evidências já lidas.</summary>
        public CancellationTokenSource? CancelAfterLoad { get; set; }

        public Task<GraphicsStoreSnapshot> LoadGraphicsInvestigationsAsync(
            CancellationToken cancellationToken = default)
        {
            CancelAfterLoad?.Cancel();
            return Failure is null
                ? Task.FromResult(new GraphicsStoreSnapshot(Records.ToArray(), Warnings.ToArray()))
                : Task.FromException<GraphicsStoreSnapshot>(Failure);
        }

        public Task<long> SaveGraphicsInvestigationAsync(GraphicsInvestigation investigation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string GetGraphicsReportPath(Guid investigationId) => $@"synthetic\{investigationId:D}.md";
    }
}
