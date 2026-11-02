using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Fonte observável de uma sessão de monitoramento. Cada implementação reutiliza um mecanismo já existente e é
/// responsável apenas pelo ciclo de leitura; persistência, orçamento e segmentos pertencem ao runner.
/// </summary>
public interface IMonitoringSource
{
    MonitoringSource Kind { get; }
    string CollectorId { get; }
    string CollectorVersion { get; }
    Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken);
}

public interface IMonitoringSourceSession : IDisposable
{
    Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Adaptador do coletor nativo de CPU e memória da Fase 3.2 para observação contínua de baixo impacto.
/// </summary>
public sealed class PerformanceMonitoringSource : IMonitoringSource
{
    private readonly IPerformanceCollector collector;

    public PerformanceMonitoringSource(IPerformanceCollector collector)
    {
        this.collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    public MonitoringSource Kind => MonitoringSource.Performance;
    public string CollectorId => collector.Id;
    public string CollectorVersion => collector.Version;

    public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new PerformanceCollectionContext(Guid.Empty, null, null);
        return Task.FromResult<IMonitoringSourceSession>(new Session(collector.StartSession(context)));
    }

    private sealed class Session : IMonitoringSourceSession
    {
        private readonly IPerformanceCollectorSession session;
        private DateTimeOffset? previousAt;
        private int index;

        public Session(IPerformanceCollectorSession session) => this.session = session;

        public async Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken)
        {
            var interval = previousAt is { } last ? timestampUtc - last : TimeSpan.Zero;
            previousAt = timestampUtc;
            var tick = await session.ReadAsync(timestampUtc, interval, cancellationToken).ConfigureAwait(false);
            if (tick.Cpu is null && tick.Memory is null)
                return MonitoringTick.NotAvailable(MonitoringSource.Performance, timestampUtc,
                    "CPU e memória não observadas neste ciclo.");

            var observation = new MonitoringPerformanceObservation(index++, timestampUtc,
                interval.TotalSeconds,
                tick.Cpu is { Valid: true } cpu ? cpu.TotalPercent : null,
                tick.Cpu is { Valid: true } validCpu ? validCpu.LogicalProcessors : null,
                tick.Memory is { Valid: true } memory ? memory.AvailablePercent : null,
                tick.Memory is { Valid: true } installed ? installed.InstalledBytes : null,
                tick.Memory is { Valid: true } available ? available.AvailableBytes : null,
                tick.Memory is { Valid: true } used ? used.UsedBytes : null,
                tick.Limitations.Distinct(StringComparer.Ordinal).ToArray());
            return new MonitoringTick(MonitoringSource.Performance, timestampUtc, false, observation, [], null,
                tick.Limitations);
        }

        public void Dispose() => session.Dispose();
    }
}

/// <summary>
/// Adaptador do leitor incremental do Log de Eventos. A classificação usa o mesmo <see cref="EventCatalog"/> da
/// Fase 3.1; nenhuma regra de classificação é reimplementada. O carimbo do ciclo é o instante informado pelo
/// runner, que também delimita o checkpoint; o horário do evento permanece em <c>OccurredUtc</c>.
/// </summary>
public sealed class EventMonitoringSource : IMonitoringSource
{
    private readonly IMonitoringEventSource source;

    public EventMonitoringSource(IMonitoringEventSource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public MonitoringSource Kind => MonitoringSource.Events;
    public string CollectorId => source.Id;
    public string CollectorVersion => source.Version;

    public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMonitoringSourceSession>(new Session(source.Start()));
    }

    private sealed class Session : IMonitoringSourceSession
    {
        private readonly IMonitoringEventProbe probe;
        private int index;

        public Session(IMonitoringEventProbe probe) => this.probe = probe;

        public async Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken)
        {
            var poll = await probe.PollAsync(cancellationToken).ConfigureAwait(false);
            var observations = new List<MonitoringEventObservation>(poll.Records.Count);
            foreach (var record in poll.Records)
            {
                var classification = EventCatalog.ClassifyEvent(record);
                observations.Add(new MonitoringEventObservation(index++, timestampUtc,
                    record.Channel, record.Provider, record.EventId, record.RecordId, record.OccurredUtc,
                    record.Level, classification.GroupId, classification.Category, classification.Severity,
                    Truncate(record.Message), record.MessageTruncated));
            }

            return new MonitoringTick(MonitoringSource.Events, timestampUtc, false, null, observations, null,
                poll.Limitations)
            {
                LimitHit = poll.LimitHit
            };
        }

        private static string? Truncate(string? message) =>
            message is null || message.Length <= 600 ? message : message[..600] + "…";

        public void Dispose() => probe.Dispose();
    }
}

/// <summary>
/// Adaptador das tabelas locais de rede. Reutiliza os coletores da Fase 3.3A e reduz o resultado a contagens
/// agregadas: nenhum endereço IP, porta remota detalhada ou conteúdo de tráfego é preservado. Uma leitura em que
/// as tabelas falharam por completo é declarada indisponível, nunca convertida em zeros.
/// </summary>
public sealed class NetworkMonitoringSource : IMonitoringSource
{
    private readonly IReadOnlyList<INetworkSampleCollector> collectors;

    public NetworkMonitoringSource(IReadOnlyList<INetworkSampleCollector> collectors)
    {
        this.collectors = collectors?.ToArray() ?? throw new ArgumentNullException(nameof(collectors));
        if (this.collectors.Count == 0)
            throw new ArgumentException("A fonte de rede exige ao menos um coletor de amostra.", nameof(collectors));
    }

    public MonitoringSource Kind => MonitoringSource.Network;
    public string CollectorId => "monitoring-network";
    public string CollectorVersion => "1.0.0";

    public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMonitoringSourceSession>(new Session(collectors));
    }

    private sealed class Session : IMonitoringSourceSession
    {
        private readonly IReadOnlyList<INetworkSampleCollector> collectors;
        private readonly NetworkCollectionContext context = new(Guid.Empty, new NetworkLimits());
        private int index;

        public Session(IReadOnlyList<INetworkSampleCollector> collectors) => this.collectors = collectors;

        public async Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken)
        {
            var endpoints = new List<NetworkEndpoint>();
            var limitations = new List<string>();
            var unsupported = 0;
            var endpointSourceAvailable = true;
            foreach (var collector in collectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var reading = await collector.ReadAsync(context, timestampUtc, cancellationToken)
                        .ConfigureAwait(false);
                    if (reading.Unsupported) unsupported++;
                    if (collector.Category == NetworkSourceCategory.Endpoints && reading.Unsupported)
                        endpointSourceAvailable = false;
                    endpoints.AddRange(reading.Endpoints);
                    limitations.AddRange(reading.Limitations);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    if (collector.Category == NetworkSourceCategory.Endpoints)
                        endpointSourceAvailable = false;
                    limitations.Add($"Coletor de rede {collector.Id} indisponível ({error.GetType().Name}).");
                }
            }

            var incomplete = unsupported > 0 || limitations.Any(limitation =>
                limitation.Contains("incompleta", StringComparison.OrdinalIgnoreCase) ||
                limitation.Contains("não atômica", StringComparison.OrdinalIgnoreCase));
            if (!endpointSourceAvailable ||
                endpoints.Count == 0 && (unsupported == collectors.Count || incomplete))
                return MonitoringTick.NotAvailable(MonitoringSource.Network, timestampUtc,
                    "Tabelas locais de endpoints indisponíveis ou incompletas nesta leitura; nenhuma contagem " +
                    "foi inventada.");

            var tcp = endpoints.Where(item => item.Protocol == NetworkProtocol.Tcp).ToArray();
            var observation = new MonitoringNetworkObservation(index++, timestampUtc,
                tcp.Length, endpoints.Count - tcp.Length,
                tcp.Count(item => item.TcpState == TcpConnectionState.Listen),
                tcp.Count(item => item.TcpState == TcpConnectionState.Established),
                endpoints.Select(item => item.LocalPort).Distinct().Count(),
                endpoints.Where(item => item.Pid > 0).Select(item => item.Pid).Distinct().Count(),
                incomplete,
                [MonitoringGuidance.NetworkPrivacyLimitation]);

            return new MonitoringTick(MonitoringSource.Network, timestampUtc, false, null, [], observation,
                [.. limitations.Distinct(StringComparer.Ordinal), MonitoringGuidance.NetworkPrivacyLimitation]);
        }

        public void Dispose() { }
    }
}
