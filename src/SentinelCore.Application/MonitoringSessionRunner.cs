using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Coordena uma sessão de monitoramento autorizada: agenda fontes com cadências diferentes, aplica orçamento de
/// recursos, grava checkpoints imutáveis por fonte e preserva incidentes manuais. Não instala serviço, não cria
/// tarefa agendada e não inicia observação sem <see cref="MonitoringRequest.Authorized"/>.
/// </summary>
public sealed class MonitoringSessionRunner
{
    private readonly IReadOnlyList<IMonitoringSource> sources;
    private readonly IMonitoringSessionStore store;
    private readonly TimeProvider clock;
    private readonly MonitoringLimits limits;
    private readonly object gate = new();
    private readonly List<MonitoringIncident> incidents = [];
    private RunState? active;
    private int running;

    public MonitoringSessionRunner(IReadOnlyList<IMonitoringSource> sources, IMonitoringSessionStore store,
        TimeProvider? clock = null, MonitoringLimits? limits = null)
    {
        this.sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? TimeProvider.System;
        this.limits = limits ?? MonitoringLimits.Default;
        this.limits.Validate();
        if (this.sources.Select(source => source.Kind).Distinct().Count() != this.sources.Count)
            throw new ArgumentException("Fontes de monitoramento devem ter tipos únicos.", nameof(sources));
    }

    public MonitoringLimits Limits => limits;

    public bool IsRunning
    {
        get { lock (gate) return active is not null; }
    }

    public IReadOnlyList<MonitoringIncident> Incidents
    {
        get { lock (gate) return incidents.ToArray(); }
    }

    /// <summary>
    /// Registra um incidente enquanto a sessão está ativa. Retorna falso quando não existe sessão em andamento
    /// ou quando o limite de incidentes foi atingido. O momento do registro é preservado separadamente de um
    /// horário aproximado informado pelo usuário.
    /// </summary>
    public bool TryRecordIncident(MonitoringIncidentRequest request, out MonitoringIncident? incident)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Category))
            throw new ArgumentOutOfRangeException(nameof(request), "Categoria de incidente inválida.");

        lock (gate)
        {
            incident = null;
            if (active is not { } run) return false;
            if (incidents.Count >= limits.MaxIncidents)
            {
                if (!run.Limitations.Contains(IncidentLimitLimitation))
                    run.Limitations.Add(IncidentLimitLimitation);
                return false;
            }

            var recorded = clock.GetUtcNow().ToUniversalTime();
            DateTimeOffset? occurred = request.OccurredUtc?.ToUniversalTime();
            var limitations = new List<string> { MonitoringGuidance.IncidentLimitation };
            if (occurred is { } informed)
            {
                if (informed > recorded.AddMinutes(1) || informed < run.StartedUtc.AddHours(-24))
                    throw new ArgumentException(
                        "O horário aproximado da ocorrência está fora da faixa aceita; nenhum horário foi inventado.",
                        nameof(request));
                if (informed > recorded)
                    limitations.Add("O horário informado é posterior ao registro; a ordem foi preservada sem ajuste.");
            }
            else
            {
                limitations.Add("Nenhum horário de ocorrência foi informado; apenas o momento do registro manual " +
                    "é conhecido.");
            }

            var description = string.IsNullOrWhiteSpace(request.Description)
                ? null
                : request.Description.Trim();
            if (description is { Length: > 0 } text && text.Length > limits.MaxIncidentDescriptionLength)
            {
                description = text[..limits.MaxIncidentDescriptionLength];
                limitations.Add($"A descrição excedeu {limits.MaxIncidentDescriptionLength} caracteres e foi " +
                    "truncada; o texto original não foi preservado.");
            }

            var evidence = new List<MonitoringEvidenceReference>();
            if (run.EventObservations > 0)
                evidence.Add(new(MonitoringSource.Events, $"{run.EventObservations} evento(s)",
                    "Eventos observados até o registro manual, sujeitos à janela da sessão."));
            if (run.PerformanceObservations > 0)
                evidence.Add(new(MonitoringSource.Performance, $"{run.PerformanceObservations} amostra(s)",
                    "Amostras de CPU e memória observadas até o registro manual."));
            if (run.NetworkObservations > 0)
                evidence.Add(new(MonitoringSource.Network, $"{run.NetworkObservations} leitura(s)",
                    "Contagens agregadas de rede observadas até o registro manual; sem endereços."));

            incident = new MonitoringIncident(Guid.NewGuid(), run.SessionId, recorded, occurred,
                occurred is null ? MonitoringIncidentPrecision.Unknown : MonitoringIncidentPrecision.Approximate,
                request.Category, description,
                Math.Round(run.Timer.Elapsed.TotalSeconds, 3),
                evidence, limitations);
            incidents.Add(incident);
            return true;
        }
    }

    public async Task<MonitoringSession> RunAsync(MonitoringRequest request,
        IProgress<MonitoringProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(request);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma sessão de monitoramento em andamento.");
        try
        {
            return await ExecuteAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private async Task<MonitoringSession> ExecuteAsync(MonitoringRequest request,
        IProgress<MonitoringProgress>? progress, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var startedUtc = clock.GetUtcNow();
        var timer = new SessionTimer(clock);
        var run = new RunState(id, startedUtc, timer);
        lock (gate)
        {
            incidents.Clear();
            active = run;
        }

        var readers = new List<Reader>();
        var sinks = new List<Sink>();
        var persistenceFailed = false;
        try
        {
            foreach (var source in sources)
            {
                var eventsKind = source.Kind == MonitoringSource.Events;
                var selected = eventsKind
                    ? request.Sources.HasFlag(MonitoringSources.Events) ||
                      request.Sources.HasFlag(MonitoringSources.Graphics)
                    : request.Sources.HasFlag(MonitoringGuidance.Flag(source.Kind));
                if (!selected) continue;
                var interval = MonitoringGuidance.Interval(source.Kind, limits);
                var primaryKind = eventsKind && !request.Sources.HasFlag(MonitoringSources.Events)
                    ? MonitoringSource.Graphics
                    : source.Kind;
                var sink = new Sink(primaryKind, source.CollectorId, source.CollectorVersion, interval,
                    request.ExpectedTicks(interval));
                sinks.Add(sink);
                var reader = new Reader(source.Kind, interval, sink);
                if (eventsKind && request.Sources.HasFlag(MonitoringSources.Events) &&
                    request.Sources.HasFlag(MonitoringSources.Graphics))
                {
                    var graphics = new Sink(MonitoringSource.Graphics, source.CollectorId,
                        source.CollectorVersion, interval, request.ExpectedTicks(interval));
                    reader.Secondary = graphics;
                    sinks.Add(graphics);
                }

                IMonitoringSourceSession? session = null;
                var unavailable = false;
                try
                {
                    session = await source.StartAsync(cancellationToken).ConfigureAwait(false);
                    reader.Session = session;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    unavailable = true;
                    sink.Limitations.Add($"Fonte {source.CollectorId} indisponível para iniciar " +
                        $"({error.GetType().Name}): {error.Message}");
                }

                readers.Add(reader);
                if (unavailable) MarkUnavailable(reader);
            }

            foreach (var kind in Enum.GetValues<MonitoringSource>())
            {
                if (!request.Sources.HasFlag(MonitoringGuidance.Flag(kind))) continue;
                if (sinks.Any(sink => sink.Kind == kind)) continue;
                var interval = MonitoringGuidance.Interval(kind, limits);
                var missing = new Sink(kind, $"unavailable-{kind.ToString().ToLowerInvariant()}", "0.0.0",
                    interval, request.ExpectedTicks(interval))
                { Unavailable = true };
                missing.Limitations.Add("Fonte indisponível nesta composição; nenhuma observação foi fabricada.");
                sinks.Add(missing);
            }

            if (readers.Count == 0)
                run.Limitations.Add("Nenhuma fonte pôde ser iniciada; a sessão foi registrada como não " +
                    "disponível sem inventar observações.");

            var nextSegmentAt = limits.SegmentDuration;
            var lastTickWarning = false;
            while (true)
            {
                var elapsed = timer.Elapsed;
                if (elapsed >= request.Duration) break;
                if (readers.All(reader => reader.Unavailable)) break;

                var due = readers.Where(reader => !reader.Unavailable && reader.NextDue <= elapsed).ToArray();
                if (due.Length == 0)
                {
                    var next = readers.Where(reader => !reader.Unavailable)
                        .Select(reader => reader.NextDue)
                        .DefaultIfEmpty(request.Duration)
                        .Min();
                    var wait = next - elapsed;
                    if (wait <= TimeSpan.Zero) continue;
                    var remaining = request.Duration - elapsed;
                    if (wait > remaining) wait = remaining;
                    await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var reader in due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.InFlight is { IsCompleted: false })
                    {
                        reader.Primary.Missed++;
                        if (reader.Secondary is { } busyGraphics) busyGraphics.Missed++;
                        if (!reader.Primary.Limitations.Contains(ReadStillRunningLimitation))
                            reader.Primary.Limitations.Add(ReadStillRunningLimitation);
                        reader.NextDue += reader.Interval;
                        continue;
                    }
                    reader.InFlight = null;
                    var measured = Stopwatch.StartNew();
                    MonitoringTick tick;
                    var readAtUtc = clock.GetUtcNow().ToUniversalTime();
                    var readTask = reader.Session!.ReadAsync(readAtUtc, cancellationToken);
                    try
                    {
                        tick = await readTask.WaitAsync(limits.MaxCollectorTick, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Observe(readTask);
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        measured.Stop();
                        Observe(readTask);
                        reader.InFlight = readTask;
                        reader.Primary.BudgetExceeded = true;
                        reader.Primary.Incomplete = true;
                        reader.Primary.Missed++;
                        if (reader.Secondary is { } abandonedGraphics)
                        {
                            abandonedGraphics.BudgetExceeded = true;
                            abandonedGraphics.Incomplete = true;
                            abandonedGraphics.Missed++;
                        }
                        reader.Primary.Limitations.Add($"Leitura de {reader.Kind} excedeu o orçamento de " +
                            $"{limits.MaxCollectorTick.TotalSeconds:0.#} s e foi abandonada; a fonte continua " +
                            "sem iniciar outra leitura até a anterior terminar, sem acumular chamadas concorrentes.");
                        run.BudgetWarning = true;
                        lastTickWarning = true;
                        reader.NextDue += reader.Interval;
                        continue;
                    }
                    catch (Exception error)
                    {
                        measured.Stop();
                        Observe(readTask);
                        reader.InFlight = null;
                        reader.Failures++;
                        reader.Primary.Incomplete = true;
                        reader.Primary.Limitations.Add($"Falha de leitura ({error.GetType().Name}); o ciclo foi " +
                            "contabilizado e a sessão continuou.");
                        if (reader.Failures >= 3) MarkUnavailable(reader);
                        reader.NextDue += reader.Interval;
                        continue;
                    }

                    reader.InFlight = null;
                    measured.Stop();
                    var milliseconds = measured.Elapsed.TotalMilliseconds;
                    run.TickSamples++;
                    run.MeanTickMilliseconds += milliseconds;
                    run.MaxTickMilliseconds = Math.Max(run.MaxTickMilliseconds, milliseconds);
                    reader.Primary.CollectedTicks++;
                    if (reader.Secondary is { } secondary) secondary.CollectedTicks++;

                    if (measured.Elapsed > limits.MaxCollectorTick)
                    {
                        reader.Primary.BudgetExceeded = true;
                        reader.Primary.Missed++;
                        if (reader.Secondary is { } overrunGraphics)
                        {
                            overrunGraphics.BudgetExceeded = true;
                            overrunGraphics.Missed++;
                        }
                        reader.Primary.Limitations.Add($"Leitura de {reader.Kind} excedeu o orçamento de " +
                            $"{limits.MaxCollectorTick.TotalSeconds:0.#} s; a próxima oportunidade foi descartada " +
                            "sem rajada de recuperação.");
                        run.BudgetWarning = true;
                        lastTickWarning = true;
                    }

                    Accept(reader, tick, run);
                    reader.NextDue += reader.Interval;
                    var now = timer.Elapsed;
                    while (reader.NextDue <= now - Jitter(reader.Interval))
                    {
                        reader.NextDue += reader.Interval;
                        reader.Primary.Missed++;
                        if (reader.Secondary is { } missedGraphics) missedGraphics.Missed++;
                        reader.Primary.Limitations.Add("Oportunidade de observação perdida: a leitura anterior " +
                            "ultrapassou a cadência planejada.");
                    }

                    if (reader.Kind is MonitoringSource.Performance or MonitoringSource.Network &&
                        reader.Primary.Observations >= limits.MaxSamplesPerSource)
                    {
                        reader.Primary.LimitHit = true;
                        reader.Primary.SampleLimitHit = true;
                        reader.Primary.Limitations.Add($"Limite de {limits.MaxSamplesPerSource} amostras por " +
                            "fonte atingido; nenhuma amostra adicional foi coletada.");
                        reader.Unavailable = true;
                    }

                    if (run.TotalEvents >= limits.MaxEventsPerSession &&
                        reader.Kind == MonitoringSource.Events)
                    {
                        foreach (var sink in sinks.Where(item =>
                                     item.Kind is MonitoringSource.Events or MonitoringSource.Graphics))
                        {
                            sink.LimitHit = true;
                            sink.Limitations.Add($"Limite de {limits.MaxEventsPerSession} eventos da sessão " +
                                "atingido; eventos adicionais não foram retidos.");
                        }
                        reader.Unavailable = true;
                    }
                }

                if (timer.Elapsed >= nextSegmentAt)
                {
                    persistenceFailed |= await FlushAsync(sinks, run, timer.Elapsed, cancellationToken)
                        .ConfigureAwait(false);
                    nextSegmentAt += limits.SegmentDuration;
                    if (run.StorageBudgetReached) break;
                }

                progress?.Report(new MonitoringProgress(timer.Elapsed,
                    request.Duration > timer.Elapsed ? request.Duration - timer.Elapsed : TimeSpan.Zero,
                    run.PerformanceObservations, run.TotalEvents, IncidentCount(), lastTickWarning,
                    persistenceFailed ? "orçamento de gravação" : "observando"));
                lastTickWarning = false;
            }

            persistenceFailed |= await FlushAsync(sinks, run, request.Duration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            persistenceFailed |= await FlushAsync(sinks, run, timer.Elapsed, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
            timer.Stop();
            lock (gate) active = null;
        }

        var cancelled = cancellationToken.IsCancellationRequested;
        var completedUtc = clock.GetUtcNow().ToUniversalTime();
        if (completedUtc < run.StartedUtc) completedUtc = run.StartedUtc;
        return BuildSession(request, run, sinks, cancelled, persistenceFailed, completedUtc);
    }

    private void Accept(Reader reader, MonitoringTick tick, RunState run)
    {
        foreach (var limitation in tick.Limitations)
            reader.Primary.Limitations.Add(limitation);
        if (reader.Secondary is { } secondary)
            foreach (var limitation in tick.Limitations)
                secondary.Limitations.Add(limitation);

        if (tick.LimitHit)
        {
            reader.Primary.LimitHit = true;
            reader.Primary.Limitations.Add("O leitor declarou truncamento no ciclo; a fonte permanece parcial.");
            if (reader.Secondary is { } truncated)
            {
                truncated.LimitHit = true;
                truncated.Limitations.Add("O leitor declarou truncamento no ciclo; a fonte permanece parcial.");
            }
        }

        if (tick.Unavailable)
        {
            reader.Primary.UnavailableTicks++;
            reader.Primary.Incomplete = true;
            reader.Primary.Limitations.Add($"Fonte {reader.Kind} não produziu leitura válida neste ciclo.");
            return;
        }

        switch (tick.Source)
        {
            case MonitoringSource.Performance when tick.Performance is { } sample:
                if (reader.Primary.Observations >= limits.MaxSamplesPerSource) return;
                reader.Primary.Performance.Add(sample);
                reader.Primary.Observations++;
                run.PerformanceObservations++;
                break;
            case MonitoringSource.Network when tick.Network is { } network:
                reader.Primary.Network.Add(network);
                reader.Primary.Observations++;
                run.NetworkObservations++;
                if (network.Incomplete)
                {
                    reader.Primary.Incomplete = true;
                    reader.Primary.Limitations.Add("A leitura de rede foi declarada incompleta pelo coletor; as " +
                        "contagens não representam todas as tabelas nativas.");
                }
                break;
            case MonitoringSource.Events:
                foreach (var item in tick.Events)
                {
                    if (run.TotalEvents >= limits.MaxEventsPerSession) break;
                    var graphics = item.Category == EventCategory.Graphics;
                    var retained = true;
                    if (graphics && reader.Secondary is { } graphicsSink)
                    {
                        graphicsSink.Events.Add(item);
                        graphicsSink.Observations++;
                        if (item.MessageTruncated) graphicsSink.TruncatedTicks++;
                    }
                    else if (graphics && reader.Primary.Kind == MonitoringSource.Graphics)
                    {
                        reader.Primary.Events.Add(item);
                        reader.Primary.Observations++;
                        if (item.MessageTruncated) reader.Primary.TruncatedTicks++;
                    }
                    else if (!graphics && reader.Primary.Kind == MonitoringSource.Events)
                    {
                        reader.Primary.Events.Add(item);
                        reader.Primary.Observations++;
                        if (item.MessageTruncated) reader.Primary.TruncatedTicks++;
                    }
                    else if (!graphics && reader.Primary.Kind == MonitoringSource.Graphics)
                    {
                        reader.Primary.FilteredEvents++;
                        retained = false;
                    }
                    else
                    {
                        reader.Primary.Events.Add(item);
                        reader.Primary.Observations++;
                        if (item.MessageTruncated) reader.Primary.TruncatedTicks++;
                    }

                    if (retained)
                    {
                        run.TotalEvents++;
                        run.EventObservations++;
                    }
                }

                if (reader.Primary.FilteredEvents > 0 && !reader.Primary.Limitations.Contains(OnlyGraphicsLimitation))
                    reader.Primary.Limitations.Add(OnlyGraphicsLimitation);
                break;
        }

        reader.Primary.MarkBuffer(tick.TimestampUtc);
        reader.Secondary?.MarkBuffer(tick.TimestampUtc);
    }

    private const string OnlyGraphicsLimitation =
        "Somente fontes gráficas foram selecionadas: eventos de outras categorias foram descartados e não são " +
        "apresentados como cobertura desta sessão.";

    private const string IncidentLimitLimitation =
        "Limite de incidentes da sessão atingido; novas marcações manuais foram recusadas e a sessão continuou.";

    private const string ReadStillRunningLimitation =
        "A leitura anterior ainda estava em andamento quando o ciclo seguinte venceu; o ciclo foi descartado " +
        "para não iniciar chamadas concorrentes e a oportunidade foi contabilizada.";

    private int IncidentCount()
    {
        lock (gate) return incidents.Count;
    }

    private async Task<bool> FlushAsync(IReadOnlyList<Sink> sinks, RunState run, TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var failed = false;
        foreach (var sink in sinks)
        {
            if (sink.Performance.Count == 0 && sink.Events.Count == 0 && sink.Network.Count == 0) continue;
            if (run.StorageBudgetReached)
            {
                sink.Incomplete = true;
                continue;
            }

            var from = sink.BufferFrom ?? run.StartedUtc;
            var to = sink.BufferTo ?? from;
            var status = sink.BudgetExceeded || sink.LimitHit || sink.Missed > 0 || sink.TruncatedTicks > 0 ||
                sink.Incomplete || sink.UnavailableTicks > 0 || sink.PersistenceFailed
                ? MonitoringSegmentStatus.Partial
                : MonitoringSegmentStatus.Complete;
            var segment = new MonitoringSegment(1, Guid.NewGuid(), run.SessionId, sink.Sequence,
                sink.Kind, sink.CollectorId, sink.CollectorVersion, from.ToUniversalTime(), to.ToUniversalTime(),
                status,
                sink.Performance.ToArray(), sink.Events.ToArray(), sink.Network.ToArray(),
                sink.Limitations.Distinct(StringComparer.Ordinal).ToArray());
            try
            {
                var bytes = await store.SaveMonitoringSegmentAsync(segment, cancellationToken)
                    .ConfigureAwait(false);
                run.PersistedBytes += bytes;
                sink.Sequence++;
                sink.Segments.Add(segment.Reference);
                sink.Performance.Clear();
                sink.Events.Clear();
                sink.Network.Clear();
                sink.BufferFrom = null;
                sink.BufferTo = null;
                if (run.PersistedBytes > limits.MaxSessionBytes)
                {
                    run.StorageBudgetReached = true;
                    run.Limitations.Add($"Orçamento de armazenamento de {limits.MaxSessionBytes} bytes atingido " +
                        $"durante a sessão ({elapsed.TotalSeconds:0} s): nenhuma gravação adicional foi feita e os " +
                        "segmentos confirmados foram preservados.");
                }
            }
            catch (Exception error)
            {
                failed = true;
                sink.PersistenceFailed = true;
                run.Limitations.Add($"Falha de gravação de segmento ({error.GetType().Name}): as observações do " +
                    "segmento não confirmado não foram persistidas; nenhuma evidência foi inventada.");
            }
        }

        if (run.StorageBudgetReached)
        {
            var pending = sinks.Sum(sink =>
                sink.Performance.Count + sink.Events.Count + sink.Network.Count);
            if (pending > 0)
                run.Limitations.Add($"{pending} observação(ões) coletada(s) não foram persistidas porque o " +
                    "orçamento de armazenamento foi atingido; a contagem observada e a persistida divergem e a " +
                    "diferença permanece declarada.");
        }

        return failed;
    }

    private static void MarkUnavailable(Reader reader)
    {
        reader.Unavailable = true;
        reader.Primary.Unavailable = true;
        if (reader.Secondary is { } secondary) secondary.Unavailable = true;
        reader.Primary.Limitations.Add("Fonte indisponível durante a sessão; nenhuma observação foi fabricada " +
            "para preencher os ciclos restantes.");
    }

    private MonitoringSession BuildSession(MonitoringRequest request, RunState run, IReadOnlyList<Sink> sinks,
        bool cancelled, bool persistenceFailed, DateTimeOffset completedUtc)
    {
        var summaries = new List<MonitoringSourceSummary>();
        foreach (var sink in sinks.OrderBy(item => item.Kind))
        {
            var status = sink.Unavailable ? MonitoringSourceStatus.Unavailable
                : cancelled ? MonitoringSourceStatus.Cancelled
                : sink.CollectedTicks == 0
                    ? sink.BudgetExceeded ? MonitoringSourceStatus.Partial : MonitoringSourceStatus.Unavailable
                : sink.Incomplete || sink.PersistenceFailed || sink.Missed > 0 || sink.TruncatedTicks > 0 ||
                  sink.LimitHit || sink.BudgetExceeded || sink.CollectedTicks < sink.ExpectedTicks ||
                  sink.UnavailableTicks > 0 ||
                  (sink.Kind is MonitoringSource.Performance or MonitoringSource.Network && sink.Observations == 0)
                    ? MonitoringSourceStatus.Partial
                    : MonitoringSourceStatus.Completed;
            var sourceLimitations = new List<string>(sink.Limitations);
            if (sink.Kind == MonitoringSource.Graphics)
                sourceLimitations.Add("Esta fonte é uma visão derivada do mesmo fluxo incremental de eventos; não " +
                    "existe coletor gráfico paralelo nem teste de estresse da GPU.");
            if (sink.Missed > 0)
                sourceLimitations.Add($"{sink.Missed} oportunidade(s) de observação perdida(s) nesta fonte.");
            if (sink.UnavailableTicks > 0)
                sourceLimitations.Add($"{sink.UnavailableTicks} ciclo(s) sem leitura válida nesta fonte.");
            if (sink.Incomplete)
                sourceLimitations.Add("A fonte ficou incompleta: parte do período não possui observação válida e " +
                    "a cobertura não a representa como concluída.");
            if (sink.PersistenceFailed)
                sourceLimitations.Add("Houve falha de gravação de checkpoint desta fonte; as observações não " +
                    "confirmadas não são apresentadas como persistidas.");
            if (sink.Kind is MonitoringSource.Performance or MonitoringSource.Network && sink.Observations == 0)
                sourceLimitations.Add("Nenhuma observação válida desta fonte foi registrada; a ausência não foi " +
                    "convertida em valor zero.");
            summaries.Add(new MonitoringSourceSummary(sink.Kind, sink.CollectorId, sink.CollectorVersion,
                status, sink.Interval, sink.ExpectedTicks, sink.CollectedTicks, sink.Missed, sink.TruncatedTicks,
                sink.Observations, sink.LimitHit, sink.BudgetExceeded,
                sourceLimitations.Distinct(StringComparer.Ordinal).ToArray()));
        }

        var segments = sinks.SelectMany(sink => sink.Segments)
            .OrderBy(reference => reference.Source)
            .ThenBy(reference => reference.Sequence)
            .ToArray();
        foreach (var sink in sinks)
        {
            var persisted = segments.Where(reference => reference.Source == sink.Kind)
                .Sum(reference => reference.ObservationCount);
            if (sink.Observations > persisted)
                run.Limitations.Add($"{sink.Observations - persisted} observação(ões) de " +
                    $"{MonitoringGuidance.SourceName(sink.Kind)} foram coletadas, mas não confirmadas em " +
                    "checkpoint; a diferença não é apresentada como evidência persistida.");
        }
        var totalObservations = sinks.Sum(sink => sink.Observations);
        var anyUnavailable = sinks.Any(sink =>
            sink.Unavailable || sink.CollectedTicks == 0 && !sink.BudgetExceeded);
        var allUnavailable = sinks.Count > 0 &&
            sinks.All(sink => sink.Unavailable || sink.CollectedTicks == 0 && !sink.BudgetExceeded);
        var execution = cancelled ? MonitoringExecutionStatus.Cancelled
            : allUnavailable && run.PersistedBytes == 0 ||
              persistenceFailed && run.PersistedBytes == 0
                ? MonitoringExecutionStatus.Failed
            : MonitoringExecutionStatus.Completed;

        var complete = execution == MonitoringExecutionStatus.Completed &&
            summaries.All(item => item.Status == MonitoringSourceStatus.Completed) &&
            !run.BudgetWarning && !run.StorageBudgetReached && !persistenceFailed &&
            summaries.All(item => item.CollectedTicks >= item.ExpectedTicks);
        var coverage = totalObservations == 0
            ? execution is MonitoringExecutionStatus.Failed or MonitoringExecutionStatus.Cancelled
                ? MonitoringCoverageStatus.Unavailable
                : MonitoringCoverageStatus.Partial
            : complete ? MonitoringCoverageStatus.Complete : MonitoringCoverageStatus.Partial;

        var limitations = new List<string>
        {
            MonitoringGuidance.AuthorizationLimitation,
            MonitoringGuidance.ScopeLimitation,
            MonitoringGuidance.CausalityLimitation,
            MonitoringGuidance.BudgetLimitation
        };
        if (request.Sources.HasFlag(MonitoringSources.Events)) limitations.Add(MonitoringGuidance.DeduplicationLimitation);
        if (request.Sources.HasFlag(MonitoringSources.Network)) limitations.Add(MonitoringGuidance.NetworkPrivacyLimitation);
        limitations.AddRange(run.Limitations);
        foreach (var summary in summaries)
            foreach (var limitation in summary.Limitations)
                limitations.Add($"{MonitoringGuidance.SourceName(summary.Source)}: {limitation}");
        if (cancelled) limitations.Add("Sessão cancelada pelo usuário; somente os segmentos confirmados foram preservados.");
        if (anyUnavailable) limitations.Add("Ao menos uma fonte ficou indisponível; a cobertura não a representa como coletada.");
        if (persistenceFailed)
            limitations.Add("Houve falha de gravação de ao menos um segmento; os dados não confirmados não foram " +
                "persistidos e a sessão não os apresenta como evidência.");
        if (run.StorageBudgetReached)
            limitations.Add("A sessão foi encerrada ao atingir o orçamento de armazenamento; registros confirmados " +
                "permanecem intactos e nenhum registro histórico foi apagado.");

        var budget = new MonitoringBudgetReport(request.Duration, limits.MaxCollectorTick,
            summaries.Sum(item => item.ExpectedTicks), summaries.Sum(item => item.CollectedTicks),
            summaries.Sum(item => item.MissedOpportunities),
            run.TickSamples == 0 ? 0 : Math.Round(run.MeanTickMilliseconds / run.TickSamples, 3),
            Math.Round(run.MaxTickMilliseconds, 3),
            run.PerformanceObservations, run.TotalEvents, run.PersistedBytes, limits.MaxSessionBytes,
            run.StorageBudgetReached, run.BudgetWarning,
            sinks.Any(sink => sink.SampleLimitHit),
            run.TotalEvents >= limits.MaxEventsPerSession,
            $"desempenho {limits.PerformanceInterval.TotalSeconds:0} s · eventos " +
            $"{limits.EventInterval.TotalSeconds:0} s · rede {limits.NetworkInterval.TotalSeconds:0} s · " +
            $"segmentos {limits.SegmentDuration.TotalSeconds:0} s",
            [
                MonitoringGuidance.BudgetLimitation,
                "Cadência fixa e declarada; nenhuma fonte é executada a cada segundo e auditorias completas não " +
                "são repetidas durante a sessão."
            ]);

        var duration = run.Timer.Elapsed;
        return new MonitoringSession(1, run.SessionId, SoftwareInfo.Version, SoftwareInfo.MonitoringSchemaVersion,
            request.Preset, request.Duration, run.StartedUtc, completedUtc, duration, execution, coverage,
            request.Sources, summaries, segments, incidents.ToArray(), budget, MonitoringRecoveryInfo.None,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

    private void Validate(MonitoringRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Authorized)
            throw new ArgumentException("O monitoramento exige autorização explícita do usuário.",
                nameof(request));
        if (request.Sources == MonitoringSources.None)
            throw new ArgumentException("Selecione ao menos uma fonte para observar.", nameof(request));
        if ((request.Sources & ~MonitoringSources.All) != 0)
            throw new ArgumentException("Fontes de monitoramento inválidas.", nameof(request));
        if (!Enum.IsDefined(request.Preset))
            throw new ArgumentOutOfRangeException(nameof(request), "Duração de sessão inválida.");
        if (request.Preset == MonitoringDurationPreset.Unknown)
            throw new ArgumentException("A duração da sessão deve ser autorizada antes do início.", nameof(request));
        if (request.Preset != MonitoringDurationPreset.Custom &&
            request.Duration != MonitoringRequest.DurationFor(request.Preset))
            throw new ArgumentException("A duração não corresponde ao preset autorizado.", nameof(request));
        if (request.Preset == MonitoringDurationPreset.Custom && request.Duration <= TimeSpan.Zero)
            throw new ArgumentException("A duração personalizada deve ser maior que zero.", nameof(request));
        if (request.Duration > limits.MaxSessionDuration)
            throw new ArgumentException(
                $"A duração máxima autorizada é de {limits.MaxSessionDuration.TotalMinutes:0} minutos.",
                nameof(request));
    }

    private static TimeSpan Jitter(TimeSpan interval) =>
        TimeSpan.FromMilliseconds(Math.Min(50, interval.TotalMilliseconds * 0.2));

    private static void Observe(Task task) =>
        _ = task.ContinueWith(completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class RunState(Guid sessionId, DateTimeOffset startedUtc, SessionTimer timer)
    {
        public Guid SessionId { get; } = sessionId;
        public DateTimeOffset StartedUtc { get; } = startedUtc.ToUniversalTime();
        public SessionTimer Timer { get; } = timer;
        public int PerformanceObservations { get; set; }
        public int EventObservations { get; set; }
        public int NetworkObservations { get; set; }
        public int TotalEvents { get; set; }
        public long PersistedBytes { get; set; }
        public int TickSamples { get; set; }
        public double MeanTickMilliseconds { get; set; }
        public double MaxTickMilliseconds { get; set; }
        public bool BudgetWarning { get; set; }
        public bool StorageBudgetReached { get; set; }
        public List<string> Limitations { get; } = [];
    }

    private sealed class Sink(MonitoringSource kind, string collectorId, string collectorVersion,
        TimeSpan interval, int expectedTicks)
    {
        public MonitoringSource Kind { get; } = kind;
        public string CollectorId { get; } = collectorId;
        public string CollectorVersion { get; } = collectorVersion;
        public TimeSpan Interval { get; } = interval;
        public int ExpectedTicks { get; } = expectedTicks;
        public int CollectedTicks { get; set; }
        public int Missed { get; set; }
        public int TruncatedTicks { get; set; }
        public int Observations { get; set; }
        public int FilteredEvents { get; set; }
        public bool LimitHit { get; set; }
        public bool SampleLimitHit { get; set; }
        public bool BudgetExceeded { get; set; }
        public bool Unavailable { get; set; }
        public bool Incomplete { get; set; }
        public bool PersistenceFailed { get; set; }
        public int UnavailableTicks { get; set; }
        public List<string> Limitations { get; } = [];
        public List<MonitoringPerformanceObservation> Performance { get; } = [];
        public List<MonitoringEventObservation> Events { get; } = [];
        public List<MonitoringNetworkObservation> Network { get; } = [];
        public List<MonitoringSegmentReference> Segments { get; } = [];
        public int Sequence { get; set; }
        public DateTimeOffset? BufferFrom { get; set; }
        public DateTimeOffset? BufferTo { get; set; }

        public void MarkBuffer(DateTimeOffset at)
        {
            BufferFrom ??= at;
            if (BufferTo is null || at > BufferTo) BufferTo = at;
            if (at < BufferFrom) BufferFrom = at;
        }
    }

    private sealed class Reader(MonitoringSource kind, TimeSpan interval, Sink primary) : IDisposable
    {
        public MonitoringSource Kind { get; } = kind;
        public TimeSpan Interval { get; } = interval;
        public IMonitoringSourceSession? Session { get; set; }
        public Sink Primary { get; } = primary;
        public Sink? Secondary { get; set; }
        public bool Unavailable { get; set; }
        public int Failures { get; set; }
        public TimeSpan NextDue { get; set; }
        public Task? InFlight { get; set; }

        public void Dispose()
        {
            try { Session?.Dispose(); } catch (Exception) { }
        }
    }
}
