using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Executa uma sessão de diagnóstico de rede somente leitura. Três presets: instantâneo,
/// 30 s e 1 min. A cadência usa grade absoluta com tolerância de jitter e relógio monotônico
/// (<see cref="Stopwatch"/>); atrasos reduzem a cobertura em vez de gerar rajada de recuperação.
/// Uma única sessão pode estar ativa por instância.
/// </summary>
public sealed class NetworkSessionRunner
{
    private readonly IReadOnlyList<INetworkSampleCollector> sampleCollectors;
    private readonly IReadOnlyList<INetworkFactsCollector> factsCollectors;
    private readonly NetworkStatisticsEngine statistics = new();
    private readonly NetworkEvidenceAnalyzer analyzer;
    private readonly NetworkLimits limits;
    private readonly TimeProvider clock;
    private int running;

    public NetworkSessionRunner(IEnumerable<INetworkSampleCollector> sampleCollectors,
        IEnumerable<INetworkFactsCollector>? factsCollectors = null, TimeProvider? clock = null,
        NetworkLimits? limits = null, NetworkRuleOptions? rules = null)
    {
        this.sampleCollectors = sampleCollectors.ToArray();
        if (this.sampleCollectors.Count == 0 ||
            this.sampleCollectors.Select(collector => collector.Id).Distinct(StringComparer.Ordinal).Count() !=
            this.sampleCollectors.Count)
            throw new ArgumentException(
                "Coletores de rede devem ter identificadores únicos e não podem estar vazios.",
                nameof(sampleCollectors));
        this.factsCollectors = (factsCollectors ?? []).ToArray();
        if (this.factsCollectors.Select(collector => collector.Id).Distinct(StringComparer.Ordinal).Count() !=
            this.factsCollectors.Count)
            throw new ArgumentException("Coletores de fatos de rede devem ter identificadores únicos.",
                nameof(factsCollectors));
        this.clock = clock ?? TimeProvider.System;
        this.limits = limits ?? NetworkLimits.Default;
        analyzer = new(rules ?? NetworkRuleOptions.Default);
    }

    public Task<NetworkSession> RunAsync(NetworkSessionPreset preset,
        EventInvestigation? events = null, IProgress<NetworkProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(NetworkSessionRequest.ForPreset(preset), events, progress, cancellationToken);

    public async Task<NetworkSession> RunAsync(NetworkSessionRequest request,
        EventInvestigation? events = null, IProgress<NetworkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma sessão de rede em andamento.");
        try
        {
            return await ExecuteAsync(request, events, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    private async Task<NetworkSession> ExecuteAsync(NetworkSessionRequest request, EventInvestigation? events,
        IProgress<NetworkProgress>? progress, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        var context = new NetworkCollectionContext(id, limits);
        var expected = request.ExpectedSamples;
        var snapshot = request.Preset == NetworkSessionPreset.Snapshot;

        // Fatos são lidos uma única vez por sessão: intervalo dinâmico e presença de famílias não
        // mudam dentro de trinta segundos e repetir a leitura não acrescentaria evidência.
        var factsRuns = new List<FactsRun>(factsCollectors.Count);
        foreach (var collector in factsCollectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            factsRuns.Add(await FactsRun.ReadAsync(collector, context, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false));
        }
        var portRanges = factsRuns.SelectMany(run => run.Reading?.PortRanges ?? [])
            .GroupBy(range => (range.Protocol, range.Family)).Select(group => group.First())
            .OrderBy(range => range.Protocol).ThenBy(range => range.Family).ToArray();
        var adapters = factsRuns.SelectMany(run => run.Reading?.Adapters ?? [])
            .OrderBy(adapter => adapter.Id, StringComparer.Ordinal).ToArray();

        var runs = new List<SampleRun>(sampleCollectors.Count);
        foreach (var collector in sampleCollectors)
            runs.Add(new SampleRun(collector, clock.GetUtcNow()));

        var samples = new List<NetworkSampleAggregate>(expected);
        var overhead = new List<SelfOverheadSample>(expected);
        IReadOnlyList<NetworkEndpoint> lastEndpoints = [];
        NetworkTcpStatistics? tcpStatistics = null;
        NetworkUdpStatistics? udpStatistics = null;
        var sampleLimitHit = false;
        var cancelled = false;
        var missed = 0;
        using var self = Process.GetCurrentProcess();
        var lastSelfCpu = self.TotalProcessorTime;
        var lastSelfElapsed = TimeSpan.Zero;
        try
        {
            var nextDue = TimeSpan.Zero;
            var jitterTolerance = TimeSpan.FromMilliseconds(
                Math.Min(50, Math.Max(0, request.SampleInterval.TotalMilliseconds * 0.2)));
            for (var index = 0; index < expected; index++)
            {
                if (index > 0)
                {
                    nextDue += request.SampleInterval;
                    if (stopwatch.Elapsed > nextDue + jitterTolerance)
                    {
                        // Coleta anterior excedeu a grade: as oportunidades perdidas são contabilizadas e
                        // descartadas, sem rajada de recuperação que distorceria a leitura.
                        while (stopwatch.Elapsed > nextDue + jitterTolerance)
                        {
                            nextDue += request.SampleInterval;
                            missed++;
                        }
                    }
                    if (request.Duration > TimeSpan.Zero && nextDue > request.Duration) break;
                    var wait = nextDue - stopwatch.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var at = clock.GetUtcNow();
                var tickElapsed = stopwatch.Elapsed;
                var collection = Stopwatch.StartNew();
                var reads = await Task.WhenAll(runs.Select(run => run.ReadAsync(context, at, cancellationToken)))
                    .ConfigureAwait(false);
                collection.Stop();

                var endpoints = reads.Where(reading => !reading.Unsupported)
                    .SelectMany(reading => reading.Endpoints).ToArray();
                if (!snapshot || index == 0)
                {
                    tcpStatistics ??= reads.Select(reading => reading.TcpStatistics)
                        .FirstOrDefault(reading => reading is not null);
                    udpStatistics ??= reads.Select(reading => reading.UdpStatistics)
                        .FirstOrDefault(reading => reading is not null);
                    lastEndpoints = endpoints;
                }

                var sample = NetworkSampleBuilder.Build(index, at, tickElapsed.TotalSeconds, index == 0,
                    endpoints,
                    reads.SelectMany(reading => reading.Processes).ToArray(),
                    portRanges,
                    reads.Select(reading => reading.TcpStatistics?.AttemptFails).FirstOrDefault(value => value is not null),
                    reads.Sum(reading => reading.DuplicateRowsIgnored),
                    collection.ElapsedMilliseconds,
                    reads.SelectMany(reading => reading.Limitations).ToArray(),
                    limits);
                samples.Add(sample);
                overhead.Add(MeasureSelf(self, tickElapsed, ref lastSelfCpu, ref lastSelfElapsed,
                    collection.ElapsedMilliseconds));
                progress?.Report(new NetworkProgress(samples.Count, expected, tickElapsed, request.Duration, sample));

                if (samples.Count >= limits.MaxSamples && index + 1 < expected)
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
            stopwatch.Stop();
        }

        var completed = clock.GetUtcNow();
        var sources = runs.Select(run => run.Summarize(cancelled, completed, stopwatch.Elapsed)).ToArray();
        var summary = statistics.Compute(samples, limits);
        var analysis = analyzer.Analyze(samples, portRanges, (int)request.Duration.TotalSeconds);
        var details = PrepareDetails(lastEndpoints, request.IncludeAddresses, out var detailLimitHit);

        var limitations = new List<string>();
        if (cancelled) limitations.Add($"Sessão cancelada pelo usuário após {samples.Count} amostra(s).");
        if (sampleLimitHit)
            limitations.Add($"Limite de {limits.MaxSamples} amostras atingido antes do fim da janela.");
        if (missed > 0)
            limitations.Add($"{missed} oportunidade(s) de amostragem perdida(s): a coleta excedeu o intervalo e " +
                "a cadência nominal foi mantida, sem rajada de recuperação.");
        if (detailLimitHit)
            limitations.Add($"Detalhes de endpoint limitados a {limits.MaxPersistedEndpoints} linhas da última amostra.");
        foreach (var facts in factsRuns.Where(facts => facts.Reading is null))
            limitations.Add($"Fonte {facts.CollectorId} indisponível nesta sessão.");
        foreach (var source in sources.Where(source => source.Status is NetworkSourceStatus.Failed
                     or NetworkSourceStatus.Unsupported))
            limitations.Add($"Fonte {source.CollectorId} indisponível; a cobertura da sessão é parcial.");
        if (samples.Count == 0) limitations.Add("Nenhuma amostra coletada; sessão sem estatísticas.");

        var allUnsupported = sources.All(source => source.Status == NetworkSourceStatus.Unsupported) &&
            factsRuns.All(facts => facts.Reading is null or { Unsupported: true });
        var allFailed = sources.All(source => source.Status == NetworkSourceStatus.Failed);
        var execution = cancelled ? NetworkExecutionStatus.Cancelled
            : allUnsupported ? NetworkExecutionStatus.Unsupported
            : allFailed ? NetworkExecutionStatus.Failed
            : NetworkExecutionStatus.Completed;

        var unavailable = sources.Where(source => source.Status is NetworkSourceStatus.Failed
                or NetworkSourceStatus.Unsupported)
            .Select(source => source.CollectorId)
            .Concat(factsRuns.Where(facts => facts.Reading is null or { Unsupported: true })
                .Select(facts => facts.CollectorId))
            .Distinct(StringComparer.Ordinal).ToArray();
        var anyPartial = sources.Any(source => source.Status == NetworkSourceStatus.Partial);
        var complete = execution == NetworkExecutionStatus.Completed && samples.Count >= expected &&
            !sampleLimitHit && missed == 0 && unavailable.Length == 0 && !anyPartial && !detailLimitHit;
        var coverageStatus = samples.Count == 0 || execution is NetworkExecutionStatus.Failed
                or NetworkExecutionStatus.Unsupported
            ? NetworkCoverageStatus.Unavailable
            : complete ? NetworkCoverageStatus.Complete : NetworkCoverageStatus.Partial;
        var coverage = new NetworkCoverage(execution, coverageStatus, expected, samples.Count, missed,
            sampleLimitHit, detailLimitHit, portRanges.Length == 0, unavailable, limitations.ToArray());

        var status = execution switch
        {
            NetworkExecutionStatus.Cancelled => NetworkSessionStatus.Cancelled,
            NetworkExecutionStatus.Failed => NetworkSessionStatus.Failed,
            NetworkExecutionStatus.Unsupported => NetworkSessionStatus.Unsupported,
            _ => complete ? NetworkSessionStatus.Completed : NetworkSessionStatus.Partial
        };

        return new NetworkSession(1, id, SoftwareInfo.Version, SoftwareInfo.NetworkSchemaVersion,
            request.Preset, request.Duration, request.SampleInterval, started, completed, stopwatch.Elapsed,
            status, details.Count > 0, request.IncludeAddresses, sources, portRanges, adapters,
            tcpStatistics, udpStatistics, summary, analysis.Observations, analysis.Notes, samples,
            details, SummarizeSelf(overhead), BuildTcpipContext(events), limitations.ToArray(), coverage);
    }

    /// <summary>
    /// Detalhes são derivados exclusivamente da última amostra, ordenados de forma determinística e
    /// truncados no limite. Sem autorização explícita, nenhum endereço é preservado.
    /// </summary>
    private IReadOnlyList<NetworkEndpoint> PrepareDetails(IReadOnlyList<NetworkEndpoint> endpoints,
        bool includeAddresses, out bool truncated)
    {
        truncated = endpoints.Count > limits.MaxPersistedEndpoints;
        return endpoints
            .OrderBy(endpoint => endpoint.Protocol)
            .ThenBy(endpoint => endpoint.Family)
            .ThenBy(endpoint => endpoint.LocalPort)
            .ThenBy(endpoint => endpoint.Pid)
            .ThenBy(endpoint => endpoint.RemotePort ?? 0)
            .Take(Math.Max(0, limits.MaxPersistedEndpoints))
            .Select(endpoint => includeAddresses ? endpoint
                : endpoint with { LocalAddress = null, RemoteAddress = null })
            .ToArray();
    }

    /// <summary>
    /// Converte uma investigação de eventos já persistida em contexto de transporte. A presença de
    /// eventos Tcpip comprova que o sistema registrou o fato, não que a sessão de rede o reproduziu.
    /// </summary>
    internal static NetworkTcpipEventContext? BuildTcpipContext(EventInvestigation? investigation)
    {
        if (investigation is null) return null;
        var tcpip = investigation.Groups.Where(group =>
                string.Equals(group.Provider, "Tcpip", StringComparison.OrdinalIgnoreCase))
            .Where(group => group.EventId is 4231 or 4266).ToArray();
        if (tcpip.Length == 0)
            return new(investigation.Id, 0, 0, null, null,
                "A investigação de eventos associada não contém eventos Tcpip 4231/4266.");
        var ephemeral = tcpip.Where(group => group.EventId == 4231).ToArray();
        var attempts = tcpip.Where(group => group.EventId == 4266).ToArray();
        var first = tcpip.Select(group => group.FirstObservedUtc).Where(value => value is not null).Min();
        var last = tcpip.Select(group => group.LastObservedUtc).Where(value => value is not null).Max();
        return new(investigation.Id,
            attempts.Sum(group => group.EventRecordCount),
            ephemeral.Sum(group => group.EventRecordCount),
            first, last,
            "Eventos Tcpip indicam que o sistema registrou falha de alocação de porta dinâmica ou limite de "
            + "tentativas de conexão; isso não comprova esgotamento na janela desta sessão nem atribui causa.");
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

    private void Validate(NetworkSessionRequest request)
    {
        if (request.Duration < TimeSpan.Zero || request.Duration > limits.MaxSessionDuration)
            throw new ArgumentException(
                $"A duração deve estar entre zero e {limits.MaxSessionDuration.TotalMinutes:0.###} minuto(s).");
        if (request.Preset == NetworkSessionPreset.Snapshot)
        {
            if (request.Duration != TimeSpan.Zero || request.SampleInterval != TimeSpan.Zero)
                throw new ArgumentException("O instantâneo não admite duração nem intervalo.");
        }
        else
        {
            if (request.Duration <= TimeSpan.Zero)
                throw new ArgumentException("A duração deve ser maior que zero.");
            if (request.SampleInterval < TimeSpan.FromMilliseconds(100) ||
                request.SampleInterval > TimeSpan.FromSeconds(5))
                throw new ArgumentException("O intervalo deve ficar entre 100 ms e 5 s.");
            if (request.Duration != NetworkSessionRequest.ForPreset(request.Preset).Duration)
                throw new ArgumentException("A duração não corresponde ao preset informado.");
        }
        if (request.ExpectedSamples > limits.MaxSamples)
            throw new ArgumentException("A combinação de duração e intervalo excede o limite de amostras.");
    }

    private sealed class SampleRun
    {
        private readonly INetworkSampleCollector collector;
        private readonly DateTimeOffset startedAt;
        private readonly List<string> limitations = [];
        private int attempts;
        private int failures;
        private int unsupported;
        private int validReads;
        private double readMilliseconds;
        private double maxReadMilliseconds;

        public SampleRun(INetworkSampleCollector collector, DateTimeOffset startedAt)
        {
            this.collector = collector;
            this.startedAt = startedAt;
        }

        public async Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context,
            DateTimeOffset at, CancellationToken cancellationToken)
        {
            try
            {
                var timer = Stopwatch.StartNew();
                var reading = await collector.ReadAsync(context, at, cancellationToken).ConfigureAwait(false);
                timer.Stop();
                attempts++;
                readMilliseconds += timer.Elapsed.TotalMilliseconds;
                maxReadMilliseconds = Math.Max(maxReadMilliseconds, timer.Elapsed.TotalMilliseconds);
                if (reading.Unsupported) unsupported++;
                if (reading.Endpoints.Count > 0 || reading.TcpStatistics is not null ||
                    reading.UdpStatistics is not null) validReads++;
                foreach (var limitation in reading.Limitations)
                    if (!limitations.Contains(limitation, StringComparer.Ordinal)) limitations.Add(limitation);
                return reading;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                attempts++;
                failures++;
                return NetworkSampleReading.Empty(collector.Id,
                    "Falha ao ler a fonte de rede; a amostra não contém dados desta fonte.");
            }
        }

        public NetworkSourceSummary Summarize(bool cancelled, DateTimeOffset completedAt, TimeSpan duration)
        {
            var status = cancelled ? NetworkSourceStatus.Cancelled
                : attempts > 0 && unsupported == attempts ? NetworkSourceStatus.Unsupported
                : validReads == 0 ? NetworkSourceStatus.Failed
                : failures > 0 || validReads < attempts ? NetworkSourceStatus.Partial
                : NetworkSourceStatus.Completed;
            return new NetworkSourceSummary(collector.Id, collector.Version, collector.Category,
                collector.Source, status, startedAt, completedAt, duration, validReads, false,
                limitations.ToArray())
            {
                MeanReadMilliseconds = attempts == 0 ? null : Math.Round(readMilliseconds / attempts, 3),
                MaxReadMilliseconds = attempts == 0 ? null : Math.Round(maxReadMilliseconds, 3)
            };
        }
    }

    private sealed class FactsRun
    {
        private FactsRun(INetworkFactsCollector collector, DateTimeOffset startedAt, NetworkFactsReading? reading)
        {
            CollectorId = collector.Id;
            StartedAt = startedAt;
            Reading = reading;
        }

        public string CollectorId { get; }
        public DateTimeOffset StartedAt { get; }
        public NetworkFactsReading? Reading { get; }

        public static async Task<FactsRun> ReadAsync(INetworkFactsCollector collector,
            NetworkCollectionContext context, DateTimeOffset at, CancellationToken cancellationToken)
        {
            try
            {
                return new FactsRun(collector, at,
                    await collector.ReadAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return new FactsRun(collector, at, null); }
        }
    }
}
