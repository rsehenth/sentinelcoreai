using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Executa uma investigação gráfica somente de leitura. A leitura de adaptadores, monitores e drivers reutiliza
/// os coletores de hardware existentes; a leitura de eventos e metadados WER reutiliza os coletores e o runner da
/// Fase 3.1. Apenas o registro gráfico é persistido: inventário e investigação de eventos originais não são
/// duplicados no histórico.
/// </summary>
public sealed class GraphicsInvestigationRunner
{
    private readonly IReadOnlyList<IHardwareCollector> hardwareCollectors;
    private readonly IReadOnlyList<IEventEvidenceCollector> eventCollectors;
    private readonly GraphicsEvidenceAnalyzer analyzer = new();
    private readonly GraphicsLimits limits;
    private readonly TimeProvider clock;
    private int running;

    public GraphicsInvestigationRunner(IEnumerable<IHardwareCollector> hardwareCollectors,
        IEnumerable<IEventEvidenceCollector> eventCollectors, TimeProvider? clock = null,
        GraphicsLimits? limits = null)
    {
        this.hardwareCollectors = hardwareCollectors
            .Where(collector => collector.Category is HardwareCategory.Gpu or HardwareCategory.Monitor
                or HardwareCategory.Driver)
            .ToArray();
        if (this.hardwareCollectors.Count == 0 ||
            this.hardwareCollectors.Select(collector => collector.Id).Distinct(StringComparer.Ordinal).Count() !=
            this.hardwareCollectors.Count)
            throw new ArgumentException(
                "Coletores gráficos de hardware devem ter identificadores únicos e não podem estar vazios.",
                nameof(hardwareCollectors));
        this.eventCollectors = eventCollectors.ToArray();
        if (this.eventCollectors.Count == 0 ||
            this.eventCollectors.Select(collector => collector.Id).Distinct(StringComparer.Ordinal).Count() !=
            this.eventCollectors.Count)
            throw new ArgumentException(
                "Coletores gráficos de eventos devem ter identificadores únicos e não podem estar vazios.",
                nameof(eventCollectors));
        this.clock = clock ?? TimeProvider.System;
        this.limits = limits ?? GraphicsLimits.Default;
        if (this.limits.MaxAdapters <= 0 || this.limits.MaxMonitors <= 0 || this.limits.MaxDrivers <= 0 ||
            this.limits.MaxEvents <= 0 || this.limits.MaxWerReports <= 0 || this.limits.MaxMessageChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Limites gráficos devem ser positivos.");
    }

    public async Task<GraphicsInvestigation> RunAsync(GraphicsInvestigationRequest request,
        GraphicsInvestigation? previous = null, IProgress<GraphicsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = EventWindows.Resolve(request.Window, clock.GetUtcNow(), request.FromUtc, request.ToUtc);
        return await RunCoreAsync(window, previous, progress, cancellationToken).ConfigureAwait(false);
    }

    internal Task<GraphicsInvestigation> RunFromAuditAsync(HardwareSnapshot hardware, EventInvestigation? events,
        CancellationToken cancellationToken)
    {
        var window = events is null
            ? EventWindows.Resolve(EventWindow.Last24Hours, clock.GetUtcNow())
            : new EventQueryWindow(events.Window, events.PeriodFromUtc, events.PeriodToUtc);
        return RunCoreAsync(window, null, null, cancellationToken, hardware, events);
    }

    private async Task<GraphicsInvestigation> RunCoreAsync(EventQueryWindow window, GraphicsInvestigation? previous,
        IProgress<GraphicsProgress>? progress, CancellationToken cancellationToken,
        HardwareSnapshot? auditHardware = null, EventInvestigation? auditEvents = null)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma investigação gráfica em andamento.");
        try
        {
            return await ExecuteAsync(window, previous, progress, cancellationToken, auditHardware, auditEvents)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private async Task<GraphicsInvestigation> ExecuteAsync(EventQueryWindow window,
        GraphicsInvestigation? previous, IProgress<GraphicsProgress>? progress, CancellationToken cancellationToken,
        HardwareSnapshot? auditHardware, EventInvestigation? auditEvents)
    {
        var id = Guid.NewGuid();
        var hardwareReadingId = auditHardware?.Id ?? Guid.NewGuid();
        var started = clock.GetUtcNow();
        var duration = Stopwatch.StartNew();
        var limitations = new List<string>();
        var sources = new List<GraphicsSourceSummary>();
        var cancelled = cancellationToken.IsCancellationRequested;
        var session = new HardwareCollectionSession();
        var hardwareResults = new List<HardwareCollectorResult>(hardwareCollectors.Count);
        var totalSteps = hardwareCollectors.Count + eventCollectors.Count;

        if (auditHardware is not null)
        {
            hardwareResults.AddRange(auditHardware.Results.Where(result =>
                hardwareCollectors.Any(collector => collector.Category == result.Category && collector.Id == result.CollectorId)));
        }
        else
        {
            for (var index = 0; index < hardwareCollectors.Count && !cancelled; index++)
            {
                var collector = hardwareCollectors[index];
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                progress?.Report(new(collector.Id, GraphicsSourceStatus.Running, index, totalSteps, "hardware"));
                var result = await ReadHardwareAsync(collector, hardwareReadingId, session, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status == HardwareCollectionStatus.Cancelled) cancelled = true;
                hardwareResults.Add(result);
                progress?.Report(new(collector.Id, Map(result.Status), index + 1, totalSteps, "hardware"));
            }
        }
        sources.AddRange(hardwareResults.Select(ToSource));

        var investigation = auditEvents;
        if (!cancelled && auditHardware is null)
        {
            try
            {
                var eventProgress = progress is null
                    ? null
                    : new Progress<EventInvestigationProgress>(update => progress.Report(new(update.CollectorId,
                        Map(update.Status), hardwareCollectors.Count + update.Finished, totalSteps, "eventos")));
                investigation = await new EventInvestigationRunner(eventCollectors, clock)
                    .RunAsync(window.Preset, window.FromUtc, window.ToUtc, eventProgress, cancellationToken)
                    .ConfigureAwait(false);
                if (investigation.Status == EventInvestigationStatus.Cancelled) cancelled = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
        }

        var eventId = investigation?.Id;
        var adapters = GraphicsProjection.BuildAdapters(
            hardwareResults.Where(result => result.Category == HardwareCategory.Gpu).SelectMany(result => result.Items),
            limits, out var adaptersTruncated);
        var monitors = GraphicsProjection.BuildMonitors(
            hardwareResults.Where(result => result.Category == HardwareCategory.Monitor).SelectMany(result => result.Items),
            limits, out var monitorsTruncated);
        var drivers = GraphicsProjection.BuildDrivers(adapters,
            hardwareResults.Where(result => result.Category == HardwareCategory.Driver).SelectMany(result => result.Items),
            limits, out var driversTruncated);
        var graphicsEvents = GraphicsProjection.BuildEvents(investigation, limits, out var eventsTruncated);
        var werReports = GraphicsProjection.BuildWerReports(investigation, limits, out var werTruncated);

        ApplyHardwareCounts(sources, adapters.Count, monitors.Count, drivers.Count);
        ApplyHardwareTruncation(sources, adaptersTruncated, monitorsTruncated, driversTruncated);
        AppendEventSources(investigation, graphicsEvents.Count, werReports.Count, sources, eventsTruncated,
            werTruncated);

        if (auditHardware is not null)
        {
            foreach (var collector in hardwareCollectors.Where(collector => !sources.Any(source => source.CollectorId == collector.Id)))
                sources.Add(Unavailable(collector.Id, collector.Version, collector.Source, Map(collector.Category)));
            foreach (var collector in eventCollectors.Where(collector => !sources.Any(source => source.CollectorId == collector.Id)))
                sources.Add(Unavailable(collector.Id, collector.Version, collector.Source,
                    string.Equals(collector.Id, GraphicsEvidenceClassification.WerMetadataCollectorId, StringComparison.OrdinalIgnoreCase)
                        ? GraphicsSourceCategory.Wer : GraphicsSourceCategory.Events));
        }

        GraphicsSourceSummary Unavailable(string id, string version, string origin, GraphicsSourceCategory category) =>
            new(id, version, category, origin, GraphicsSourceStatus.Failed, started, started, TimeSpan.Zero,
                0, 0, false, ["A fonte não foi confirmada nas etapas anteriores da auditoria."]);

        var expectedSources = hardwareCollectors.Count + eventCollectors.Count;
        var unavailable = sources.Where(source => source.Status is GraphicsSourceStatus.Failed
                or GraphicsSourceStatus.Unsupported)
            .Select(source => source.CollectorId)
            .Distinct(StringComparer.Ordinal).ToArray();
        var adaptersUnavailable = !sources.Any(source => source.Category == GraphicsSourceCategory.Adapters &&
            source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial);
        var monitorsUnavailable = !sources.Any(source => source.Category == GraphicsSourceCategory.Monitors &&
            source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial);
        var driversUnavailable = !sources.Any(source => source.Category == GraphicsSourceCategory.Drivers &&
            source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial);
        var eventsUnavailable = !sources.Any(source => source.Category is GraphicsSourceCategory.Events
            or GraphicsSourceCategory.Wer &&
            source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial);
        if (investigation is null)
        {
            eventsUnavailable = true;
            limitations.Add("A leitura de eventos e metadados WER não foi concluída nesta execução.");
        }
        else
        {
            var werQuality = EventQualityRules.Evaluate(investigation);
            if (werQuality.State == EventQualityState.Affected)
                limitations.Add($"Qualidade {werQuality.RuleId} {werQuality.RuleVersion}: {werQuality.Summary} " +
                    "Relatórios com horário efetivo fora do período não foram incluídos na evidência gráfica.");
            else if (werQuality.State == EventQualityState.Undetermined)
                limitations.Add($"Qualidade {werQuality.RuleId} {werQuality.RuleVersion}: {werQuality.Summary}");
        }

        // Presença e alterações só podem ser avaliadas quando a leitura atual também está completa e sem
        // truncamento. Uma falha atual nunca é convertida em remoção de adaptador, monitor ou driver.
        var currentAdaptersComparable = sources.Any(source =>
                source.Category == GraphicsSourceCategory.Adapters && source.Status == GraphicsSourceStatus.Completed &&
                !source.LimitHit) && !adaptersTruncated;
        var currentMonitorsComparable = sources.Any(source =>
                source.Category == GraphicsSourceCategory.Monitors && source.Status == GraphicsSourceStatus.Completed &&
                !source.LimitHit) && !monitorsTruncated;
        var currentDriversComparable = sources.Any(source =>
                source.Category == GraphicsSourceCategory.Drivers && source.Status == GraphicsSourceStatus.Completed &&
                !source.LimitHit) && !driversTruncated;

        var changes = GraphicsChangeEngine.Compare(previous, adapters, monitors, drivers,
            currentAdaptersComparable, currentMonitorsComparable, currentDriversComparable);
        var eventsSourceAvailable = sources.Any(source => source.Category == GraphicsSourceCategory.Events &&
            source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial);
        var werSourceAvailable = sources.Any(source => source.Category == GraphicsSourceCategory.Wer &&
            source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial);
        var analysis = analyzer.Analyze(adapters, monitors, graphicsEvents, werReports, changes,
            window.FromUtc, window.ToUtc, eventsSourceAvailable, werSourceAvailable);

        var allUnsupported = sources.Count > 0 && sources.All(source =>
            source.Status == GraphicsSourceStatus.Unsupported);
        var allFailed = sources.Count > 0 && sources.All(source => source.Status == GraphicsSourceStatus.Failed);
        var execution = cancelled ? GraphicsExecutionStatus.Cancelled
            : allUnsupported && expectedSources > 0 ? GraphicsExecutionStatus.Unsupported
            : allFailed ? GraphicsExecutionStatus.Failed
            : GraphicsExecutionStatus.Completed;

        var truncation = adaptersTruncated || monitorsTruncated || driversTruncated || eventsTruncated || werTruncated;
        var complete = execution == GraphicsExecutionStatus.Completed &&
            sources.Count == expectedSources && sources.All(source => source.Status == GraphicsSourceStatus.Completed) &&
            unavailable.Length == 0 && !adaptersUnavailable && !monitorsUnavailable && !driversUnavailable &&
            !eventsUnavailable && !truncation;
        var coverageStatus = execution is GraphicsExecutionStatus.Failed or GraphicsExecutionStatus.Unsupported
            ? GraphicsCoverageStatus.Unavailable
            : sources.Count == 0 ? GraphicsCoverageStatus.Unavailable
            : complete ? GraphicsCoverageStatus.Complete
            : GraphicsCoverageStatus.Partial;
        var status = execution switch
        {
            GraphicsExecutionStatus.Cancelled => GraphicsInvestigationStatus.Cancelled,
            GraphicsExecutionStatus.Failed => GraphicsInvestigationStatus.Failed,
            GraphicsExecutionStatus.Unsupported => GraphicsInvestigationStatus.Unsupported,
            _ => complete ? GraphicsInvestigationStatus.Completed : GraphicsInvestigationStatus.Partial
        };

        if (cancelled)
            limitations.Add("Investigação cancelada pelo usuário; as evidências coletadas foram preservadas.");
        if (adaptersUnavailable)
            limitations.Add("Adaptadores gráficos indisponíveis ou parciais; a ausência de um adaptador não indica remoção.");
        if (monitorsUnavailable)
            limitations.Add("Monitores indisponíveis ou parciais; a ausência de um monitor não indica remoção.");
        if (driversUnavailable)
            limitations.Add("Drivers gráficos indisponíveis ou parciais; a ausência de um driver não indica remoção.");
        if (eventsUnavailable)
            limitations.Add("Eventos e WER indisponíveis ou parciais; ausência de evento não prova ausência de falha.");
        if (truncation)
            limitations.Add("Limites internos de volume foram atingidos; a cobertura é parcial e nada foi omitido em silêncio.");
        if (graphicsEvents.Count == 0 && werReports.Count == 0 && !eventsUnavailable)
            limitations.Add("Nenhuma evidência gráfica observada na janela; isso não prova que não houve falha fora " +
                "da janela ou que o sistema não deixou de registrar o evento.");
        limitations.Add(GraphicsEvidenceClassification.ExtraScopeLimitation);
        limitations.Add(GraphicsEvidenceClassification.CausalityLimitation);
        limitations.Add(GraphicsEvidenceClassification.NoChangeLimitation);

        var coverage = new GraphicsCoverage(execution, coverageStatus, expectedSources,
            sources.Count(source => source.Status == GraphicsSourceStatus.Completed),
            adaptersUnavailable, monitorsUnavailable, driversUnavailable, eventsUnavailable,
            unavailable, limitations.ToArray());

        var completed = clock.GetUtcNow();
        duration.Stop();
        return new GraphicsInvestigation(1, id, SoftwareInfo.Version, SoftwareInfo.GraphicsSchemaVersion,
            window.Preset, window.FromUtc, window.ToUtc, started, completed, duration.Elapsed, status,
            hardwareReadingId, eventId, previous?.Id, sources, adapters, monitors, drivers, graphicsEvents,
            werReports, analysis.Observations, analysis.Notes, changes.Changes, changes.Comparable,
            changes.Limitations, coverage, limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task<HardwareCollectorResult> ReadHardwareAsync(IHardwareCollector collector, Guid readingId,
        IHardwareCollectionSession session, CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        var timer = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await collector.CollectAsync(readingId, session, cancellationToken).ConfigureAwait(false);
            if (result.CollectorId != collector.Id || result.Category != collector.Category ||
                result.Status == HardwareCollectionStatus.Running || result.Items is null ||
                result.Limitations is null)
                throw new InvalidDataException("Contrato inválido retornado pelo coletor gráfico de hardware.");
            var status = result.Status == HardwareCollectionStatus.Completed && result.Limitations.Count > 0
                ? HardwareCollectionStatus.Partial
                : result.Status;
            return result with { Status = status };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(collector.Id, collector.Version, collector.Category, collector.Source, started,
                clock.GetUtcNow(), timer.Elapsed, HardwareCollectionStatus.Cancelled, [],
                ["Coleta cancelada pelo usuário."]);
        }
        catch (Exception error)
        {
            return new(collector.Id, collector.Version, collector.Category, collector.Source, started,
                clock.GetUtcNow(), timer.Elapsed, HardwareCollectionStatus.Failed, [],
                [$"Falha de coleta ({error.GetType().Name}): {error.Message}"]);
        }
    }

    private static GraphicsSourceSummary ToSource(HardwareCollectorResult result) => new(
        result.CollectorId, result.CollectorVersion, Map(result.Category), result.Source, Map(result.Status),
        result.StartedUtc, result.CompletedUtc, result.Duration, result.Items.Count, result.Items.Count, false,
        result.Limitations.ToArray());

    private static void ApplyHardwareCounts(List<GraphicsSourceSummary> sources, int adapters, int monitors,
        int drivers)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var evidence = source.Category switch
            {
                GraphicsSourceCategory.Adapters => adapters,
                GraphicsSourceCategory.Monitors => monitors,
                GraphicsSourceCategory.Drivers => drivers,
                _ => source.EvidenceCount
            };
            if (evidence != source.EvidenceCount) sources[index] = source with { EvidenceCount = evidence };
        }
    }

    private static void ApplyHardwareTruncation(List<GraphicsSourceSummary> sources, bool adaptersTruncated,
        bool monitorsTruncated, bool driversTruncated)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var truncated = source.Category switch
            {
                GraphicsSourceCategory.Adapters => adaptersTruncated,
                GraphicsSourceCategory.Monitors => monitorsTruncated,
                GraphicsSourceCategory.Drivers => driversTruncated,
                _ => false
            };
            if (!truncated) continue;
            sources[index] = source with
            {
                LimitHit = true,
                Limitations = [.. source.Limitations, "Limite interno de itens atingido; a lista não é exaustiva."]
            };
        }
    }

    private static void AppendEventSources(EventInvestigation? investigation, int graphicsEventCount,
        int werReportCount, List<GraphicsSourceSummary> sources, bool eventsTruncated, bool werTruncated)
    {
        if (investigation is null) return;
        foreach (var source in investigation.Sources)
        {
            var isWer = string.Equals(source.CollectorId,
                GraphicsEvidenceClassification.WerMetadataCollectorId, StringComparison.OrdinalIgnoreCase);
            var truncated = isWer ? werTruncated : eventsTruncated;
            var evidence = isWer ? werReportCount : graphicsEventCount;
            var limitations = source.Limitations.ToList();
            if (truncated)
            {
                limitations.Add("Limite interno de evidências gráficas atingido; a amostra não é exaustiva.");
            }
            sources.Add(new GraphicsSourceSummary(source.CollectorId, source.CollectorVersion,
                isWer ? GraphicsSourceCategory.Wer : GraphicsSourceCategory.Events, source.Source,
                Map(source.Status), source.StartedUtc, source.CompletedUtc, source.Duration,
                isWer ? source.ReportsRead : source.EventsRead, evidence, source.LimitHit || truncated,
                limitations.Distinct(StringComparer.Ordinal).ToArray()));
        }
    }

    private static GraphicsSourceCategory Map(HardwareCategory category) => category switch
    {
        HardwareCategory.Gpu => GraphicsSourceCategory.Adapters,
        HardwareCategory.Monitor => GraphicsSourceCategory.Monitors,
        HardwareCategory.Driver => GraphicsSourceCategory.Drivers,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static GraphicsSourceStatus Map(HardwareCollectionStatus status) => status switch
    {
        HardwareCollectionStatus.Running => GraphicsSourceStatus.Running,
        HardwareCollectionStatus.Completed => GraphicsSourceStatus.Completed,
        HardwareCollectionStatus.Partial => GraphicsSourceStatus.Partial,
        HardwareCollectionStatus.Failed => GraphicsSourceStatus.Failed,
        HardwareCollectionStatus.Unsupported => GraphicsSourceStatus.Unsupported,
        HardwareCollectionStatus.Cancelled => GraphicsSourceStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static GraphicsSourceStatus Map(EventSourceStatus status) => status switch
    {
        EventSourceStatus.Running => GraphicsSourceStatus.Running,
        EventSourceStatus.Completed => GraphicsSourceStatus.Completed,
        EventSourceStatus.Partial => GraphicsSourceStatus.Partial,
        EventSourceStatus.Failed => GraphicsSourceStatus.Failed,
        EventSourceStatus.Unsupported => GraphicsSourceStatus.Unsupported,
        EventSourceStatus.Cancelled => GraphicsSourceStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

}
