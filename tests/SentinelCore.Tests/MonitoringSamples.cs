using System.Collections.Concurrent;
using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.Tests;

/// <summary>
/// Fixtures sintéticas da Fase 5. Nenhum teste usa o armazenamento pessoal: sessões, segmentos e incidentes são
/// construídos em memória ou em raiz temporária isolada.
/// </summary>
internal static class MonitoringSamples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    public static MonitoringLimits FastLimits() => new()
    {
        PerformanceInterval = TimeSpan.FromMilliseconds(100),
        EventInterval = TimeSpan.FromMilliseconds(100),
        NetworkInterval = TimeSpan.FromMilliseconds(150),
        SegmentDuration = TimeSpan.FromMilliseconds(250),
        MaxSamplesPerSource = 200,
        MaxEventsPerSession = 200,
        MaxEventsPerTick = 20,
        MaxIncidents = 5,
        MaxIncidentDescriptionLength = 40,
        MaxCollectorTick = TimeSpan.FromSeconds(2),
        MaxSessionBytes = 512 * 1024
    };

    public static MonitoringRequest FastRequest(MonitoringSources sources,
        TimeSpan? duration = null, bool authorized = true) =>
        new(MonitoringDurationPreset.Custom, duration ?? TimeSpan.FromMilliseconds(700), sources, authorized);

    public static MonitoringSessionRunner Runner(FakeMonitoringStore store,
        IReadOnlyList<IMonitoringSource> sources, MonitoringLimits? limits = null) =>
        new(sources, store, TimeProvider.System, limits ?? FastLimits());

    public static RawEventRecord Tdr(long recordId, DateTimeOffset at) => new(
        "System", "Display", 4101, recordId, at, EventLevel.Error, 2, null, null,
        "O driver de vídeo parou de responder e foi recuperado.", false, []);

    public static RawEventRecord ApplicationCrash(long recordId, DateTimeOffset at) => new(
        "Application", "Application Error", 1000, recordId, at, EventLevel.Error, 2, null, null,
        "Falha do aplicativo de teste.", false, []);

    public static MonitoringEventObservation EventObservation(
        RawEventRecord record, int index = 0, DateTimeOffset? timestamp = null)
    {
        var classification = EventCatalog.ClassifyEvent(record);
        return new MonitoringEventObservation(index, timestamp ?? record.OccurredUtc, record.Channel,
            record.Provider, record.EventId, record.RecordId, record.OccurredUtc, record.Level,
            classification.GroupId, classification.Category, classification.Severity,
            record.Message, record.MessageTruncated);
    }

    public static MonitoringPerformanceObservation PerformanceObservation(int index,
        DateTimeOffset? at = null, double cpu = 12.5, double available = 55) =>
        new(index, at ?? Now, index * 0.1, cpu, 16, available, 32UL * 1024 * 1024 * 1024,
            16UL * 1024 * 1024 * 1024, 16UL * 1024 * 1024 * 1024, []);

    public static MonitoringSegment Segment(Guid sessionId, MonitoringSource source,
        int sequence = 0, MonitoringEventObservation[]? events = null,
        MonitoringPerformanceObservation[]? performance = null,
        MonitoringNetworkObservation[]? network = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? id = null) =>
        new(1, id ?? Guid.NewGuid(), sessionId, sequence, source, "fake-collector", "1.0.0",
            from ?? Now, to ?? Now.AddSeconds(1), MonitoringSegmentStatus.Complete,
            performance ?? [], events ?? [], network ?? [], []);

    public static MonitoringIncident Incident(Guid sessionId, DateTimeOffset? occurred = null,
        string? description = "tela preta no navegador", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), sessionId, Now.AddSeconds(20), occurred,
            occurred is null ? MonitoringIncidentPrecision.Unknown : MonitoringIncidentPrecision.Approximate,
            MonitoringIncidentCategory.BlackScreen, description, 20,
            [new(MonitoringSource.Events, "1 evento(s)", "observado até o registro")],
            [MonitoringGuidance.IncidentLimitation,
                "Nenhum horário de ocorrência foi informado; apenas o momento do registro manual é conhecido."]);

    public static MonitoringSession Session(Guid id, IReadOnlyList<MonitoringSegment> segments,
        MonitoringSources requested = MonitoringSources.Events,
        MonitoringExecutionStatus status = MonitoringExecutionStatus.Completed,
        MonitoringCoverageStatus coverage = MonitoringCoverageStatus.Partial,
        MonitoringIncident[]? incidents = null,
        MonitoringRecoveryInfo? recovery = null,
        MonitoringBudgetReport? budget = null)
    {
        var sources = segments
            .GroupBy(segment => segment.Source)
            .Select(group => new MonitoringSourceSummary(group.Key, group.First().CollectorId,
                group.First().CollectorVersion, MonitoringSourceStatus.Partial,
                TimeSpan.FromMinutes(1), group.Count(), group.Count(), 0, 0,
                group.Sum(segment => segment.ObservationCount), false, false,
                ["Fixture sintética de teste; nenhum dado real foi observado."]))
            .ToArray();
        var performance = segments.Sum(segment => segment.Performance.Count);
        var events = segments.Sum(segment => segment.Events.Count);
        var network = segments.Sum(segment => segment.Network.Count);
        return new MonitoringSession(1, id, "0.0.0-test", SoftwareInfo.MonitoringSchemaVersion,
            MonitoringDurationPreset.Minutes5, TimeSpan.FromMinutes(5), Now, Now.AddMinutes(5),
            TimeSpan.FromMinutes(5), status, coverage, requested, sources,
            segments.Select(segment => segment.Reference).ToArray(), incidents ?? [],
            budget ?? new MonitoringBudgetReport(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2),
                segments.Count, segments.Count, 0, 2, 5, performance, events, 1024, 3 * 1024 * 1024,
                false, false, false, false, "desempenho 15 s · eventos 15 s · rede 30 s · segmentos 60 s",
                [MonitoringGuidance.BudgetLimitation]),
            recovery ?? MonitoringRecoveryInfo.None,
            [
                MonitoringGuidance.AuthorizationLimitation,
                MonitoringGuidance.ScopeLimitation,
                MonitoringGuidance.CausalityLimitation,
                MonitoringGuidance.BudgetLimitation,
                "Fixture sintética de teste; nenhum dado real foi observado."
            ]);
    }
}

internal sealed class FakeMonitoringStore : IMonitoringSessionStore
{
    public ConcurrentQueue<MonitoringSegment> Segments { get; } = new();
    public ConcurrentQueue<MonitoringSession> Sessions { get; } = new();
    public Func<MonitoringSegment, Exception?>? SegmentFailure { get; set; }
    public int SegmentWrites;
    public int SessionWrites;

    public Task<MonitoringStoreSnapshot> LoadMonitoringSessionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonitoringStoreSnapshot(Sessions.ToArray(), Segments.ToArray(), [], []));

    public Task<long> SaveMonitoringSegmentAsync(MonitoringSegment segment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SegmentFailure?.Invoke(segment) is { } error) throw error;
        if (Segments.Any(existing => existing.Id == segment.Id &&
                System.Text.Json.JsonSerializer.Serialize(existing) !=
                System.Text.Json.JsonSerializer.Serialize(segment)))
            throw new InvalidDataException("Segmento já existe com conteúdo diferente; histórico preservado.");
        Interlocked.Increment(ref SegmentWrites);
        Segments.Enqueue(segment);
        return Task.FromResult(1024L);
    }

    public Task<long> SaveMonitoringSessionAsync(MonitoringSession session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref SessionWrites);
        Sessions.Enqueue(session);
        return Task.FromResult(2048L);
    }

    public string GetMonitoringReportPath(Guid sessionId) =>
        Path.Combine(Path.GetTempPath(), "SentinelCoreMonitoringReports", $"{sessionId:D}.md");
}

internal sealed class FakeMonitoringPerformanceCollector : IPerformanceCollector
{
    public string Id => "monitoring-performance";
    public string Version => "1.0.0";
    public string Source => "Fake nativo de teste";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;

    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session();

    private sealed class Session : IPerformanceCollectorSession
    {
        private int reads;

        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reads++;
            var cpu = new CpuReading(10 + reads, 6 + reads, 4, null, 16, true, []);
            var memory = new MemoryReading(32UL * 1024 * 1024 * 1024, 16UL * 1024 * 1024 * 1024,
                16UL * 1024 * 1024 * 1024, 50, null, null, null, null, null, null, true, []);
            return Task.FromResult(new PerformanceCollectorTick("monitoring-performance", timestampUtc, false, cpu,
                memory, [], [], [], []));
        }

        public void Dispose() { }
    }
}

internal sealed class FakeMonitoringEventSource : IMonitoringEventSource
{
    private readonly Func<int, IReadOnlyList<RawEventRecord>> factory;
    private readonly bool limitHit;
    private int polls;

    public FakeMonitoringEventSource(Func<int, IReadOnlyList<RawEventRecord>> factory, bool limitHit = false)
    {
        this.factory = factory;
        this.limitHit = limitHit;
    }

    public string Id => "monitoring-events";
    public string Version => "1.0.0";
    public string Source => "Fake incremental de teste";
    public int Polls => polls;

    public IMonitoringEventProbe Start() => new Probe(() =>
    {
        var index = Interlocked.Increment(ref polls);
        return factory(index);
    }, limitHit);

    private sealed class Probe(Func<IReadOnlyList<RawEventRecord>> read, bool limitHit) : IMonitoringEventProbe
    {
        public Task<MonitoringEventPoll> PollAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MonitoringEventPoll(MonitoringSamples.Now, read(), limitHit, []));
        }

        public void Dispose() { }
    }
}

/// <summary>Fonte que demora mais que o orçamento por leitura.</summary>
internal sealed class SlowMonitoringSource(TimeSpan delay) : IMonitoringSource
{
    public MonitoringSource Kind => MonitoringSource.Performance;
    public string CollectorId => "slow-source";
    public string CollectorVersion => "1.0.0";

    public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IMonitoringSourceSession>(new Session(delay));

    private sealed class Session(TimeSpan delay) : IMonitoringSourceSession
    {
        private int index;

        public async Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var observation = MonitoringSamples.PerformanceObservation(index++, timestampUtc);
            return new MonitoringTick(MonitoringSource.Performance, timestampUtc, false, observation, [], null, []);
        }

        public void Dispose() { }
    }
}

/// <summary>Fonte que falha ao iniciar, para cobrir fonte indisponível sem inventar dados.</summary>
internal sealed class UnavailableMonitoringSource : IMonitoringSource
{
    public MonitoringSource Kind => MonitoringSource.Network;
    public string CollectorId => "unavailable-source";
    public string CollectorVersion => "1.0.0";

    public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken) =>
        Task.FromException<IMonitoringSourceSession>(
            new InvalidOperationException("Fonte simulada indisponível."));
}

/// <summary>Fonte que sinaliza o primeiro ciclo e aguarda a liberação para manter a sessão ativa nos testes.</summary>
internal sealed class BlockingMonitoringSource : IMonitoringSource
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MonitoringSource Kind => MonitoringSource.Performance;
    public string CollectorId => "blocking-source";
    public string CollectorVersion => "1.0.0";

    public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IMonitoringSourceSession>(new Session(this));

    private sealed class Session(BlockingMonitoringSource owner) : IMonitoringSourceSession
    {
        private int index;

        public async Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken)
        {
            owner.Started.TrySetResult();
            await owner.Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var observation = MonitoringSamples.PerformanceObservation(index++, timestampUtc);
            return new MonitoringTick(MonitoringSource.Performance, timestampUtc, false, observation, [], null, []);
        }

        public void Dispose() { }
    }
}

/// <summary>Fonte que cancela a própria sessão após N leituras.</summary>
internal sealed class CancellingMonitoringSource(int cancelAfter, CancellationTokenSource cancellation) :
    IMonitoringSource
{
    public MonitoringSource Kind => MonitoringSource.Performance;
    public string CollectorId => "cancelling-source";
    public string CollectorVersion => "1.0.0";

    public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IMonitoringSourceSession>(new Session(cancelAfter, cancellation));

    private sealed class Session(int cancelAfter, CancellationTokenSource cancellation) : IMonitoringSourceSession
    {
        private int index;

        public Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken)
        {
            var observation = MonitoringSamples.PerformanceObservation(index++, timestampUtc);
            if (index >= cancelAfter) cancellation.Cancel();
            return Task.FromResult(new MonitoringTick(MonitoringSource.Performance, timestampUtc, false,
                observation, [], null, []));
        }

        public void Dispose() { }
    }
}

/// <summary>Coletor sintético que nunca produz leitura válida, sem lançar exceção.</summary>
internal sealed class FakeEmptyPerformanceCollector : IPerformanceCollector
{
    public string Id => "empty-performance";
    public string Version => "1.0.0";
    public string Source => "Fake vazio de teste";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;

    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session();

    private sealed class Session : IPerformanceCollectorSession
    {
        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PerformanceCollectorTick.Empty("empty-performance", timestampUtc,
                "Nenhuma métrica disponível."));
        }

        public void Dispose() { }
    }
}

/// <summary>Coletor de rede sintético que permite simular leitura incompleta e falha total.</summary>
internal sealed class FakeNetworkCollector(
    IReadOnlyList<NetworkEndpoint>? endpoints = null,
    bool incomplete = false,
    bool unsupported = false) : INetworkSampleCollector
{
    public string Id => "fake-network";
    public string Version => "1.0.0";
    public string Source => "Fake network test";
    public NetworkSourceCategory Category => NetworkSourceCategory.Endpoints;

    public Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context,
        DateTimeOffset timestampUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (unsupported)
            return Task.FromResult(NetworkSampleReading.NotSupported(Id, "Coletor sintético indisponível."));
        IReadOnlyList<string> limitations = incomplete
            ? ["2 tabela(s) não foram lidas; a amostra está incompleta."]
            : [];
        return Task.FromResult(new NetworkSampleReading(Id, false, endpoints ?? [], [], null, null, 0,
            limitations));
    }
}

/// <summary>Relógio que salta após a primeira leitura, para provar que a duração usa relógio monotônico.</summary>
internal sealed class JumpingClock(DateTimeOffset start) : TimeProvider
{
    private int calls;

    public override DateTimeOffset GetUtcNow()
    {
        var index = Interlocked.Increment(ref calls);
        return index <= 1 ? start : start.AddHours(2);
    }
}
