using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

public partial class MainWindow
{
    private IReadOnlyList<PerformanceSession> performanceSessions = [];
    private PerformanceSession? selectedPerformanceSession;
    private PerformanceSession? unsavedPerformance;
    private CancellationTokenSource? performanceCancellation;
    private bool performanceBusy;
    private bool performanceSaving;
    private bool performanceClosingAfterSave;
    private bool updatingPerformance;

    private void InitializePerformance()
    {
        updatingPerformance = true;
        PerformanceDurationCombo.ItemsSource = new[]
        {
            new PerformancePresetRow(PerformanceSessionPreset.Seconds30, "30 segundos"),
            new PerformancePresetRow(PerformanceSessionPreset.Minute1, "1 minuto"),
            new PerformancePresetRow(PerformanceSessionPreset.Minutes3, "3 minutos")
        };
        PerformanceDurationCombo.SelectedIndex = 1;
        updatingPerformance = false;
        UpdatePerformanceButtons();
    }

    private PerformanceSessionPreset SelectedPreset() =>
        (PerformanceDurationCombo.SelectedItem as PerformancePresetRow)?.Preset ?? PerformanceSessionPreset.Minute1;

    private async Task ReloadPerformanceAsync(Guid? selectId = null)
    {
        var loaded = await performanceStore.LoadSessionsAsync();
        performanceSessions = loaded.Sessions;
        SetIntegrity("Desempenho", loaded.Warnings);
        RefreshPerformanceHistoryList();
        var target = selectId is { } id
            ? performanceSessions.FirstOrDefault(session => session.Id == id)
            : null;
        target ??= unsavedPerformance;
        if (target is null && selectedPerformanceSession is { } current)
            target = performanceSessions.FirstOrDefault(session => session.Id == current.Id);
        target ??= performanceSessions.FirstOrDefault();
        if (target is null) { ClearPerformanceView(); return; }
        SelectPerformanceInHistory(target.Id);
        ApplyPerformanceSession(target);
    }

    private void RefreshPerformanceHistoryList()
    {
        updatingPerformance = true;
        PerformanceHistoryList.ItemsSource = performanceSessions
            .OrderByDescending(session => session.CompletedUtc)
            .Select(session => new PerformanceHistoryRow(session))
            .ToArray();
        updatingPerformance = false;
    }

    private void SelectPerformanceInHistory(Guid id)
    {
        updatingPerformance = true;
        var row = PerformanceHistoryList.Items.OfType<PerformanceHistoryRow>()
            .FirstOrDefault(item => item.Session.Id == id);
        PerformanceHistoryList.SelectedItem = row;
        if (row is not null) PerformanceHistoryList.ScrollIntoView(row);
        updatingPerformance = false;
    }

    private static PerformanceCoverage Coverage(PerformanceSession session) =>
        session.Coverage ?? PerformanceCoverage.FromLegacy(session);

    private void ApplyPerformanceSession(PerformanceSession session)
    {
        selectedPerformanceSession = session;
        var coverage = Coverage(session);
        var status = ExecutionName(coverage.Execution);
        var samples = session.Statistics.TotalSamples;
        var cpu = session.Statistics.CpuTotalPercent;
        var memory = session.Statistics.MemoryAvailablePercent;
        var processes = session.Statistics.Processes;
        var storage = session.Statistics.Storage;

        PerformanceCpuValue.Text = cpu.Mean is { } mean ? Percent(mean) : "—";
        PerformanceCpuDetail.Text = cpu.ValidSamples > 0
            ? Loc.F($"P95 {Percent(cpu.P95)} · máx. {Percent(cpu.Max)} · {cpu.ValidSamples}/{cpu.TotalSamples} amostras")
            : Loc.T("Indisponível");
        PerformanceMemoryValue.Text = memory.Mean is { } available ? Percent(available) : "—";
        var minimumAvailable = session.Samples
            .Where(sample => sample.Memory is { Valid: true })
            .Select(sample => (ulong?)sample.Memory!.AvailableBytes)
            .DefaultIfEmpty()
            .Min();
        PerformanceMemoryDetail.Text = Loc.F($"mín. disponível {Bytes(minimumAvailable)} · ") +
            Loc.F($"{memory.ValidSamples}/{memory.TotalSamples} amostras");
        PerformanceProcessValue.Text = processes.Count.ToString(CultureInfo.InvariantCulture);
        var topCpu = processes.OrderByDescending(process => process.CpuPercent.Mean ?? -1).FirstOrDefault();
        PerformanceProcessDetail.Text = topCpu is null
            ? Loc.T("Nenhum processo monitorado")
            : Loc.F($"maior CPU: {topCpu.Name} ({Percent(topCpu.CpuPercent.Mean)})");
        PerformanceStorageValue.Text = storage.Count.ToString(CultureInfo.InvariantCulture);
        var peakLatency = storage
            .SelectMany(item => new[] { item.ReadLatencyMs.Max, item.WriteLatencyMs.Max })
            .Where(value => value.HasValue).Select(value => value!.Value)
            .DefaultIfEmpty(double.NaN).Max();
        PerformanceStorageDetail.Text = storage.Count == 0
            ? Loc.T("Nenhum dispositivo observado")
            : Loc.F($"latência máx. {(double.IsNaN(peakLatency) ? "Indisponível" : Milliseconds(peakLatency))}");

        var info = new StringBuilder();
        info.AppendLine(Loc.F($"{status} · cobertura {CoverageName(coverage.Status)} · {PresetName(session.Preset)} · ") +
            Loc.F($"{samples} amostra(s) · {session.Duration.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture)} s · ") +
            Loc.F($"ID {session.Id:D}"));
        info.AppendLine(Loc.F($"{coverage.CollectedSamples}/{coverage.ExpectedSamples} amostra(s) coletada(s) · ") +
            Loc.F($"{coverage.MissedOpportunities} oportunidade(s) perdida(s) · ") +
            Loc.F($"{session.Sources.Count} fonte(s) · {session.Samples.Sum(sample => sample.Exits.Count)} saída(s) · ") +
            Loc.T("causalidade não avaliada"));
        info.AppendLine(Loc.F($"Início {Local(session.StartedUtc)} · conclusão {Local(session.CompletedUtc)}"));
        if (coverage.ProcessLimitReached || coverage.StorageLimitReached || coverage.SampleLimitReached)
            info.AppendLine(Loc.F($"Limites: processos {(coverage.ProcessLimitReached ? "atingido" : "não")} · ") +
                Loc.F($"armazenamento {(coverage.StorageLimitReached ? "atingido" : "não")} · ") +
                Loc.F($"amostras {(coverage.SampleLimitReached ? "atingido" : "não")}"));
        if (coverage.UnavailableCollectors.Count > 0)
            info.AppendLine(Loc.T("Coletores indisponíveis: ") + string.Join(", ", coverage.UnavailableCollectors));
        var slowest = session.Sources.OrderByDescending(source => source.MaxReadMilliseconds ?? 0).FirstOrDefault();
        if (slowest?.MeanReadMilliseconds is { } meanRead)
            info.AppendLine(Loc.F($"Leitura: {slowest.CollectorId} média {Milliseconds(meanRead)} · ") +
                Loc.F($"máx. {Milliseconds(slowest.MaxReadMilliseconds)}"));
        var limitations = coverage.Limitations.Concat(session.Limitations)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (limitations.Length > 0)
            info.AppendLine(Loc.T("Limitações: ") + string.Join(" ", limitations));
        PerformanceStatusText.Text = info.ToString().TrimEnd();

        var quality = PerformanceQualityCatalog.Find(session.Id);
        PerformanceQualityBanner.Visibility = quality is null ? Visibility.Collapsed : Visibility.Visible;
        PerformanceQualityText.Text = quality is null ? string.Empty :
            Loc.F($"Qualidade histórica: {quality.DefectId} — {quality.Reason} ") +
            Loc.F($"Versão afetada: {quality.AffectedVersion}; corrigida em {quality.FixedVersion}. ") +
            Loc.T("Comparação quantitativa bloqueada; registro original preservado.");
        PerformanceProgress.Maximum = Math.Max(1, samples);
        PerformanceProgress.Value = samples;

        PerformanceMetricList.ItemsSource = MetricRows(session);
        PerformanceProcessList.ItemsSource = ProcessRows(session);
        PerformanceStorageList.ItemsSource = StorageRows(session);
        PerformanceObservationList.ItemsSource = session.Observations
            .Select(observation => new PerformanceObservationRow(observation))
            .ToArray();
        PerformanceObservationDetail.Text = session.Observations.Count == 0
            ? Loc.T("Nenhuma observação de evidência foi gerada nesta sessão.")
            : Loc.T("Selecione uma observação para ver valores, janela e limitações.");
        UpdatePerformanceButtons();
    }

    private void ClearPerformanceView()
    {
        selectedPerformanceSession = null;
        updatingPerformance = true;
        PerformanceMetricList.ItemsSource = null;
        PerformanceProcessList.ItemsSource = null;
        PerformanceStorageList.ItemsSource = null;
        PerformanceObservationList.ItemsSource = null;
        PerformanceObservationDetail.Text = string.Empty;
        updatingPerformance = false;
        PerformanceCpuValue.Text = "—";
        PerformanceCpuDetail.Text = Loc.T("Nenhuma sessão disponível.");
        PerformanceMemoryValue.Text = "—";
        PerformanceMemoryDetail.Text = Loc.T("Inicie uma sessão para coletar.");
        PerformanceProcessValue.Text = "—";
        PerformanceProcessDetail.Text = Loc.T("Nenhuma sessão disponível.");
        PerformanceStorageValue.Text = "—";
        PerformanceStorageDetail.Text = Loc.T("Nenhuma sessão disponível.");
        PerformanceProgress.Value = 0;
        PerformanceStatusText.Text = Loc.T("Nenhuma sessão de desempenho realizada. A coleta é somente de leitura.");
        PerformanceQualityBanner.Visibility = Visibility.Collapsed;
        PerformanceQualityText.Text = string.Empty;
        UpdatePerformanceButtons();
    }

    private static IReadOnlyList<PerformanceMetricRow> MetricRows(PerformanceSession session)
    {
        var stats = session.Statistics;
        var rows = new List<PerformanceMetricRow>
        {
            new("CPU total", stats.CpuTotalPercent),
            new("CPU usuário", stats.CpuUserPercent),
            new("CPU kernel", stats.CpuKernelPercent),
            new("Fila do processador", stats.ProcessorQueueLength),
            new("Memória disponível", stats.MemoryAvailablePercent),
            new("Memória usada", stats.MemoryUsedBytes),
            new("Commit", stats.CommitUsagePercent),
            new("Páginas por segundo", stats.PagesPerSecond)
        };
        var self = session.SelfOverhead;
        rows.Add(new PerformanceMetricRow("Auto-sobrecarga · CPU média", Percent(self.CpuPercentMean),
            $"máx. {Percent(self.CpuPercentMax)} · {self.Samples} amostra(s)"));
        rows.Add(new PerformanceMetricRow("Auto-sobrecarga · duração da coleta",
            Milliseconds(self.CollectionDurationMeanMilliseconds),
            $"máx. {Milliseconds(self.CollectionDurationMaxMilliseconds)} · {self.Samples} amostra(s)"));
        foreach (var source in session.Sources)
            rows.Add(new PerformanceMetricRow($"Leitura · {source.CollectorId}",
                Milliseconds(source.MeanReadMilliseconds),
                $"máx. {Milliseconds(source.MaxReadMilliseconds)} · {source.SamplesRead} leitura(s) · " +
                $"{SourceStatusName(source.Status)}"));
        return rows;
    }

    private static IReadOnlyList<PerformanceProcessRow> ProcessRows(PerformanceSession session)
    {
        var rows = session.Statistics.Processes
            .OrderByDescending(process => process.CpuPercent.Mean ?? -1)
            .ThenByDescending(process => process.PrivateBytes.Max ?? 0)
            .Take(20)
            .Select(process => new PerformanceProcessRow(process))
            .ToList();
        var growth = session.Statistics.Processes
            .Where(process => (process.PrivateBytes.GrowthPercent ?? 0) > 0)
            .OrderByDescending(process => process.PrivateBytes.GrowthPercent)
            .Take(5)
            .Select(process => new PerformanceProcessRow(process));
        rows.AddRange(growth.Where(row => rows.All(existing =>
            existing.Process.Identity != row.Process.Identity)));
        return rows;
    }

    private static IReadOnlyList<PerformanceStorageRow> StorageRows(PerformanceSession session) =>
        session.Statistics.Storage
            .OrderBy(item => item.Scope)
            .ThenByDescending(item => item.ReadBytesPerSecond.Mean ?? 0)
            .Select(item => new PerformanceStorageRow(item))
            .ToArray();

    private void OnPerformanceDurationChanged(object sender, SelectionChangedEventArgs e) => UpdatePerformanceButtons();

    private async void OnPerformanceRun(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
            return;

        if (unsavedPerformance is not null)
        {
            performanceBusy = true; performanceSaving = true;
            UpdatePerformanceButtons(); UpdateHardwareButtons();
            PerformanceStatusText.Text = Loc.T("Tentando salvar novamente a sessão pendente…");
            try
            {
                var pending = unsavedPerformance;
                await SavePerformanceAsync(pending);
                unsavedPerformance = null;
                await ReloadPerformanceAsync(pending.Id);
                PerformanceStatusText.Text = Loc.T("Sessão pendente salva novamente.");
            }
            catch (Exception error)
            {
                ShowPerformanceFailure(Loc.T("Falha de gravação da sessão. O resultado permanece nesta janela; tente salvar novamente"), error);
            }
            finally
            {
                performanceBusy = false; performanceSaving = false;
                PerformanceRunButton.Content = unsavedPerformance is null ? Loc.T("Iniciar sessão") : Loc.T("Tentar salvar sessão");
                RunButton.IsEnabled = true;
                UpdatePerformanceButtons(); UpdateHardwareButtons();
                var closeNow = performanceClosingAfterSave && unsavedPerformance is null;
                performanceClosingAfterSave = false;
                if (closeNow) Close();
            }
            return;
        }

        var preset = SelectedPreset();
        var hardware = hardwareSnapshots.FirstOrDefault();
        performanceBusy = true; performanceSaving = false;
        performanceCancellation = new CancellationTokenSource();
        PerformanceRunButton.Content = Loc.T("Iniciar sessão");
        PerformanceProgress.Minimum = 0; PerformanceProgress.Maximum = 1; PerformanceProgress.Value = 0;
        ClearRunWarnings();
        PerformanceQualityBanner.Visibility = Visibility.Collapsed;
        PerformanceQualityText.Text = string.Empty;
        RunButton.IsEnabled = false;
        UpdatePerformanceButtons(); UpdateHardwareButtons();
        var saved = false;
        try
        {
            PerformanceStatusText.Text = Loc.T("Coleta de desempenho em andamento. Leitura somente, sem alterações no Windows.");
            var progress = new Progress<PerformanceProgress>(OnPerformanceProgress);
            var session = await performanceRunner.RunAsync(preset, hardware, progress, performanceCancellation.Token);
            unsavedPerformance = session;
            ApplyPerformanceSession(session);
            performanceSaving = true;
            UpdatePerformanceButtons();
            PerformanceStatusText.Text = Loc.T("Salvando sessão e relatório na memória local…");
            await SavePerformanceAsync(session);
            saved = true;
            unsavedPerformance = null;
            await ReloadPerformanceAsync(session.Id);
            PerformanceStatusText.Text = Loc.F($"Sessão {ExecutionName(Coverage(session).Execution).ToLowerInvariant()} salva. ") +
                Loc.F($"{session.Statistics.TotalSamples} amostra(s), {session.Statistics.Processes.Count} processo(s), ") +
                Loc.F($"{session.Observations.Count} observação(ões). Causalidade não avaliada.");
        }
        catch (Exception error)
        {
            var message = unsavedPerformance is not null
                ? "Falha de gravação da sessão. O resultado permanece nesta janela; tente salvar novamente"
                : saved
                    ? "A sessão foi salva, mas a memória local não pôde ser relida. Reabra o aplicativo"
                    : "A sessão de desempenho não pôde ser concluída";
            ShowPerformanceFailure(message, error);
        }
        finally
        {
            performanceBusy = false; performanceSaving = false;
            performanceCancellation?.Dispose(); performanceCancellation = null;
            PerformanceRunButton.Content = unsavedPerformance is null ? Loc.T("Iniciar sessão") : Loc.T("Tentar salvar sessão");
            RunButton.IsEnabled = true;
            UpdatePerformanceButtons(); UpdateHardwareButtons();
            var closeNow = performanceClosingAfterSave && unsavedPerformance is null;
            performanceClosingAfterSave = false;
            if (closeNow) Close();
        }
    }

    private void OnPerformanceProgress(PerformanceProgress update)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnPerformanceProgress(update)); return; }
        if (performanceSaving) return;
        PerformanceProgress.Maximum = Math.Max(1, update.ExpectedSamples);
        PerformanceProgress.Value = update.SamplesCollected;
        PerformanceStatusText.Text = Loc.F($"Coletando — {update.SamplesCollected}/{update.ExpectedSamples} amostras · ") +
            Loc.F($"{update.Elapsed.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)}/") +
            Loc.F($"{update.Requested.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)} s · leitura somente");
        if (update.Latest is not { } sample) return;
        if (sample.Cpu is { Valid: true } cpu)
        {
            PerformanceCpuValue.Text = Percent(cpu.TotalPercent);
            PerformanceCpuDetail.Text = Loc.F($"usuário {Percent(cpu.UserPercent)} · kernel {Percent(cpu.KernelPercent)}");
        }
        if (sample.Memory is { Valid: true } memory)
        {
            PerformanceMemoryValue.Text = memory.AvailablePercent is { } percent ? Percent(percent) : "—";
            PerformanceMemoryDetail.Text = Loc.F($"{Bytes(memory.AvailableBytes)} disponíveis");
        }
        PerformanceProcessValue.Text = sample.Processes.Count.ToString(CultureInfo.InvariantCulture);
        PerformanceProcessDetail.Text = Loc.T("processos observados");
        PerformanceStorageValue.Text = sample.Storage.Count.ToString(CultureInfo.InvariantCulture);
        PerformanceStorageDetail.Text = Loc.T("dispositivos observados");
    }

    private void OnPerformanceCancel(object sender, RoutedEventArgs e)
    {
        performanceCancellation?.Cancel();
        PerformanceCancelButton.IsEnabled = false;
        PerformanceStatusText.Text = Loc.T("Cancelamento solicitado. Aguardando a última leitura somente de desempenho…");
    }

    private async void OnPerformanceReport(object sender, RoutedEventArgs e)
    {
        if (selectedPerformanceSession is not { } session) return;
        try
        {
            PerformanceReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(performanceStore.GetPerformanceReportPath(session.Id));
        }
        catch (Exception error)
        {
            ShowPerformanceFailure(Loc.T("Não foi possível abrir a localização do relatório de desempenho"), error);
        }
        finally { UpdatePerformanceButtons(); }
    }

    private void OnPerformanceHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPerformance) return;
        if (PerformanceHistoryList.SelectedItem is PerformanceHistoryRow row) ApplyPerformanceSession(row.Session);
    }

    private void OnPerformanceObservationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPerformance) return;
        if (PerformanceObservationList.SelectedItem is not PerformanceObservationRow row)
        {
            PerformanceObservationDetail.Text = string.Empty;
            return;
        }
        var observation = row.Observation;
        var text = new StringBuilder();
        text.AppendLine(observation.Title);
        text.AppendLine(Loc.F($"Regra: {observation.RuleId} {observation.RuleVersion} · tipo: {KindName(observation.Kind)}"));
        text.AppendLine(Loc.F($"Severidade: {EventCatalog.SeverityName(observation.Severity)} · ") +
            Loc.F($"confiança: {EventCatalog.ConfidenceName(observation.Confidence)} · causalidade: não avaliada"));
        text.AppendLine();
        text.AppendLine(observation.Statement);
        text.AppendLine();
        text.AppendLine(Loc.F($"Métrica: {observation.Metric} ({observation.Unit})") +
            (observation.Threshold is { } threshold ? Loc.F($" · limiar: {FormatValue(observation.Unit, threshold)}") : string.Empty));
        if (observation.Subject is not null || observation.SubjectName is not null)
            text.AppendLine(Loc.F($"Sujeito: {observation.Subject ?? "Indisponível"}") +
                (observation.SubjectName is null ? string.Empty : Loc.F($" ({observation.SubjectName})")));
        text.AppendLine(Loc.F($"Janela: {Local(observation.WindowFromUtc)} até {Local(observation.WindowToUtc)} · ") +
            Loc.F($"{observation.Duration.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)} s"));
        text.AppendLine(Loc.F($"Amostras de suporte: {observation.SupportingSampleCount} de {observation.EvaluatedSampleCount}"));
        text.AppendLine(Loc.F($"Valores observados — mínimo: {FormatValue(observation.Unit, observation.ObservedMin)}; ") +
            Loc.F($"máximo: {FormatValue(observation.Unit, observation.ObservedMax)}; ") +
            Loc.F($"média: {FormatValue(observation.Unit, observation.ObservedMean)}; ") +
            Loc.F($"P95: {FormatValue(observation.Unit, observation.ObservedP95)}"));
        text.AppendLine(Loc.F($"Fonte: {observation.Source}"));
        if (observation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in observation.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        }
        PerformanceObservationDetail.Text = text.ToString();
    }

    private async Task SavePerformanceAsync(PerformanceSession session)
    {
        await performanceStore.SaveSessionAsync(session, CancellationToken.None);
    }

    private void UpdatePerformanceButtons()
    {
        var idle = !operations.IsBusy && !performanceBusy && !busy && !hardwareBusy && !eventBusy && !networkBusy && !graphicsBusy &&
            !correlationBusy && !monitoringBusy;
        PerformanceRunButton.IsEnabled = idle;
        PerformanceCancelButton.IsEnabled = performanceBusy && !performanceSaving &&
            performanceCancellation is { IsCancellationRequested: false };
        PerformanceDurationCombo.IsEnabled = !performanceBusy && !performanceSaving;
        PerformanceHistoryList.IsEnabled = !performanceBusy;
        PerformanceReportButton.IsEnabled = !performanceBusy && selectedPerformanceSession is not null &&
            File.Exists(performanceStore.GetPerformanceReportPath(selectedPerformanceSession.Id));
        UpdateNetworkButtons();
    }

    private void ShowPerformanceFailure(string message, Exception error)
    {
        logFailure(error);
        PerformanceStatusText.Text = message + ".";
        ShowWarning(Loc.F($"{message}. {error.GetType().Name}: {error.Message}"));
    }

    private static string ExecutionName(PerformanceExecutionStatus execution) => execution switch
    {
        PerformanceExecutionStatus.Running => "Em andamento",
        PerformanceExecutionStatus.Completed => "Concluída",
        PerformanceExecutionStatus.Cancelled => "Cancelada",
        PerformanceExecutionStatus.Failed => "Falhou",
        PerformanceExecutionStatus.Unsupported => "Não suportada",
        _ => execution.ToString()
    };

    private static string CoverageName(PerformanceCoverageStatus status) => status switch
    {
        PerformanceCoverageStatus.Complete => "completa",
        PerformanceCoverageStatus.Partial => "parcial",
        PerformanceCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    };

    private static string SourceStatusName(PerformanceSourceStatus status) => status switch
    {
        PerformanceSourceStatus.Running => "em andamento",
        PerformanceSourceStatus.Completed => "concluída",
        PerformanceSourceStatus.Partial => "parcial",
        PerformanceSourceStatus.Failed => "falhou",
        PerformanceSourceStatus.Unsupported => "não suportada",
        PerformanceSourceStatus.Cancelled => "cancelada",
        _ => status.ToString()
    };

    private static string PresetName(PerformanceSessionPreset preset) => preset switch
    {
        PerformanceSessionPreset.Seconds30 => "30 segundos",
        PerformanceSessionPreset.Minute1 => "1 minuto",
        PerformanceSessionPreset.Minutes3 => "3 minutos",
        PerformanceSessionPreset.Custom => "personalizado",
        _ => preset.ToString()
    };

    private static string KindName(PerformanceObservationKind kind) => kind switch
    {
        PerformanceObservationKind.SustainedCpu => "CPU sustentada",
        PerformanceObservationKind.LowAvailableMemory => "memória disponível baixa",
        PerformanceObservationKind.CommitMargin => "margem de commit",
        PerformanceObservationKind.PrivateMemoryGrowth => "crescimento de memória privada",
        PerformanceObservationKind.DiskLatency => "latência de disco",
        PerformanceObservationKind.IoPeak => "pico de I/O",
        _ => kind.ToString()
    };

    private static string Percent(double? value) =>
        value is { } number ? number.ToString("F1", CultureInfo.CurrentCulture) + "%" : "Indisponível";

    private static string Milliseconds(double? value) =>
        value is { } number ? number.ToString("F1", CultureInfo.CurrentCulture) + " ms" : "Indisponível";

    private static string Bytes(ulong? value) => value is { } number ? Bytes((double)number) : "Indisponível";

    private static string Bytes(double? value)
    {
        if (value is not { } number || double.IsNaN(number) || double.IsInfinity(number) || number < 0) return "Indisponível";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = number; var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString("F1", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string FormatValue(string unit, double? value)
    {
        if (value is not { } number) return "Indisponível";
        return unit switch
        {
            "%" => Percent(number),
            "B" or "bytes" => Bytes(number),
            "ms" => Milliseconds(number),
            "/s" => number.ToString("F1", CultureInfo.CurrentCulture) + " " + unit,
            _ => number.ToString("F2", CultureInfo.CurrentCulture) + (unit.Length == 0 ? string.Empty : " " + unit)
        };
    }

    private sealed record PerformancePresetRow(PerformanceSessionPreset Preset, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PerformanceHistoryRow(PerformanceSession Session)
    {
        public string DateText => Session.CompletedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        public string SummaryText
        {
            get
            {
                var reports = Session.Observations.Count;
                var coverage = Coverage(Session);
                var quality = PerformanceQualityCatalog.Find(Session.Id) is null
                    ? string.Empty : " · histórico afetado";
                return $"{ExecutionName(coverage.Execution)} · cobertura {CoverageName(coverage.Status)} · " +
                    $"{PresetName(Session.Preset)} · {Session.Statistics.TotalSamples} amostra(s) · " +
                    $"{reports} observação(ões){quality}";
            }
        }
        public override string ToString() => DateText;
    }

    private sealed record PerformanceMetricRow(string Name, string Value, string Detail)
    {
        public PerformanceMetricRow(string name, MetricStatistics statistics)
            : this(name, FormatValue(statistics.Unit, statistics.Mean),
                statistics.ValidSamples == 0
                    ? $"Nenhuma amostra válida em {statistics.TotalSamples}."
                    : $"P95 {FormatValue(statistics.Unit, statistics.P95)} · máx. {FormatValue(statistics.Unit, statistics.Max)} · " +
                      $"{statistics.ValidSamples}/{statistics.TotalSamples} amostras")
        {
        }

        public override string ToString() => Name;
    }

    private sealed record PerformanceProcessRow(ProcessStatistics Process)
    {
        public string Name => $"{Process.Name} · PID {Process.Pid}";
        public string Cpu => Percent(Process.CpuPercent.Mean);
        public string Memory => $"privado máx. {Bytes(Process.PrivateBytes.Max)}";
        public string Detail
        {
            get
            {
                var growth = Process.PrivateBytes.GrowthPercent is { } value
                    ? $"{value.ToString("F1", CultureInfo.CurrentCulture)}%" : "—";
                var io = $"I/O leitura {Bytes(Process.IoReadBytesPerSecond.Mean)}/s · " +
                    $"escrita {Bytes(Process.IoWriteBytesPerSecond.Mean)}/s";
                return $"crescimento {growth} · {io}";
            }
        }
        public override string ToString() => Name;
    }

    private sealed record PerformanceStorageRow(StorageStatistics Storage)
    {
        public string Name => Storage.Scope == StorageScope.Physical
            ? $"{Storage.InstanceName} · disco físico" + (Storage.DiskNumber is { } number ? $" {number}" : string.Empty)
            : $"{Storage.InstanceName} · volume" + (Storage.VolumeLetter is { } letter ? $" {letter}:" : string.Empty);
        public string Throughput => $"leitura {Bytes(Storage.ReadBytesPerSecond.Mean)}/s · " +
            $"escrita {Bytes(Storage.WriteBytesPerSecond.Mean)}/s";
        public string Detail => $"latência leitura {Milliseconds(Storage.ReadLatencyMs.Max)} · " +
            $"latência escrita {Milliseconds(Storage.WriteLatencyMs.Max)} · " +
            $"fila máx. {(Storage.QueueLength.Max is { } queue ? queue.ToString("F1", CultureInfo.CurrentCulture) : "—")} · " +
            $"atividade máx. {Percent(Storage.PercentActiveTime.Max)}";
        public override string ToString() => Name;
    }

    private sealed record PerformanceObservationRow(PerformanceObservation Observation)
    {
        public string Title => Observation.Title;
        public string Subtitle => $"{KindName(Observation.Kind)} · {EventCatalog.SeverityName(Observation.Severity)} · " +
            $"confiança {EventCatalog.ConfidenceName(Observation.Confidence)} · causalidade não avaliada";
        public override string ToString() => Title;
    }
}
