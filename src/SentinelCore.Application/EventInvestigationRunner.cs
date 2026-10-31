using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class EventInvestigationRunner
{
    private readonly IReadOnlyList<IEventEvidenceCollector> collectors;
    private readonly TimeProvider clock;

    public EventInvestigationRunner(IEnumerable<IEventEvidenceCollector> collectors, TimeProvider? clock = null)
    {
        this.collectors = collectors.ToArray();
        if (this.collectors.Count == 0 ||
            this.collectors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != this.collectors.Count)
            throw new ArgumentException("Coletores de eventos devem ter identificadores únicos e não podem estar vazios.",
                nameof(collectors));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<EventInvestigation> RunAsync(EventWindow window, DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null, IProgress<EventInvestigationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var query = EventWindows.Resolve(window, clock.GetUtcNow(), fromUtc, toUtc);
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var duration = Stopwatch.StartNew();
        var sources = new List<EventSourceSummary>();
        var rawEvents = new List<RawEventRecord>();
        var rawReports = new List<RawWerReport>();
        var channels = new List<EventChannelState>();

        for (var index = 0; index < collectors.Count; index++)
        {
            var collector = collectors[index];
            var sourceStart = clock.GetUtcNow();
            var timer = Stopwatch.StartNew();
            EventCollectorResult result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(collector.Id, EventSourceStatus.Running, index, collectors.Count));
                result = await collector.CollectAsync(id, query, cancellationToken).ConfigureAwait(false);
                Validate(collector, result);
                if (result.Status == EventSourceStatus.Completed && (result.Limitations.Count > 0 || result.LimitHit))
                {
                    var convertedLimitations = result.Limitations.ToList();
                    if (result.LimitHit && convertedLimitations.Count == 0)
                        convertedLimitations.Add("Limite de coleta atingido; a contagem é parcial.");
                    result = result with { Status = EventSourceStatus.Partial, Limitations = convertedLimitations };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new(collector.Id, collector.Version, collector.Source, sourceStart, clock.GetUtcNow(),
                    timer.Elapsed, EventSourceStatus.Cancelled, 0, 0, false, [], [], [],
                    ["Coleta cancelada pelo usuário."]);
            }
            catch (Exception error)
            {
                result = new(collector.Id, collector.Version, collector.Source, sourceStart, clock.GetUtcNow(),
                    timer.Elapsed, EventSourceStatus.Failed, 0, 0, false, [], [], [],
                    [$"Falha de coleta ({error.GetType().Name}): {error.Message}"]);
            }

            rawEvents.AddRange(result.Events);
            rawReports.AddRange(result.Reports);
            channels.AddRange(result.Channels);
            sources.Add(new(result.CollectorId, result.CollectorVersion, result.Source, result.Status,
                result.StartedUtc, result.CompletedUtc, result.Duration, result.EventsRead, result.ReportsRead,
                result.LimitHit, result.Limitations));
            progress?.Report(new(collector.Id, result.Status, index + 1, collectors.Count));
        }

        var deduplicated = EvidenceDeduplicator.Build(rawEvents, rawReports);
        var collectedChannels = channels
            .GroupBy(c => c.Channel, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Channel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var status = OverallStatus(sources, cancellationToken.IsCancellationRequested);
        var limitations = sources.SelectMany(s => s.Limitations).Distinct(StringComparer.Ordinal).ToArray();
        return new(1, id, SoftwareInfo.Version, SoftwareInfo.EventSchemaVersion, query.Preset, query.FromUtc,
            query.ToUtc, started, clock.GetUtcNow(), duration.Elapsed, status, collectedChannels, sources,
            deduplicated.Groups, deduplicated.Events, deduplicated.Reports, limitations);
    }

    private static EventInvestigationStatus OverallStatus(IReadOnlyList<EventSourceSummary> sources, bool cancelled)
    {
        if (cancelled || sources.Any(s => s.Status == EventSourceStatus.Cancelled))
            return EventInvestigationStatus.Cancelled;
        if (sources.All(s => s.Status == EventSourceStatus.Completed) &&
            sources.All(s => s.Limitations.Count == 0 && !s.LimitHit))
            return EventInvestigationStatus.Completed;
        if (sources.All(s => s.Status == EventSourceStatus.Failed))
            return EventInvestigationStatus.Failed;
        if (sources.All(s => s.Status == EventSourceStatus.Unsupported))
            return EventInvestigationStatus.Unsupported;
        return EventInvestigationStatus.Partial;
    }

    private static void Validate(IEventEvidenceCollector collector, EventCollectorResult result)
    {
        if (result is null || result.CollectorId != collector.Id || string.IsNullOrWhiteSpace(result.Source) ||
            string.IsNullOrWhiteSpace(result.CollectorVersion) || !Enum.IsDefined(result.Status) ||
            result.Status == EventSourceStatus.Running || result.StartedUtc > result.CompletedUtc ||
            result.Duration < TimeSpan.Zero || result.EventsRead < 0 || result.ReportsRead < 0 ||
            result.Events is null || result.Reports is null || result.Channels is null ||
            result.Limitations is null || result.EventsRead != result.Events.Count ||
            result.ReportsRead != result.Reports.Count)
            throw new InvalidDataException("Contrato inválido retornado pelo coletor de eventos.");
    }
}
