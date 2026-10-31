using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class PerformanceSessionRunner
{
    private readonly IReadOnlyList<IPerformanceCollector> collectors;
    private readonly PerformanceStatisticsEngine statistics = new();
    private readonly PerformanceEvidenceAnalyzer analyzer;
    private readonly PerformanceRuleOptions rules;
    private readonly PerformanceLimits limits;
    private readonly TimeProvider clock;
    private int running;

    public PerformanceSessionRunner(IEnumerable<IPerformanceCollector> collectors,
        TimeProvider? clock = null, PerformanceLimits? limits = null,
        PerformanceRuleOptions? rules = null)
    {
        this.collectors = collectors.ToArray();
        if (this.collectors.Count == 0 ||
            this.collectors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != this.collectors.Count)
            throw new ArgumentException("Coletores de desempenho devem ter identificadores únicos e não podem estar vazios.",
                nameof(collectors));
        this.clock = clock ?? TimeProvider.System;
        this.limits = limits ?? PerformanceLimits.Default;
        this.rules = rules ?? PerformanceRuleOptions.Default;
        analyzer = new(this.rules);
    }

    public Task<PerformanceSession> RunAsync(PerformanceSessionPreset preset,
        HardwareSnapshot? hardware = null, IProgress<PerformanceProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(PerformanceSessionRequest.ForPreset(preset), hardware, progress, cancellationToken);

    public async Task<PerformanceSession> RunAsync(PerformanceSessionRequest request,
        HardwareSnapshot? hardware = null, IProgress<PerformanceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma sessão de desempenho em andamento.");
        try
        {
            return await ExecuteAsync(request, hardware, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    private async Task<PerformanceSession> ExecuteAsync(PerformanceSessionRequest request,
        HardwareSnapshot? hardware, IProgress<PerformanceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        var expected = request.ExpectedSamples;
        var map = HardwareStorageIdentityMap.From(hardware);
        var context = new PerformanceCollectionContext(id, map.PhysicalDisks, map.Volumes, limits);

        var runs = new List<CollectorRun>(collectors.Count);
        foreach (var collector in collectors)
        {
            try { runs.Add(CollectorRun.Started(collector, collector.StartSession(context), clock.GetUtcNow())); }
            catch (Exception) { runs.Add(CollectorRun.Unavailable(collector, clock.GetUtcNow())); }
        }

        var samples = new List<PerformanceSample>();
        var overhead = new List<SelfOverheadSample>();
        var sampleLimitHit = false;
        var cancelled = false;
        var missed = 0;
        var lastTickElapsed = TimeSpan.Zero;
        using var self = Process.GetCurrentProcess();
        var lastSelfCpu = self.TotalProcessorTime;
        var lastSelfElapsed = TimeSpan.Zero;
        try
        {
            var nextDue = TimeSpan.Zero;
            var jitterTolerance = TimeSpan.FromMilliseconds(Math.Min(50, request.SampleInterval.TotalMilliseconds * 0.2));
            for (var index = 0; index < expected; index++)
            {
                if (index > 0)
                {
                    nextDue += request.SampleInterval;
                    if (stopwatch.Elapsed > nextDue + jitterTolerance)
                    {
                        // Coleta anterior excedeu o intervalo: pula oportunidades perdidas em vez de
                        // disparar amostras em sequência acelerada para "recuperar" o atraso.
                        while (stopwatch.Elapsed > nextDue + jitterTolerance)
                        {
                            nextDue += request.SampleInterval;
                            missed++;
                        }
                    }
                    if (nextDue > request.Duration) break;
                    var wait = nextDue - stopwatch.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var at = clock.GetUtcNow();
                var tickElapsed = stopwatch.Elapsed;
                var interval = index == 0 ? TimeSpan.Zero : tickElapsed - lastTickElapsed;
                lastTickElapsed = tickElapsed;
                var collection = Stopwatch.StartNew();
                var reads = await Task.WhenAll(runs.Select(run =>
                    run.ReadAsync(at, interval, cancellationToken))).ConfigureAwait(false);
                collection.Stop();
                var sample = new PerformanceSample(index, at,
                    Math.Round(tickElapsed.TotalSeconds, 3), index == 0,
                    reads.Select(reading => reading.Cpu).FirstOrDefault(reading => reading is not null),
                    reads.Select(reading => reading.Memory).FirstOrDefault(reading => reading is not null),
                    reads.SelectMany(reading => reading.Processes).ToArray(),
                    reads.SelectMany(reading => reading.Exits).ToArray(),
                    reads.SelectMany(reading => reading.Storage).ToArray(),
                    collection.ElapsedMilliseconds,
                    reads.SelectMany(reading => reading.Limitations)
                        .Distinct(StringComparer.Ordinal).Take(8).ToArray());
                samples.Add(sample);
                overhead.Add(MeasureSelf(self, stopwatch.Elapsed, ref lastSelfCpu, ref lastSelfElapsed,
                    collection.ElapsedMilliseconds));
                progress?.Report(new PerformanceProgress(samples.Count, expected, stopwatch.Elapsed,
                    request.Duration, sample));
                if (samples.Count >= limits.MaxSamples &&
                    request.Duration - stopwatch.Elapsed > request.SampleInterval)
                {
                    sampleLimitHit = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            foreach (var run in runs) run.Dispose();
            stopwatch.Stop();
        }

        var completed = clock.GetUtcNow();
        var sources = runs.Select(run => run.Summarize(sampleLimitHit, cancelled, completed, stopwatch.Elapsed))
            .ToArray();
        var stats = statistics.Compute(samples, rules);
        var analysis = analyzer.Analyze(samples, stats);

        var limitations = new List<string>();
        if (cancelled) limitations.Add($"Sessão cancelada pelo usuário após {samples.Count} amostra(s).");
        if (sampleLimitHit) limitations.Add($"Limite de {limits.MaxSamples} amostras atingido durante a sessão.");
        if (missed > 0)
            limitations.Add(
                $"{missed} oportunidade(s) de amostragem perdida(s): a coleta excedeu o intervalo e as " +
                "amostras seguintes mantiveram a cadência nominal, sem rajada de recuperação.");
        foreach (var source in sources.Where(source => source.Status is PerformanceSourceStatus.Failed
                     or PerformanceSourceStatus.Unsupported))
            limitations.Add(source.CollectorId == "performance-cpu"
                ? "CPU indisponível; percentuais, fila e regras de CPU não avaliados."
                : $"Fonte {source.CollectorId} indisponível nesta sessão.");
        foreach (var source in sources.Where(source => source.LimitHit))
            limitations.Add($"Fonte {source.CollectorId}: limite interno de acompanhamento atingido; dados parciais.");
        if (samples.Count == 0) limitations.Add("Nenhuma amostra coletada; sessão sem estatísticas.");

        var execution = cancelled ? PerformanceExecutionStatus.Cancelled
            : sources.All(source => source.Status == PerformanceSourceStatus.Unsupported)
                ? PerformanceExecutionStatus.Unsupported
            : sources.All(source => source.Status == PerformanceSourceStatus.Failed)
                ? PerformanceExecutionStatus.Failed
            : PerformanceExecutionStatus.Completed;
        var unavailable = sources.Where(source => source.Status is PerformanceSourceStatus.Failed
                or PerformanceSourceStatus.Unsupported)
            .Select(source => source.CollectorId).ToArray();
        var processLimit = runs.Any(run => run.ProcessLimitHit);
        var storageLimit = runs.Any(run => run.StorageLimitHit);
        var anyPartial = sources.Any(source => source.Status == PerformanceSourceStatus.Partial);
        var complete = execution == PerformanceExecutionStatus.Completed &&
            samples.Count >= expected && !processLimit && !storageLimit && !sampleLimitHit &&
            missed == 0 && unavailable.Length == 0 && !anyPartial;
        var coverageStatus = samples.Count == 0 || execution is PerformanceExecutionStatus.Failed
                or PerformanceExecutionStatus.Unsupported
            ? PerformanceCoverageStatus.Unavailable
            : complete ? PerformanceCoverageStatus.Complete : PerformanceCoverageStatus.Partial;
        var coverage = new PerformanceCoverage(execution, coverageStatus, expected, samples.Count, missed,
            processLimit, storageLimit, sampleLimitHit, unavailable, limitations.ToArray());
        // Status legado: a execução terminou normalmente mesmo quando a cobertura é parcial. A parcialidade
        // da cobertura fica em Coverage e não é reclassificada como falha de execução.
        var status = execution switch
        {
            PerformanceExecutionStatus.Cancelled => PerformanceSessionStatus.Cancelled,
            PerformanceExecutionStatus.Failed => PerformanceSessionStatus.Failed,
            PerformanceExecutionStatus.Unsupported => PerformanceSessionStatus.Unsupported,
            _ => PerformanceSessionStatus.Completed
        };

        return new PerformanceSession(2, id, SoftwareInfo.Version, SoftwareInfo.PerformanceSchemaVersion,
            request.Preset, request.Duration, request.SampleInterval, started, completed,
            stopwatch.Elapsed, status, sources, stats, analysis.Observations, analysis.Notes,
            samples, SummarizeSelf(overhead), limitations, coverage);
    }

    private static SelfOverheadSample MeasureSelf(Process self, TimeSpan wallElapsed,
        ref TimeSpan lastCpu, ref TimeSpan lastElapsed, long collectionMilliseconds)
    {
        self.Refresh();
        var cpu = self.TotalProcessorTime;
        var wall = wallElapsed - lastElapsed;
        var percent = wall <= TimeSpan.Zero ? 0 : Math.Clamp(
            (cpu - lastCpu).TotalMilliseconds / (wall.TotalMilliseconds * Environment.ProcessorCount) * 100,
            0, 100);
        lastCpu = cpu;
        lastElapsed = wallElapsed;
        return new(Math.Round(percent, 1), (ulong)Math.Max(0, self.WorkingSet64),
            (ulong)Math.Max(0, self.PrivateMemorySize64), collectionMilliseconds);
    }

    private static SelfOverheadSummary SummarizeSelf(IReadOnlyList<SelfOverheadSample> samples) =>
        new(samples.Count,
            samples.Count == 0 ? null : Math.Round(samples.Average(item => item.CpuPercent), 1),
            samples.Count == 0 ? null : Math.Round(samples.Max(item => item.CpuPercent), 1),
            samples.Count == 0 ? null : samples.Max(item => item.WorkingSetBytes),
            samples.Count == 0 ? null : samples.Max(item => item.PrivateBytes),
            samples.Count == 0 ? null : Math.Round(samples.Average(item => item.CollectionDurationMilliseconds), 1),
            samples.Count == 0 ? null : samples.Max(item => (double)item.CollectionDurationMilliseconds));

    private void Validate(PerformanceSessionRequest request)
    {
        if (request.Duration <= TimeSpan.Zero || request.Duration > limits.MaxSessionDuration)
            throw new ArgumentException(
                $"A duração deve ser maior que zero e no máximo {limits.MaxSessionDuration.TotalMinutes:0.###} minuto(s).");
        if (request.SampleInterval < TimeSpan.FromMilliseconds(100) ||
            request.SampleInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentException("O intervalo deve ficar entre 100 ms e 5 s.");
        if (request.ExpectedSamples > limits.MaxSamples)
            throw new ArgumentException("A combinação de duração e intervalo excede o limite de amostras.");
        if (request.Preset != PerformanceSessionPreset.Custom && request.Duration !=
            PerformanceSessionRequest.ForPreset(request.Preset).Duration)
            throw new ArgumentException("A duração não corresponde ao preset informado.");
    }

    private sealed class CollectorRun : IDisposable
    {
        // Coletores sinalizam truncamento por limite com este prefixo; o runner o promove a LimitHit.
        private const string LimitPrefix = "Limite de ";
        private readonly IPerformanceCollector collector;
        private readonly IPerformanceCollectorSession? session;
        private readonly DateTimeOffset startedAt;
        private readonly List<string> limitations = [];
        private int validReads;
        private int attempts;
        private int failures;
        private int unsupported;
        private bool limitHit;
        private double readMilliseconds;
        private double maxReadMilliseconds;

        private CollectorRun(IPerformanceCollector collector, IPerformanceCollectorSession? session,
            DateTimeOffset startedAt)
        {
            this.collector = collector;
            this.session = session;
            this.startedAt = startedAt;
        }

        public bool ProcessLimitHit { get; private set; }
        public bool StorageLimitHit { get; private set; }

        public static CollectorRun Started(IPerformanceCollector collector,
            IPerformanceCollectorSession session, DateTimeOffset at) => new(collector, session, at);

        public static CollectorRun Unavailable(IPerformanceCollector collector, DateTimeOffset at) =>
            new(collector, null, at);

        public async Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset at, TimeSpan elapsed,
            CancellationToken cancellationToken)
        {
            if (session is null)
            {
                failures++;
                return PerformanceCollectorTick.Empty(collector.Id, at,
                    "Não foi possível iniciar a coleta desta fonte.");
            }
            try
            {
                var timer = Stopwatch.StartNew();
                var tick = await session.ReadAsync(at, elapsed, cancellationToken).ConfigureAwait(false);
                timer.Stop();
                attempts++;
                readMilliseconds += timer.Elapsed.TotalMilliseconds;
                maxReadMilliseconds = Math.Max(maxReadMilliseconds, timer.Elapsed.TotalMilliseconds);
                if (tick.Unsupported) unsupported++;

                var hasValidData = (tick.Cpu is { Valid: true }) ||
                    (tick.Memory is { Valid: true }) ||
                    (tick.Processes.Count > 0) ||
                    (tick.Storage.Count > 0 && tick.Storage.Any(s => s.Valid));
                if (hasValidData) validReads++;

                foreach (var limitation in tick.Limitations.Where(limitation =>
                             limitation.StartsWith(LimitPrefix, StringComparison.Ordinal)))
                {
                    limitHit = true;
                    if (limitation.Contains("processos", StringComparison.OrdinalIgnoreCase))
                        ProcessLimitHit = true;
                    if (limitation.Contains("instâncias de armazenamento", StringComparison.OrdinalIgnoreCase))
                        StorageLimitHit = true;
                }
                foreach (var limitation in tick.Limitations)
                    if (!limitations.Contains(limitation, StringComparer.Ordinal)) limitations.Add(limitation);
                return tick;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failures++;
                return PerformanceCollectorTick.Empty(collector.Id, at,
                    "Falha ao ler a fonte de desempenho.");
            }
        }

        public PerformanceSourceSummary Summarize(bool sessionLimitHit, bool cancelled, DateTimeOffset completedAt,
            TimeSpan duration)
        {
            var hit = limitHit || sessionLimitHit;
            var status = cancelled ? PerformanceSourceStatus.Cancelled
                : unsupported == attempts && attempts > 0 ? PerformanceSourceStatus.Unsupported
                : validReads == 0 ? PerformanceSourceStatus.Failed
                : validReads < attempts || failures > 0 || hit ? PerformanceSourceStatus.Partial
                : PerformanceSourceStatus.Completed;
            return new(collector.Id, collector.Version, collector.Category, collector.Source, status,
                startedAt, completedAt, duration, validReads, limitHit, limitations.ToArray())
            {
                MeanReadMilliseconds = attempts == 0 ? null : Math.Round(readMilliseconds / attempts, 3),
                MaxReadMilliseconds = attempts == 0 ? null : Math.Round(maxReadMilliseconds, 3)
            };
        }

        public void Dispose()
        {
            try { session?.Dispose(); } catch (Exception) { }
        }
    }
}
