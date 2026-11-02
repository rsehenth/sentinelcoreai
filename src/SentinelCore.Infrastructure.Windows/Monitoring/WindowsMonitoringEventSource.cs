using System.Diagnostics.Eventing.Reader;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

/// <summary>
/// Leitura incremental do Log de Eventos para sessões de monitoramento. Diferente da investigação de período da
/// Fase 3.1, cada ciclo consulta somente registros com <c>EventRecordID</c> acima da marca d'água do canal, nos
/// níveis 1–3. A primeira leitura apenas posiciona a marca d'água: eventos anteriores ao início da sessão não
/// são recuperados e nenhum canal é habilitado, alterado ou limpo.
/// </summary>
internal sealed class WindowsMonitoringEventSource : IMonitoringEventSource
{
    private static readonly string[] Channels =
    [
        "System",
        "Application",
        "Microsoft-Windows-DriverFrameworks-UserMode/Operational",
        "Microsoft-Windows-Diagnostics-Performance/Operational"
    ];

    private readonly int maxRecordsPerPoll;
    private readonly int maxMessageChars;

    public WindowsMonitoringEventSource(int maxRecordsPerPoll = 40, int maxMessageChars = 600)
    {
        this.maxRecordsPerPoll = Math.Max(1, maxRecordsPerPoll);
        this.maxMessageChars = Math.Max(120, maxMessageChars);
    }

    public string Id => "monitoring-events";
    public string Version => "1.0.0";
    public string Source => "Windows Event Log incremental (EventRecordID; leitura nativa)";

    public IMonitoringEventProbe Start() =>
        new Probe(Id, Version, Source, Channels, maxRecordsPerPoll, maxMessageChars);

    private sealed class Probe : IMonitoringEventProbe
    {
        private static readonly string LevelFilter = "(Level=1 or Level=2 or Level=3)";
        private readonly string id;
        private readonly string version;
        private readonly string source;
        private readonly string[] channels;
        private readonly int perPollLimit;
        private readonly int maxMessageChars;
        private readonly Dictionary<string, long> watermarks = new(StringComparer.OrdinalIgnoreCase);
        private EventLogSession? session;
        private bool initialized;

        public Probe(string id, string version, string source, string[] channels, int perPollLimit,
            int maxMessageChars)
        {
            this.id = id;
            this.version = version;
            this.source = source;
            this.channels = channels;
            this.perPollLimit = perPollLimit;
            this.maxMessageChars = maxMessageChars;
        }

        public Task<MonitoringEventPoll> PollAsync(CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            var records = new List<RawEventRecord>();
            var limitations = new List<string>();
            var limitHit = false;
            var truncated = 0;
            var unavailableMessages = 0;

            if (!EnsureSession(limitations))
                return new MonitoringEventPoll(started, [], false, limitations);

            if (!initialized)
            {
                initialized = true;
                foreach (var channel in channels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        watermarks[channel] = ReadWatermark(channel);
                    }
                    catch (Exception error) when (error is EventLogException or InvalidOperationException
                        or UnauthorizedAccessException or ArgumentException)
                    {
                        limitations.Add($"Canal {channel}: marca d'água inicial indisponível " +
                            $"({error.GetType().Name}); eventos desse canal não serão observados nesta sessão.");
                    }
                }
                limitations.Add("Primeira leitura posicionou a marca d'água de cada canal: eventos anteriores ao " +
                    "início da sessão não são recuperados pelo monitoramento.");
                return new MonitoringEventPoll(started, [], false,
                    limitations.Distinct(StringComparer.Ordinal).ToArray());
            }

            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (records.Count >= perPollLimit)
                {
                    limitHit = true;
                    limitations.Add($"Limite global de {perPollLimit} evento(s) por ciclo atingido; os demais " +
                        "serão lidos no próximo ciclo.");
                    break;
                }
                if (!watermarks.TryGetValue(channel, out var watermark)) continue;
                try
                {
                    var query = new EventLogQuery(channel, PathType.LogName,
                        $"*[System[{LevelFilter} and (EventRecordID > {watermark})]]")
                    {
                        TolerateQueryErrors = true
                    };
                    using var reader = new EventLogReader(query);
                    var highest = watermark;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (records.Count >= perPollLimit)
                        {
                            limitHit = true;
                            limitations.Add($"Limite global de {perPollLimit} evento(s) por ciclo atingido; os " +
                                "demais serão lidos no próximo ciclo.");
                            break;
                        }

                        EventRecord? record;
                        try
                        {
                            record = reader.ReadEvent();
                        }
                        catch (Exception error) when (error is EventLogException or InvalidOperationException)
                        {
                            limitations.Add($"Canal {channel}: falha de leitura ({error.GetType().Name}); os " +
                                "eventos seguintes serão tentados no próximo ciclo.");
                            break;
                        }

                        if (record is null) break;
                        using (record)
                        {
                            RawEventRecord? mapped;
                            try
                            {
                                mapped = EventRecordMapper.Map(record, channel, maxMessageChars,
                                    out var messageTruncated, out var messageUnavailable);
                                if (messageTruncated) truncated++;
                                if (messageUnavailable) unavailableMessages++;
                            }
                            catch (Exception error) when (error is not OperationCanceledException)
                            {
                                limitations.Add($"Canal {channel}: registro não convertido ({error.GetType().Name}).");
                                if (record.RecordId is { } skipped && skipped > highest) highest = skipped;
                                continue;
                            }

                            if (record.RecordId is { } recordId && recordId > highest) highest = recordId;
                            if (mapped is null)
                            {
                                limitations.Add($"Canal {channel}: registro sem horário válido foi excluído.");
                                continue;
                            }

                            records.Add(mapped);
                        }
                    }

                    watermarks[channel] = highest;
                }
                catch (UnauthorizedAccessException)
                {
                    limitations.Add($"Canal {channel}: acesso negado; nenhuma elevação foi solicitada.");
                }
                catch (Exception error) when (error is EventLogException or ArgumentException or InvalidOperationException)
                {
                    limitations.Add($"Canal {channel}: consulta incremental indisponível ({error.GetType().Name}).");
                }
            }

            if (truncated > 0)
                limitations.Add($"{truncated} mensagem(ns) excederam {maxMessageChars} caracteres e foram " +
                    "truncadas; os identificadores técnicos foram preservados.");
            if (unavailableMessages > 0)
                limitations.Add($"{unavailableMessages} registro(s) não possuíam mensagem formatada disponível.");

            return new MonitoringEventPoll(started, records, limitHit,
                limitations.Distinct(StringComparer.Ordinal).ToArray());
        }, cancellationToken);

        private bool EnsureSession(List<string> limitations)
        {
            if (session is not null) return true;
            try
            {
                session = new EventLogSession();
                return true;
            }
            catch (Exception error)
            {
                limitations.Add($"Log de eventos indisponível ({error.GetType().Name}): {error.Message}");
                return false;
            }
        }

        private long ReadWatermark(string channel)
        {
            var query = new EventLogQuery(channel, PathType.LogName, $"*[System[{LevelFilter}]]")
            {
                TolerateQueryErrors = true,
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return record?.RecordId ?? 0;
        }

        public void Dispose()
        {
            session?.Dispose();
            session = null;
            watermarks.Clear();
        }
    }
}
