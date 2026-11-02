using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed class WindowsEventLogCollector(InvestigationLimits? limits = null) : IEventEvidenceCollector
{
    private static readonly string[] Channels =
    [
        "System",
        "Application",
        "Microsoft-Windows-Kernel-Power/Operational",
        "Microsoft-Windows-Kernel-PnP/Configuration",
        "Microsoft-Windows-DriverFrameworks-UserMode/Operational",
        "Microsoft-Windows-Storage-StorPort/Operational",
        "Microsoft-Windows-Ntfs/Operational",
        "Microsoft-Windows-TCPIP/Operational",
        "Microsoft-Windows-NDIS/Operational",
        "Microsoft-Windows-Diagnostics-Performance/Operational",
        "Microsoft-Windows-ResourceExhaustionDetector/Operational",
        "Microsoft-Windows-Power-Troubleshooter/Operational",
        "Microsoft-Windows-WER-SystemErrorReporting/Operational"
    ];

    private static readonly HashSet<string> Required = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Application"
    };

    private readonly InvestigationLimits limits = limits ?? InvestigationLimits.Default;

    public string Id => "event-log";
    public string Version => "1.0.0";
    public string Source => "Windows Event Log (leitura nativa)";

    public Task<EventCollectorResult> CollectAsync(Guid investigationId, EventQueryWindow window,
        CancellationToken cancellationToken) => Task.Run(() => Collect(window, cancellationToken), cancellationToken);

    private EventCollectorResult Collect(EventQueryWindow window, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var records = new List<RawEventRecord>();
        var channels = new List<EventChannelState>();
        var limitations = new List<string>();
        var limitHit = false;
        var messageTruncated = 0;
        var messageUnavailable = 0;

        EventLogSession session;
        try
        {
            session = new EventLogSession();
        }
        catch (Exception error)
        {
            return new(Id, Version, Source, started, DateTimeOffset.UtcNow, timer.Elapsed,
                EventSourceStatus.Unsupported, 0, 0, false, [], [],
                Channels.Select(channel => new EventChannelState(channel, false, false, false,
                    "Serviço de log de eventos indisponível.")).ToArray(),
                [$"Log de eventos indisponível ({error.GetType().Name}): {error.Message}"]);
        }

        using (session)
        {
            foreach (var channel in Channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed > limits.CollectionTime)
                {
                    limitHit = true;
                    limitations.Add("Tempo máximo de coleta atingido; a contagem é parcial.");
                    break;
                }

                EventLogConfiguration configuration;
                try
                {
                    configuration = new EventLogConfiguration(channel, session);
                }
                catch (EventLogNotFoundException)
                {
                    channels.Add(new(channel, false, false, false, "Canal ausente neste sistema."));
                    if (Required.Contains(channel))
                        limitations.Add($"Canal obrigatório ausente: {channel}.");
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    channels.Add(new(channel, true, false, false, "Acesso negado; elevação não solicitada."));
                    limitations.Add($"Acesso negado ao canal {channel}; nenhuma elevação foi solicitada.");
                    continue;
                }
                catch (Exception error) when (error is EventLogException or ArgumentException)
                {
                    channels.Add(new(channel, true, false, false, $"Estado indisponível ({error.GetType().Name})."));
                    limitations.Add($"Estado do canal {channel} indisponível ({error.GetType().Name}): {error.Message}");
                    continue;
                }

                if (!configuration.IsEnabled)
                {
                    channels.Add(new(channel, true, false, true, "Canal desabilitado; não foi habilitado."));
                    if (Required.Contains(channel))
                        limitations.Add($"Canal obrigatório desabilitado: {channel}.");
                    continue;
                }

                channels.Add(new(channel, true, true, true, null));
                try
                {
                    var query = new EventLogQuery(channel, PathType.LogName, EventQuery.XPath(window))
                    {
                        TolerateQueryErrors = true,
                        ReverseDirection = true
                    };
                    using var reader = new EventLogReader(query);
                    var perChannel = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (records.Count >= limits.MaxTotalEvents)
                        {
                            limitHit = true;
                            limitations.Add($"Limite total de {limits.MaxTotalEvents} eventos atingido; a contagem é parcial.");
                            break;
                        }
                        if (perChannel >= limits.MaxEventsPerChannel)
                        {
                            limitHit = true;
                            limitations.Add($"Limite de {limits.MaxEventsPerChannel} eventos no canal {channel}; a contagem é parcial.");
                            break;
                        }
                        if (timer.Elapsed > limits.CollectionTime)
                        {
                            limitHit = true;
                            limitations.Add("Tempo máximo de coleta atingido; a contagem é parcial.");
                            break;
                        }

                        EventRecord? record;
                        try
                        {
                            record = reader.ReadEvent();
                        }
                        catch (Exception error) when (error is EventLogException or InvalidOperationException)
                        {
                            limitations.Add($"Falha ao ler o canal {channel} ({error.GetType().Name}): {error.Message}");
                            break;
                        }
                        if (record is null) break;

                        using (record)
                        {
                            RawEventRecord? mapped;
                            try
                            {
                                mapped = EventRecordMapper.Map(record, channel, limits.MaxMessageChars,
                                    out var truncated, out var unavailable);
                                if (truncated) messageTruncated++;
                                if (unavailable) messageUnavailable++;
                            }
                            catch (Exception error) when (error is not OperationCanceledException)
                            {
                                limitations.Add($"Registro do canal {channel} não recuperado ({error.GetType().Name}): {error.Message}");
                                continue;
                            }
                            if (mapped is null)
                            {
                                limitations.Add($"Registro do canal {channel} sem horário válido foi excluído.");
                                continue;
                            }
                            records.Add(mapped);
                            perChannel++;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    limitations.Add($"Acesso negado durante a consulta do canal {channel}; nenhuma elevação foi solicitada.");
                }
                catch (Exception error) when (error is EventLogException or ArgumentException or InvalidOperationException)
                {
                    limitations.Add($"Consulta do canal {channel} falhou ({error.GetType().Name}): {error.Message}");
                }
            }
        }

        if (messageTruncated > 0)
            limitations.Add($"{messageTruncated} mensagem(ns) formatada(s) excederam {limits.MaxMessageChars} caracteres e foram truncadas.");
        if (messageUnavailable > 0)
            limitations.Add($"{messageUnavailable} registro(s) não possuíam mensagem formatada disponível; os identificadores técnicos foram preservados.");

        var status = limitations.Count == 0 && !limitHit ? EventSourceStatus.Completed : EventSourceStatus.Partial;
        return new(Id, Version, Source, started, DateTimeOffset.UtcNow, timer.Elapsed, status,
            records.Count, 0, limitHit, records, [], channels,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

}
