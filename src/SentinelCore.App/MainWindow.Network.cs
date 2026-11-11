using static SentinelCore.App.NetworkPresentationRows;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Persistence;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

public partial class MainWindow
{
    private IReadOnlyList<NetworkSession> networkSessions = [];
    private NetworkSession? selectedNetworkSession;
    private NetworkSession? unsavedNetwork;
    private CancellationTokenSource? networkCancellation;
    private bool networkBusy;
    private bool networkSaving;
    private bool networkClosingAfterSave;
    private bool updatingNetwork;

    private void InitializeNetwork()
    {
        updatingNetwork = true;
        NetworkPresetCombo.DisplayMemberPath = "Label";
        NetworkPresetCombo.ItemsSource = new[]
        {
            new NetworkPresetRow(NetworkSessionPreset.Snapshot, "Instantâneo"),
            new NetworkPresetRow(NetworkSessionPreset.Seconds30, "30 segundos"),
            new NetworkPresetRow(NetworkSessionPreset.Minute1, "1 minuto")
        };
        NetworkPresetCombo.SelectedIndex = 0;
        NetworkAddressCheck.IsChecked = false;
        updatingNetwork = false;
        ClearNetworkView();
        UpdateNetworkButtons();
    }

    private NetworkSessionPreset SelectedNetworkPreset() =>
        NetworkPresetCombo.SelectedItem is NetworkPresetRow row ? row.Preset : NetworkSessionPreset.Snapshot;

    private async Task ReloadNetworksAsync(Guid? selectId = null)
    {
        var snapshot = await networkStore.LoadNetworkSessionsAsync();
        networkSessions = snapshot.Sessions;
        SetIntegrity("Rede", snapshot.Warnings);
        RefreshNetworkHistoryList();

        var target = selectId is { } id
            ? networkSessions.FirstOrDefault(s => s.Id == id)
            : unsavedNetwork
              ?? (selectedNetworkSession is { } current ? networkSessions.FirstOrDefault(s => s.Id == current.Id) : null)
              ?? networkSessions.FirstOrDefault();

        if (target is null)
        {
            ClearNetworkView();
            return;
        }

        SelectNetworkInHistory(target.Id);
        ApplyNetworkSession(target);
    }

    private void RefreshNetworkHistoryList()
    {
        updatingNetwork = true;
        NetworkHistoryList.ItemsSource = networkSessions
            .OrderByDescending(s => s.CompletedUtc)
            .Select(s => new NetworkHistoryRow(s))
            .ToArray();
        updatingNetwork = false;
    }

    private void SelectNetworkInHistory(Guid id)
    {
        updatingNetwork = true;
        var row = NetworkHistoryList.Items.OfType<NetworkHistoryRow>().FirstOrDefault(r => r.Session.Id == id);
        NetworkHistoryList.SelectedItem = row;
        if (row is not null) NetworkHistoryList.ScrollIntoView(row);
        updatingNetwork = false;
    }

    private void ApplyNetworkSession(NetworkSession session)
    {
        selectedNetworkSession = session;
        var stats = session.Statistics;
        var coverage = session.Coverage;
        var persisted = networkSessions.Any(s => s.Id == session.Id) &&
            File.Exists(networkStore.GetNetworkReportPath(session.Id));

        NetworkProgress.Minimum = 0;
        NetworkProgress.Maximum = Math.Max(1, coverage?.ExpectedSamples ?? Math.Max(1, session.Samples.Count));
        NetworkProgress.Value = coverage?.CollectedSamples ?? session.Samples.Count;

        NetworkStatusText.Text =
            Loc.F($"Sessão de {Local(session.CompletedUtc)} · {StatusName(session.Status)} · cobertura {NetworkPresentationRows.CoverageName(coverage)} · ") +
            Loc.F($"{session.Samples.Count} amostra(s) em {session.Duration.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture)} s · ") +
            Loc.F($"ID {session.Id:D}") +
            (persisted ? string.Empty : Loc.T(" · não confirmada na memória local"));

        NetworkTcpValue.Text = stats.FinalTcpConnections.ToString(CultureInfo.CurrentCulture);
        NetworkTcpDetail.Text =
            Loc.F($"pico {stats.PeakTcpConnections} · média {Number(stats.MeanTcpConnections)} · ") +
            Loc.F($"escuta {stats.FinalListening} · TIME_WAIT {stats.FinalTimeWait}");

        NetworkUdpValue.Text = stats.FinalUdpEndpoints.ToString(CultureInfo.CurrentCulture);
        NetworkUdpDetail.Text =
            Loc.F($"pico {stats.PeakUdpEndpoints} · média {Number(stats.MeanUdpEndpoints)} · ") +
            Loc.F($"portas locais {stats.FinalDistinctLocalPorts}");

        NetworkEphemeralValue.Text = stats.FinalEphemeralEndpoints.ToString(CultureInfo.CurrentCulture);
        NetworkEphemeralDetail.Text = stats.FinalEphemeralSharePercent is { } share
            ? Loc.F($"{share.ToString("F1", CultureInfo.CurrentCulture)}% dos endpoints locais")
            : Loc.T("participação indisponível");

        NetworkProcessValue.Text = stats.FinalTopProcesses.Count.ToString(CultureInfo.CurrentCulture);
        var topProc = stats.FinalTopProcesses.FirstOrDefault();
        NetworkProcessDetail.Text = topProc is not null
            ? Loc.F($"topo: {topProc.Name ?? $"PID {topProc.Pid}"} ({topProc.TcpConnections + topProc.UdpEndpoints})")
            : Loc.T("nenhum processo atribuído");

        NetworkPortList.ItemsSource = stats.FinalTopLocalPorts.Select(p => new NetworkPortRow(p)).ToArray();
        NetworkStateList.ItemsSource = stats.FinalStates.Select(s => new NetworkStateRow(s)).ToArray();
        NetworkProcessList.ItemsSource = stats.FinalTopProcesses.Select(p => new NetworkProcessRow(p)).ToArray();
        NetworkRangeList.ItemsSource = session.PortRanges.Select(r => new NetworkRangeRow(r)).ToArray();

        updatingNetwork = true;
        var observationRows = session.Observations.Select(o => new NetworkObservationRow(o)).ToArray();
        NetworkObservationList.ItemsSource = observationRows;
        NetworkObservationList.SelectedIndex = observationRows.Length > 0 ? 0 : -1;
        updatingNetwork = false;

        if (observationRows.Length > 0)
            OnNetworkObservationSelected(NetworkObservationList, null!);
        else
            NetworkObservationDetail.Text = BuildNetworkSummaryText(session);

        UpdateNetworkButtons();
    }

    private void ClearNetworkView()
    {
        selectedNetworkSession = null;
        NetworkProgress.Minimum = 0;
        NetworkProgress.Maximum = 1;
        NetworkProgress.Value = 0;
        NetworkStatusText.Text =
            Loc.T("Nenhuma sessão de rede selecionada. Escolha Instantâneo, 30 segundos ou 1 minuto (somente leitura, sem tráfego ativo).");
        NetworkTcpValue.Text = "—";
        NetworkTcpDetail.Text = Loc.T("escuta, estabelecidas e TIME_WAIT");
        NetworkUdpValue.Text = "—";
        NetworkUdpDetail.Text = Loc.T("endpoints UDP locais");
        NetworkEphemeralValue.Text = "—";
        NetworkEphemeralDetail.Text = Loc.T("ocupação da faixa dinâmica");
        NetworkProcessValue.Text = "—";
        NetworkProcessDetail.Text = Loc.T("processos com mais endpoints");
        NetworkPortList.ItemsSource = null;
        NetworkStateList.ItemsSource = null;
        NetworkProcessList.ItemsSource = null;
        NetworkRangeList.ItemsSource = null;
        updatingNetwork = true;
        NetworkObservationList.ItemsSource = null;
        updatingNetwork = false;
        NetworkObservationDetail.Text =
            Loc.T("Nenhuma observação de rede selecionada. A coleta é somente de leitura e não executa ping, DNS ativo ou captura de pacotes.");
        UpdateNetworkButtons();
    }

    private static string BuildNetworkSummaryText(NetworkSession session)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.T("Nenhuma observação sustentada superou os limiares conservadores nesta sessão."));
        text.AppendLine(Loc.T("Causalidade não avaliada."));
        if (session.TcpipEvents is { } tcpip)
        {
            text.AppendLine();
            text.AppendLine(Loc.F($"Contexto Tcpip: {NetworkTcpipEventContext.FormatCounts(tcpip)}. {tcpip.Note}"));
        }

        if (NetworkQualityCatalog.Evaluate(session) is { } quality)
        {
            text.AppendLine();
            text.AppendLine(quality.Summary);
        }

        if (session.AnalysisNotes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Regras avaliadas"));
            foreach (var note in session.AnalysisNotes)
                text.AppendLine(Loc.F($"• {note.RuleId}: {note.Detail}"));
        }

        if (session.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in session.Limitations)
                text.AppendLine(Loc.F($"• {limitation}"));
        }

        return text.ToString();
    }

    private async void OnNetworkRun(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
            return;

        if (unsavedNetwork is not null)
        {
            networkBusy = true;
            networkSaving = true;
            RunButton.IsEnabled = false;
            UpdateNetworkButtons();
            UpdatePerformanceButtons();
            UpdateHardwareButtons();
            UpdateEventButtons();
            NetworkStatusText.Text = Loc.T("Tentando salvar novamente a sessão pendente…");
            try
            {
                var pending = unsavedNetwork;
                await networkStore.SaveNetworkSessionAsync(pending, CancellationToken.None);
                unsavedNetwork = null;
                await ReloadNetworksAsync(pending.Id);
                NetworkStatusText.Text = Loc.T("Sessão pendente salva novamente.");
            }
            catch (Exception error)
            {
                ShowNetworkFailure(Loc.T("Falha de gravação da sessão. O resultado permanece nesta janela; ") +
                    Loc.T("tente salvar novamente"), error);
            }
            finally
            {
                networkBusy = false;
                networkSaving = false;
                RunButton.IsEnabled = !busy;
                NetworkRunButton.Content = unsavedNetwork is null ? Loc.T("Coletar agora") : Loc.T("Tentar salvar sessão");
                UpdateNetworkButtons();
                UpdatePerformanceButtons();
                UpdateHardwareButtons();
                UpdateEventButtons();
                var closeNow = networkClosingAfterSave && unsavedNetwork is null;
                networkClosingAfterSave = false;
                if (closeNow) Close();
            }
            return;
        }

        var preset = SelectedNetworkPreset();
        var includeAddresses = NetworkAddressCheck.IsChecked == true;
        var events = selectedEventInvestigation ?? eventInvestigations.FirstOrDefault();
        networkBusy = true;
        networkSaving = false;
        networkCancellation = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        NetworkRunButton.Content = Loc.T("Coletar agora");
        NetworkProgress.Minimum = 0;
        NetworkProgress.Maximum = 1;
        NetworkProgress.Value = 0;
        ClearRunWarnings();
        UpdateNetworkButtons();
        UpdatePerformanceButtons();
        UpdateHardwareButtons();
        UpdateEventButtons();
        var saved = false;
        try
        {
            NetworkStatusText.Text = preset == NetworkSessionPreset.Snapshot
                ? Loc.T("Instantâneo em andamento. Leitura somente, sem conexões ativas.")
                : Loc.T("Coleta de rede em andamento. Leitura somente, sem conexões ativas.");
            var progress = new Progress<NetworkProgress>(OnNetworkProgress);
            var session = await networkRunner.RunAsync(NetworkSessionRequest.ForPreset(preset, includeAddresses),
                events, progress, networkCancellation.Token);
            unsavedNetwork = session;
            ApplyNetworkSession(session);
            networkSaving = true;
            UpdateNetworkButtons();
            NetworkStatusText.Text = Loc.T("Salvando sessão e relatório na memória local…");
            await networkStore.SaveNetworkSessionAsync(session, CancellationToken.None);
            saved = true;
            unsavedNetwork = null;
            await ReloadNetworksAsync(session.Id);
            NetworkStatusText.Text = Loc.F($"Sessão {StatusName(session.Status).ToLowerInvariant()} salva. ") +
                Loc.F($"{session.Samples.Count} amostra(s), {session.Statistics.FinalTcpConnections} conexão(ões) TCP, ") +
                Loc.F($"{session.Observations.Count} observação(ões). Causalidade não avaliada.");
        }
        catch (Exception error)
        {
            var message = unsavedNetwork is not null
                ? "Falha de gravação da sessão. O resultado permanece nesta janela; tente salvar novamente"
                : saved
                    ? "A sessão foi salva, mas a memória local não pôde ser relida. Reabra o aplicativo"
                    : "A sessão de rede não pôde ser concluída";
            ShowNetworkFailure(message, error);
        }
        finally
        {
            networkBusy = false;
            networkSaving = false;
            networkCancellation?.Dispose();
            networkCancellation = null;
            RunButton.IsEnabled = !busy;
            NetworkRunButton.Content = unsavedNetwork is null ? Loc.T("Coletar agora") : Loc.T("Tentar salvar sessão");
            UpdateNetworkButtons();
            UpdatePerformanceButtons();
            UpdateHardwareButtons();
            UpdateEventButtons();
            var closeNow = networkClosingAfterSave && unsavedNetwork is null;
            networkClosingAfterSave = false;
            if (closeNow) Close();
        }
    }

    private void OnNetworkCancel(object sender, RoutedEventArgs e)
    {
        networkCancellation?.Cancel();
        NetworkCancelButton.IsEnabled = false;
        NetworkStatusText.Text = Loc.T("Cancelamento solicitado. Aguardando a última leitura somente de rede…");
    }

    private async void OnNetworkReport(object sender, RoutedEventArgs e)
    {
        if (selectedNetworkSession is not { } session) return;
        try
        {
            NetworkReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(networkStore.GetNetworkReportPath(session.Id));
        }
        catch (Exception error)
        {
            ShowNetworkFailure(Loc.T("Não foi possível abrir a localização do relatório de rede"), error);
        }
        finally
        {
            UpdateNetworkButtons();
        }
    }

    private void OnNetworkPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingNetwork) return;
        UpdateNetworkButtons();
    }

    private void OnNetworkAddressOptionChanged(object sender, RoutedEventArgs e)
    {
        if (updatingNetwork) return;
        if (NetworkAddressCheck.IsChecked == true)
        {
            ShowWarning(Loc.T("Endereços local e remoto passarão a ser gravados no histórico local desta sessão. ") +
                Loc.T("Eles podem identificar dispositivos da sua rede; desative a opção se não precisar desse dado."));
        }
    }

    private void OnNetworkProgress(NetworkProgress update)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnNetworkProgress(update));
            return;
        }

        if (networkSaving) return;
        NetworkProgress.Maximum = Math.Max(1, update.ExpectedSamples);
        NetworkProgress.Value = update.SamplesCollected;
        NetworkStatusText.Text = Loc.F($"Coletando — {update.SamplesCollected}/{update.ExpectedSamples} amostra(s) · ") +
            Loc.F($"{update.Elapsed.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)}/") +
            Loc.F($"{update.Requested.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)} s · leitura somente");
        if (update.Latest is not { } sample) return;
        NetworkTcpValue.Text = sample.TcpConnections.ToString(CultureInfo.CurrentCulture);
        NetworkTcpDetail.Text = Loc.F($"escuta {sample.ListeningTcp} · portas {sample.DistinctLocalPorts}");
        NetworkUdpValue.Text = sample.UdpEndpoints.ToString(CultureInfo.CurrentCulture);
        NetworkUdpDetail.Text = Loc.T("endpoints UDP observados");
        NetworkEphemeralValue.Text =
            (sample.EphemeralTcpConnections + sample.EphemeralUdpEndpoints).ToString(CultureInfo.CurrentCulture);
        NetworkEphemeralDetail.Text = Loc.T("endpoints na faixa dinâmica");
        NetworkProcessValue.Text = sample.TopProcesses.Count.ToString(CultureInfo.CurrentCulture);
        NetworkProcessDetail.Text = Loc.T("processos com mais endpoints");
    }

    private void OnNetworkHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingNetwork) return;
        if (NetworkHistoryList.SelectedItem is NetworkHistoryRow row)
            ApplyNetworkSession(row.Session);
    }

    private void OnNetworkObservationSelected(object sender, SelectionChangedEventArgs? e)
    {
        if (updatingNetwork) return;
        if (NetworkObservationList.SelectedItem is not NetworkObservationRow row)
        {
            NetworkObservationDetail.Text = selectedNetworkSession is { } current
                ? BuildNetworkSummaryText(current)
                : string.Empty;
            return;
        }

        var observation = row.Observation;
        var text = new StringBuilder();
        text.AppendLine(observation.Title);
        text.AppendLine(Loc.F($"Regra: {observation.RuleId} {observation.RuleVersion} · tipo: {NetworkPresentationRows.KindName(observation.Kind)}"));
        text.AppendLine(Loc.F($"Severidade: {Loc.T(EventCatalog.SeverityName(observation.Severity))} · ") +
            Loc.F($"confiança: {Loc.T(EventCatalog.ConfidenceName(observation.Confidence))} · causalidade: não avaliada"));
        text.AppendLine();
        text.AppendLine(observation.Statement);
        text.AppendLine();
        text.AppendLine(Loc.F($"Métrica: {observation.Metric} ({observation.Unit})") +
            (observation.Threshold is { } threshold
                ? Loc.F($" · limiar: {threshold.ToString("0.##", CultureInfo.CurrentCulture)}")
                : string.Empty));
        if (observation.Subject is not null || observation.SubjectName is not null)
            text.AppendLine(Loc.F($"Sujeito: {observation.Subject ?? "Indisponível"}") +
                (observation.SubjectName is null ? string.Empty : Loc.F($" ({observation.SubjectName})")));
        text.AppendLine(Loc.F($"Janela: {Local(observation.WindowFromUtc)} até {Local(observation.WindowToUtc)} · ") +
            Loc.F($"{observation.Duration.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)} s"));
        text.AppendLine(Loc.F($"Amostras de suporte: {observation.SupportingSampleCount} de {observation.EvaluatedSampleCount}"));
        text.AppendLine(Loc.F($"Valores observados — mínimo: {Value(observation.ObservedMin)}; ") +
            Loc.F($"máximo: {Value(observation.ObservedMax)}; média: {Value(observation.ObservedMean)}; ") +
            Loc.F($"P95: {Value(observation.ObservedP95)}"));
        text.AppendLine(Loc.F($"Fonte: {observation.Source}"));
        if (observation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in observation.Limitations)
                text.AppendLine(Loc.F($"• {limitation}"));
        }

        NetworkObservationDetail.Text = text.ToString();
    }

    private void UpdateNetworkButtons()
    {
        var idle = !operations.IsBusy && !networkBusy && !busy && !hardwareBusy && !eventBusy && !performanceBusy && !graphicsBusy &&
            !correlationBusy && !monitoringBusy;
        NetworkRunButton.IsEnabled = idle;
        NetworkCancelButton.IsEnabled = networkBusy && !networkSaving &&
            networkCancellation is { IsCancellationRequested: false };
        NetworkPresetCombo.IsEnabled = !networkBusy && !networkSaving;
        NetworkAddressCheck.IsEnabled = !networkBusy && !networkSaving;
        NetworkHistoryList.IsEnabled = !networkBusy;
        NetworkReportButton.IsEnabled = !networkBusy && selectedNetworkSession is not null &&
            File.Exists(networkStore.GetNetworkReportPath(selectedNetworkSession.Id));
        UpdateGraphicsButtons();
    }

    private void ShowNetworkFailure(string message, Exception error)
    {
        logFailure(error);
        NetworkStatusText.Text = message + ".";
        ShowWarning(Loc.F($"{message}. {error.GetType().Name}: {error.Message}"));
    }

}
