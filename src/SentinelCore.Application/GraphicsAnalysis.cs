using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Projeta itens dos coletores existentes de hardware em adaptadores, monitores e drivers gráficos.
/// Nenhuma consulta WMI própria é feita aqui: os itens vêm dos coletores da Fase 2, reutilizados pelo runner.
/// </summary>
internal static class GraphicsProjection
{
    internal const string AdapterSource = @"WMI root\cimv2: Win32_VideoController + Win32_PnPSignedDriver";
    internal const string MonitorSource = "DisplayConfig (QueryDisplayConfig) + WMI root\\wmi: WmiMonitorID";
    internal const string DriverSource = @"WMI root\cimv2: Win32_PnPSignedDriver + SetupAPI";

    public static IReadOnlyList<GraphicsAdapter> BuildAdapters(IEnumerable<HardwareItem> items,
        GraphicsLimits limits, out bool truncated)
    {
        var ordered = items.OrderBy(item => item.StableId, StringComparer.Ordinal).ToArray();
        truncated = ordered.Length > limits.MaxAdapters;
        return ordered.Take(limits.MaxAdapters).Select(adapter =>
        {
            var limitations = adapter.Limitations.ToList();
            if (string.IsNullOrWhiteSpace(adapter.DriverVersion))
                limitations.Add("Versão do driver de vídeo não exposta por esta interface.");
            var ram = ParseULong(adapter.Property("adapter_ram_wmi_bytes"));
            if (ram is null)
                limitations.Add("AdapterRAM não reportado; a VRAM real não é inferida.");
            return new GraphicsAdapter(
                adapter.StableId, adapter.Name, adapter.Manufacturer, adapter.Model, adapter.HardwareId,
                adapter.DriverProvider, adapter.DriverVersion, adapter.DriverDateUtc, adapter.DriverClass,
                adapter.DriverInfName, adapter.WindowsState, adapter.Presence, ram, AdapterSource,
                adapter.Properties, limitations.Distinct(StringComparer.Ordinal).ToArray());
        }).ToArray();
    }

    public static IReadOnlyList<GraphicsMonitor> BuildMonitors(IEnumerable<HardwareItem> items,
        GraphicsLimits limits, out bool truncated)
    {
        var ordered = items.OrderBy(item => item.StableId, StringComparer.Ordinal).ToArray();
        truncated = ordered.Length > limits.MaxMonitors;
        return ordered.Take(limits.MaxMonitors).Select(monitor =>
        {
            var limitations = monitor.Limitations.ToList();
            var modes = (monitor.Property("supported_resolutions") ?? string.Empty)
                .Split("; ", StringSplitOptions.RemoveEmptyEntries).ToArray();
            var modesTruncated = ParseFlag(monitor.Property("supported_modes_truncated")) == true ||
                monitor.Limitations.Any(limitation =>
                    limitation.Contains("modos suportados limitados", StringComparison.OrdinalIgnoreCase));
            if (monitor.Property("active_width") is null || monitor.Property("active_height") is null)
                limitations.Add("Resolução ativa não confirmada; o valor não é estimado.");
            return new GraphicsMonitor(
                monitor.StableId, monitor.Name, monitor.Manufacturer, monitor.Model, monitor.HardwareId,
                monitor.WindowsState, monitor.Presence,
                ParseUInt(monitor.Property("active_width")), ParseUInt(monitor.Property("active_height")),
                monitor.Property("active_refresh_hz"), ParseFlag(monitor.Property("hdr_supported")),
                ParseFlag(monitor.Property("hdr_enabled")), monitor.Property("connector"),
                monitor.Property("gdi_device"), modes, modesTruncated, MonitorSource,
                monitor.Properties, limitations.Distinct(StringComparer.Ordinal).ToArray());
        }).ToArray();
    }

    public static IReadOnlyList<GraphicsDriver> BuildDrivers(IReadOnlyList<GraphicsAdapter> adapters,
        IEnumerable<HardwareItem> driverItems, GraphicsLimits limits, out bool truncated)
    {
        var drivers = new List<GraphicsDriver>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var adapter in adapters)
        {
            if (!HasDriverData(adapter)) continue;
            var limitations = adapter.Limitations.Where(limitation =>
                limitation.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
                limitation.Contains("INF", StringComparison.OrdinalIgnoreCase)).ToList();
            drivers.Add(new GraphicsDriver(adapter.StableId, adapter.Name, adapter.DriverProvider,
                adapter.DriverVersion, adapter.DriverDateUtc, adapter.DriverClass, adapter.DriverInfName,
                adapter.HardwareId, adapter.Presence, adapter.WindowsState, AdapterSource, limitations));
            seen.Add(adapter.StableId);
        }

        var adapterHardwareIds = adapters.Select(adapter => adapter.HardwareId)
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in driverItems.OrderBy(item => item.StableId, StringComparer.Ordinal))
        {
            if (!IsDisplayDriver(item)) continue;
            if (seen.Contains(item.StableId)) continue;
            if (item.HardwareId is not null && adapterHardwareIds.Contains(item.HardwareId)) continue;
            var limitations = item.Limitations.ToList();
            if (string.IsNullOrWhiteSpace(item.DriverVersion))
                limitations.Add("Versão do driver não exposta por esta interface.");
            drivers.Add(new GraphicsDriver(item.StableId, item.Name, item.DriverProvider, item.DriverVersion,
                item.DriverDateUtc, item.DriverClass, item.DriverInfName, item.HardwareId, item.Presence,
                item.WindowsState, DriverSource, limitations.Distinct(StringComparer.Ordinal).ToArray()));
            seen.Add(item.StableId);
        }

        var ordered = drivers.OrderBy(driver => driver.StableId, StringComparer.Ordinal).ToArray();
        truncated = ordered.Length > limits.MaxDrivers;
        return ordered.Take(limits.MaxDrivers).ToArray();
    }

    public static IReadOnlyList<GraphicsEventEvidence> BuildEvents(EventInvestigation? investigation,
        GraphicsLimits limits, out bool truncated)
    {
        if (investigation is null)
        {
            truncated = false;
            return [];
        }

        var graphics = investigation.Events
            .Where(item => item.GroupId.StartsWith("graphics:", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.OccurredUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        truncated = graphics.Length > limits.MaxEvents;
        return graphics.Take(limits.MaxEvents).Select(item =>
        {
            var limitations = item.Limitations.ToList();
            var message = item.Message;
            var messageTruncated = item.MessageTruncated;
            if (message is { Length: > 0 } && message.Length > limits.MaxMessageChars)
            {
                message = message[..limits.MaxMessageChars];
                messageTruncated = true;
            }
            if (messageTruncated)
                limitations.Add("Mensagem truncada no limite de evidência gráfica ou na coleta de origem; " +
                    "os identificadores técnicos foram preservados.");
            if (GraphicsEvidenceClassification.IsDisplayDriverReset(item.Provider, item.EventId))
                limitations.Add("Evento Display 4101 registra reinício do driver de vídeo; não identifica a causa do tempo limite.");
            return new GraphicsEventEvidence(item.Id, item.GroupId, item.Channel, item.Provider, item.EventId,
                item.RecordId, item.OccurredUtc, item.Level, item.TaskCode, item.ActivityId, message,
                messageTruncated, GraphicsEvidenceClassification.ClassifyEvent(item.Provider, item.EventId),
                item.Data, limitations.Distinct(StringComparer.Ordinal).ToArray());
        }).ToArray();
    }

    public static IReadOnlyList<GraphicsWerEvidence> BuildWerReports(EventInvestigation? investigation,
        GraphicsLimits limits, out bool truncated)
    {
        if (investigation is null)
        {
            truncated = false;
            return [];
        }

        var reports = investigation.Reports
            .Where(item => GraphicsEvidenceClassification.IsLiveKernelKind(
                GraphicsEvidenceClassification.ClassifyWer(item.EventType, item.ProblemCode)))
            .Where(item => IsWithinReportedWindow(item, investigation))
            .OrderByDescending(item => item.EventTimeUtc ?? item.CreationTimeUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        truncated = reports.Length > limits.MaxWerReports;
        return reports.Take(limits.MaxWerReports).Select(item =>
        {
            var limitations = item.Limitations.ToList();
            limitations.Add("Metadados do WER não substituem o dump; nenhum dump foi lido, enviado ou apagado.");
            limitations.Add("A presença de um relatório não prova falha de hardware nem identifica a causa.");
            return new GraphicsWerEvidence(item.Id, item.GroupId, item.ReportId, item.ReportIdFromFile,
                item.Source, item.Store, item.FolderName, item.EventType, item.ProblemCode, item.EventTimeUtc,
                item.CreationTimeUtc, item.ReportStatus, item.Application, item.Module, item.ModuleVersion,
                item.BucketId, GraphicsEvidenceClassification.ClassifyWer(item.EventType, item.ProblemCode),
                item.Confidence, item.Data, limitations.Distinct(StringComparer.Ordinal).ToArray());
        }).ToArray();
    }

    private static bool IsWithinReportedWindow(WerEvidence report, EventInvestigation investigation)
    {
        var effective = report.EventTimeUtc ?? report.CreationTimeUtc;
        return effective is null ||
            effective >= investigation.PeriodFromUtc && effective <= investigation.PeriodToUtc;
    }

    private static bool HasDriverData(GraphicsAdapter adapter) =>
        !string.IsNullOrWhiteSpace(adapter.DriverProvider) ||
        !string.IsNullOrWhiteSpace(adapter.DriverVersion) ||
        adapter.DriverDateUtc is not null ||
        !string.IsNullOrWhiteSpace(adapter.DriverClass) ||
        !string.IsNullOrWhiteSpace(adapter.DriverInfName);

    private static bool IsDisplayDriver(HardwareItem item)
    {
        if (item.DriverClass?.Contains("display", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (item.Property("pnp_class")?.Contains("display", StringComparison.OrdinalIgnoreCase) == true) return true;
        return false;
    }

    private static ulong? ParseULong(string? value) =>
        ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static uint? ParseUInt(string? value) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool? ParseFlag(string? value) => value switch
    {
        "Sim" => true,
        "Não" => false,
        _ => null
    };
}

public sealed record GraphicsAnalysis(
    IReadOnlyList<GraphicsObservation> Observations,
    IReadOnlyList<GraphicsAnalysisNote> Notes);

/// <summary>
/// Regras determinísticas de evidência gráfica. Nenhuma regra atribui causa: um TDR, um LiveKernelEvent ou uma
/// mudança de driver não provam defeito físico, superaquecimento, VRAM defeituosa ou instabilidade da GPU.
/// </summary>
public sealed class GraphicsEvidenceAnalyzer
{
    public const string TdrRuleId = "GFX-TDR-DISPLAY-4101";
    public const string LiveKernelRulePrefix = "GFX-LIVEKERNEL";
    public const string MultiAdapterRuleId = "GFX-MULTI-ADAPTER";
    public const string AdapterStateRuleId = "GFX-ADAPTER-STATE";
    public const string MonitorTopologyRuleId = "GFX-MONITOR-TOPOLOGY";
    public const string WerRuleId = "GFX-WER-GRAPHICS";
    public const string AdapterChangeRuleId = "GFX-ADAPTER-CHANGE";
    public const string DriverChangeRuleId = "GFX-DRIVER-CHANGE";
    public const string MonitorChangeRuleId = "GFX-MONITOR-CHANGE";
    public const string RuleVersion = "1.0";

    public GraphicsAnalysis Analyze(
        IReadOnlyList<GraphicsAdapter> adapters,
        IReadOnlyList<GraphicsMonitor> monitors,
        IReadOnlyList<GraphicsEventEvidence> events,
        IReadOnlyList<GraphicsWerEvidence> werReports,
        GraphicsChangeResult changes,
        DateTimeOffset windowFromUtc,
        DateTimeOffset windowToUtc,
        bool eventsAvailable,
        bool werAvailable)
    {
        var observations = new List<GraphicsObservation>();
        var notes = new List<GraphicsAnalysisNote>();

        AddTdr(events, eventsAvailable, windowFromUtc, windowToUtc, observations, notes);
        foreach (var code in GraphicsEvidenceClassification.LiveKernelProblemCodes)
            AddLiveKernel(code, werReports, werAvailable, windowFromUtc, windowToUtc, observations, notes);
        AddMultiAdapter(adapters, windowFromUtc, windowToUtc, observations, notes);
        AddAdapterState(adapters, windowFromUtc, windowToUtc, observations, notes);
        AddMonitorTopology(monitors, windowFromUtc, windowToUtc, observations, notes);
        AddWer(werReports, werAvailable, windowFromUtc, windowToUtc, observations, notes);
        AddChanges(changes, windowFromUtc, windowToUtc, observations, notes);
        return new GraphicsAnalysis(observations, notes);
    }

    private static void AddChanges(GraphicsChangeResult changes, DateTimeOffset from, DateTimeOffset to,
        List<GraphicsObservation> observations, List<GraphicsAnalysisNote> notes)
    {
        AddChange(AdapterChangeRuleId, GraphicsObservationKind.AdapterChange, changes.AdaptersComparable,
            changes.Changes.Where(change => change.Type is GraphicsChangeType.AdapterAdded
                or GraphicsChangeType.AdapterRemoved or GraphicsChangeType.StateChanged).ToArray());
        AddChange(DriverChangeRuleId, GraphicsObservationKind.DriverChange, changes.DriversComparable,
            changes.Changes.Where(change => change.Type == GraphicsChangeType.DriverChanged).ToArray());
        AddChange(MonitorChangeRuleId, GraphicsObservationKind.MonitorChange, changes.MonitorsComparable,
            changes.Changes.Where(change => change.Type is GraphicsChangeType.MonitorAdded
                or GraphicsChangeType.MonitorRemoved or GraphicsChangeType.ConfigurationChanged).ToArray());

        void AddChange(string ruleId, GraphicsObservationKind kind, bool comparable,
            IReadOnlyList<GraphicsChange> selected)
        {
            if (!comparable)
            {
                notes.Add(new(ruleId, RuleVersion, false,
                    "Alterações não avaliadas: a leitura anterior ou a atual estava indisponível, parcial ou truncada.",
                    GraphicsNoteReason.PreconditionNotObserved));
                return;
            }
            if (selected.Count == 0)
            {
                notes.Add(new(ruleId, RuleVersion, true, "Nenhuma alteração observada entre as investigações.",
                    GraphicsNoteReason.ConditionNotObserved));
                return;
            }

            observations.Add(new GraphicsObservation(
                $"{ruleId}@{from.UtcDateTime:O}", ruleId, RuleVersion, kind,
                kind switch
                {
                    GraphicsObservationKind.DriverChange => "Alteração de driver entre investigações",
                    GraphicsObservationKind.MonitorChange => "Alteração de configuração de monitor",
                    _ => "Alteração de adaptador entre investigações"
                },
                $"{selected.Count} alteração(ões) observada(s) entre a investigação anterior e esta. " +
                "A alteração é descritiva: não indica melhoria, regressão, causa ou relação com qualquer evento.",
                EvidenceSeverity.Informational, EvidenceConfidence.High, CausalityStatus.NotAssessed,
                kind switch
                {
                    GraphicsObservationKind.DriverChange => "graphics.driver.changes",
                    GraphicsObservationKind.MonitorChange => "graphics.monitor.changes",
                    _ => "graphics.adapter.changes"
                }, "alterações", selected.Count, from, to, null, null, selected.Count,
                "Comparação entre investigações gráficas",
                ["Alteração observada não é causa, correção ou prova de instabilidade."]));
            notes.Add(new(ruleId, RuleVersion, true, "Alterações observadas entre investigações.",
                GraphicsNoteReason.Observed));
        }
    }

    private static void AddTdr(IReadOnlyList<GraphicsEventEvidence> events, bool available,
        DateTimeOffset from, DateTimeOffset to, List<GraphicsObservation> observations,
        List<GraphicsAnalysisNote> notes)
    {
        if (!available)
        {
            notes.Add(new(TdrRuleId, RuleVersion, false,
                "Fonte de eventos indisponível; a ausência de TDR não foi observada.", GraphicsNoteReason.Unavailable));
            return;
        }
        var tdr = events.Where(item => item.Kind == GraphicsEventKind.DisplayDriverReset).ToArray();
        if (tdr.Length == 0)
        {
            notes.Add(new(TdrRuleId, RuleVersion, true,
                "Nenhum evento Display 4101 observado na janela. A ausência não prova que não houve reinício do " +
                "driver em outro período ou que o registro não tenha sido perdido.",
                GraphicsNoteReason.ConditionNotObserved));
            return;
        }

        observations.Add(new GraphicsObservation(
            $"{TdrRuleId}@{tdr.Min(item => item.OccurredUtc).UtcDateTime:O}",
            TdrRuleId, RuleVersion, GraphicsObservationKind.DisplayDriverReset,
            "Reinício do driver de vídeo registrado pelo Windows",
            $"O Windows registrou {tdr.Length} evento(s) Display {GraphicsEvidenceClassification.DisplayDriverResetEventId} " +
            "na janela. Esse é o registro de parada e recuperação do driver de vídeo (TDR). Ele não identifica a causa " +
            "do tempo limite, não prova defeito físico da GPU e não confirma que uma tela preta específica tenha sido " +
            "causada por esse evento.",
            EvidenceSeverity.ObservedImpact, EvidenceConfidence.High, CausalityStatus.NotAssessed,
            "graphics.display.tdr.events", "eventos", tdr.Length,
            from, to, tdr.Min(item => item.OccurredUtc), tdr.Max(item => item.OccurredUtc), tdr.Length,
            "Log de Eventos do Windows — provedor Display",
            ["Evento registrado não identifica causa, componente físico ou correlação com tela preta."]));
        notes.Add(new(TdrRuleId, RuleVersion, true, "Eventos Display 4101 observados na janela.",
            GraphicsNoteReason.Observed));
    }

    private static void AddLiveKernel(int code, IReadOnlyList<GraphicsWerEvidence> reports, bool available,
        DateTimeOffset from, DateTimeOffset to, List<GraphicsObservation> observations,
        List<GraphicsAnalysisNote> notes)
    {
        var ruleId = $"{LiveKernelRulePrefix}-{code}";
        if (!available)
        {
            notes.Add(new(ruleId, RuleVersion, false,
                "Fonte de metadados WER indisponível; a ausência de LiveKernelEvent não foi observada.",
                GraphicsNoteReason.Unavailable));
            return;
        }
        var codeKind = GraphicsEvidenceClassification.KindForCode(code);
        var matches = reports.Where(item => item.Kind == codeKind).ToArray();
        if (matches.Length == 0)
        {
            notes.Add(new(ruleId, RuleVersion, true,
                $"Nenhum relatório WER LiveKernelEvent com ProblemCode {code} observado na janela. Ausência de " +
                "relatório não prova ausência de falha: o WER pode não ter preservado o metadata.",
                GraphicsNoteReason.ConditionNotObserved));
            return;
        }

        var first = matches.Select(item => item.EventTimeUtc ?? item.CreationTimeUtc)
            .Where(item => item is not null).Min();
        var last = matches.Select(item => item.EventTimeUtc ?? item.CreationTimeUtc)
            .Where(item => item is not null).Max();
        observations.Add(new GraphicsObservation(
            $"{ruleId}@{first?.ToString("O") ?? "sem-horario"}",
            ruleId, RuleVersion, GraphicsObservationKind.LiveKernelEvent,
            $"LiveKernelEvent {code} preservado pelo Windows Error Reporting",
            $"O WER preservou {matches.Length} relatório(s) LiveKernelEvent com ProblemCode {code} na janela. " +
            "O código é mantido exatamente como reportado; esta fase não amplia seu significado oficial, não lê " +
            "dumps e não conclui que exista defeito físico, superaquecimento, problema de VRAM ou instabilidade da GPU.",
            EvidenceSeverity.ObservedImpact, EvidenceConfidence.Medium, CausalityStatus.NotAssessed,
            $"graphics.livekernel.{code}.reports", "relatórios", matches.Length,
            from, to, first, last, matches.Length, "Windows Error Reporting — metadados",
            ["Código do LiveKernelEvent não é diagnóstico; nenhum dump foi lido.",
             "Relatório ausente ou não preservado não prova ausência do evento."]));
        notes.Add(new(ruleId, RuleVersion, true,
            $"Relatórios LiveKernelEvent {code} observados na janela.", GraphicsNoteReason.Observed));
    }

    private static void AddMultiAdapter(IReadOnlyList<GraphicsAdapter> adapters, DateTimeOffset from,
        DateTimeOffset to, List<GraphicsObservation> observations, List<GraphicsAnalysisNote> notes)
    {
        if (adapters.Count <= 1)
        {
            notes.Add(new(MultiAdapterRuleId, RuleVersion, true,
                adapters.Count == 0
                    ? "Nenhum adaptador gráfico enumerado; a fonte de GPU pode estar indisponível ou parcial."
                    : "Um adaptador gráfico enumerado.",
                adapters.Count == 0
                    ? GraphicsNoteReason.Unavailable
                    : GraphicsNoteReason.ConditionNotObserved));
            return;
        }

        observations.Add(new GraphicsObservation(
            $"{MultiAdapterRuleId}@{from.UtcDateTime:O}",
            MultiAdapterRuleId, RuleVersion, GraphicsObservationKind.MultipleAdapters,
            "Múltiplos adaptadores gráficos enumerados",
            $"{adapters.Count} adaptadores gráficos foram enumerados, incluindo adaptadores virtuais ou " +
            "secundários quando reportados pelo Windows. A quantidade não é um defeito e não indica qual adaptador " +
            "está associado a um evento ou a uma saída de vídeo.",
            EvidenceSeverity.Informational, EvidenceConfidence.High, CausalityStatus.NotAssessed,
            "graphics.adapters.count", "adaptadores", adapters.Count, from, to, null, null, adapters.Count,
            "Inventário de GPU (WMI)",
            ["A enumeração não confirma uso efetivo, saúde ou integridade de cada adaptador."]));
        notes.Add(new(MultiAdapterRuleId, RuleVersion, true, "Múltiplos adaptadores observados.",
            GraphicsNoteReason.Observed));
    }

    private static void AddAdapterState(IReadOnlyList<GraphicsAdapter> adapters, DateTimeOffset from,
        DateTimeOffset to, List<GraphicsObservation> observations, List<GraphicsAnalysisNote> notes)
    {
        if (adapters.Count == 0)
        {
            notes.Add(new(AdapterStateRuleId, RuleVersion, false,
                "Nenhum adaptador disponível para avaliar estado reportado.", GraphicsNoteReason.Unavailable));
            return;
        }
        var flagged = adapters.Where(HasReportedError).ToArray();
        if (flagged.Length == 0)
        {
            notes.Add(new(AdapterStateRuleId, RuleVersion, true,
                "Nenhum código de erro de configuração ou estado de erro reportado para os adaptadores observados. " +
                "Isso não atesta integridade do hardware.",
                GraphicsNoteReason.ConditionNotObserved));
            return;
        }

        observations.Add(new GraphicsObservation(
            $"{AdapterStateRuleId}@{from.UtcDateTime:O}",
            AdapterStateRuleId, RuleVersion, GraphicsObservationKind.AdapterState,
            "Adaptador gráfico com estado de erro reportado",
            $"{flagged.Length} adaptador(es) reportaram código de erro de configuração ou estado de erro. " +
            $"Valores observados: {string.Join("; ", flagged.Select(adapter =>
                $"{adapter.Name ?? "adaptador"} — código {adapter.Property("config_manager_error_code") ?? "indisponível"}, " +
                $"estado {adapter.WindowsState ?? "indisponível"}"))}. O estado é o reportado pelo Windows; não " +
            "substitui teste de hardware e não identifica a causa do erro.",
            EvidenceSeverity.PotentialImpact, EvidenceConfidence.High, CausalityStatus.NotAssessed,
            "graphics.adapters.error_state.count", "adaptadores", flagged.Length, from, to, null, null,
            flagged.Length, "Inventário de GPU (WMI/ConfigMgr)",
            ["Código de erro reportado não é diagnóstico definitivo nem prova defeito físico."]));
        notes.Add(new(AdapterStateRuleId, RuleVersion, true, "Estado de erro reportado por adaptador.",
            GraphicsNoteReason.Observed));
    }

    private static void AddMonitorTopology(IReadOnlyList<GraphicsMonitor> monitors, DateTimeOffset from,
        DateTimeOffset to, List<GraphicsObservation> observations, List<GraphicsAnalysisNote> notes)
    {
        if (monitors.Count == 0)
        {
            notes.Add(new(MonitorTopologyRuleId, RuleVersion, true,
                "Nenhum monitor ativo reportado pelo DisplayConfig. Saídas desconectadas ou não mapeadas não são " +
                "inferidas como defeito.", GraphicsNoteReason.Unavailable));
            return;
        }

        var active = monitors.Count(monitor => monitor.Width is > 0 && monitor.Height is > 0);
        observations.Add(new GraphicsObservation(
            $"{MonitorTopologyRuleId}@{from.UtcDateTime:O}",
            MonitorTopologyRuleId, RuleVersion, GraphicsObservationKind.MonitorTopology,
            "Topologia de exibição observada",
            $"{monitors.Count} monitor(es) ativo(s) foram reportados; {active} com resolução ativa confirmada. " +
            "HDR, G-SYNC/VRR e frequência são apenas os valores reportados por DisplayConfig/WMI; esta fase não " +
            "infere suporte além do reportado e não altera nenhuma configuração de exibição.",
            EvidenceSeverity.Informational, EvidenceConfidence.High, CausalityStatus.NotAssessed,
            "graphics.monitors.active.count", "monitores", monitors.Count, from, to, null, null,
            monitors.Count, "DisplayConfig + WmiMonitorID",
            ["Topologia observada no instante da coleta; monitores desconectados podem não ser enumerados."]));
        notes.Add(new(MonitorTopologyRuleId, RuleVersion, true, "Monitores ativos observados.",
            GraphicsNoteReason.Observed));
    }

    private static void AddWer(IReadOnlyList<GraphicsWerEvidence> reports, bool available, DateTimeOffset from,
        DateTimeOffset to, List<GraphicsObservation> observations, List<GraphicsAnalysisNote> notes)
    {
        if (!available)
        {
            notes.Add(new(WerRuleId, RuleVersion, false,
                "Fonte de metadados WER indisponível; a ausência de relatórios não foi observada.",
                GraphicsNoteReason.Unavailable));
            return;
        }
        if (reports.Count == 0)
        {
            notes.Add(new(WerRuleId, RuleVersion, true,
                "Nenhum relatório WER classificado como gráfico na janela. Ausência de relatório não prova " +
                "ausência de falha.", GraphicsNoteReason.ConditionNotObserved));
            return;
        }

        var first = reports.Select(item => item.EventTimeUtc ?? item.CreationTimeUtc)
            .Where(item => item is not null).Min();
        var last = reports.Select(item => item.EventTimeUtc ?? item.CreationTimeUtc)
            .Where(item => item is not null).Max();
        observations.Add(new GraphicsObservation(
            $"{WerRuleId}@{from.UtcDateTime:O}",
            WerRuleId, RuleVersion, GraphicsObservationKind.WerMetadata,
            "Metadados gráficos preservados pelo Windows Error Reporting",
            $"{reports.Count} relatório(s) WER com classificação gráfica foram preservados na janela. Somente " +
            "metadados são lidos; nenhum dump é aberto. A presença do relatório não confirma falha de hardware, " +
            "não identifica a causa e não demonstra que um aplicativo específico tenha causado a falha.",
            EvidenceSeverity.Informational, EvidenceConfidence.Medium, CausalityStatus.NotAssessed,
            "graphics.wer.reports", "relatórios", reports.Count, from, to, first, last, reports.Count,
            "Windows Error Reporting — metadados",
            ["Metadados podem faltar ou estar incompletos; ausência não prova ausência de falha."]));
        notes.Add(new(WerRuleId, RuleVersion, true, "Relatórios WER gráficos observados.",
            GraphicsNoteReason.Observed));
    }

    private static bool HasReportedError(GraphicsAdapter adapter) =>
        adapter.Property("config_manager_error_code") is { } code && code != "0" ||
        adapter.WindowsState?.Contains("error", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Compara a investigação atual com a anterior. Adições e remoções só são avaliadas quando as fontes relevantes
/// estavam disponíveis nas duas leituras; a comparação nunca atribui causa.
/// </summary>
public sealed record GraphicsChangeResult(
    IReadOnlyList<GraphicsChange> Changes,
    bool Comparable,
    bool AdaptersComparable,
    bool MonitorsComparable,
    bool DriversComparable,
    IReadOnlyList<string> Limitations);

public static class GraphicsChangeEngine
{
    /// <summary>
    /// Adições e remoções só são avaliadas quando as duas leituras — anterior e atual — estão completas e sem
    /// truncamento. Uma falha na leitura atual nunca é convertida em "dispositivo removido".
    /// </summary>
    public static GraphicsChangeResult Compare(GraphicsInvestigation? previous,
        IReadOnlyList<GraphicsAdapter> adapters, IReadOnlyList<GraphicsMonitor> monitors,
        IReadOnlyList<GraphicsDriver> drivers, bool currentAdaptersComparable,
        bool currentMonitorsComparable, bool currentDriversComparable)
    {
        if (previous is null)
            return new([], false, false, false, false,
                ["Não há investigação gráfica anterior para comparar."]);
        if (HasDuplicateAdapterIdentity(previous.Adapters) || HasDuplicateAdapterIdentity(adapters) ||
            HasDuplicateMonitorIdentity(previous.Monitors) || HasDuplicateMonitorIdentity(monitors) ||
            HasDuplicateDriverIdentity(previous.Drivers) || HasDuplicateDriverIdentity(drivers))
            throw new InvalidDataException("Identidade gráfica duplicada; a comparação não foi executada.");

        var limitations = new List<string>();
        var changes = new List<GraphicsChange>();
        var previousAdaptersComparable = !previous.Coverage.AdaptersUnavailable &&
            previous.Sources.Any(source => source.Category == GraphicsSourceCategory.Adapters &&
                source.Status == GraphicsSourceStatus.Completed && !source.LimitHit);
        var previousMonitorsComparable = !previous.Coverage.MonitorsUnavailable &&
            previous.Sources.Any(source => source.Category == GraphicsSourceCategory.Monitors &&
                source.Status == GraphicsSourceStatus.Completed && !source.LimitHit);
        var previousDriversComparable = !previous.Coverage.DriversUnavailable &&
            previous.Sources.Any(source => source.Category == GraphicsSourceCategory.Drivers &&
                source.Status == GraphicsSourceStatus.Completed && !source.LimitHit);
        var adaptersComparable = previousAdaptersComparable && currentAdaptersComparable;
        var monitorsComparable = previousMonitorsComparable && currentMonitorsComparable;
        var driversComparable = previousDriversComparable && currentDriversComparable;

        if (!adaptersComparable)
            limitations.Add("Adaptadores: leitura anterior ou atual parcial/indisponível ou truncada; " +
                "adições e remoções não foram avaliadas.");
        if (!monitorsComparable)
            limitations.Add("Monitores: leitura anterior ou atual parcial/indisponível ou truncada; " +
                "adições e remoções não foram avaliadas.");
        if (!driversComparable)
            limitations.Add("Drivers: leitura anterior ou atual parcial/indisponível; alterações não foram avaliadas.");

        CompareById(previous.Adapters, adapters, adaptersComparable,
            GraphicsChangeType.AdapterAdded, GraphicsChangeType.AdapterRemoved,
            CompareAdapter);
        CompareById(previous.Monitors, monitors, monitorsComparable,
            GraphicsChangeType.MonitorAdded, GraphicsChangeType.MonitorRemoved,
            CompareMonitor);
        if (driversComparable) CompareDrivers(previous.Drivers, drivers);

        var ordered = changes
            .OrderBy(change => change.Type)
            .ThenBy(change => change.Subject, StringComparer.Ordinal)
            .ThenBy(change => change.Field, StringComparer.Ordinal)
            .ToArray();
        return new(ordered, adaptersComparable || monitorsComparable || driversComparable,
            adaptersComparable, monitorsComparable, driversComparable, limitations);

        void CompareById<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, bool evaluatePresence,
            GraphicsChangeType added, GraphicsChangeType removed, Action<T, T> compare)
            where T : notnull
        {
            var beforeById = before.ToDictionary(item => Id(item), StringComparer.Ordinal);
            var afterById = after.ToDictionary(item => Id(item), StringComparer.Ordinal);
            if (!evaluatePresence) return;
            foreach (var (id, item) in afterById)
                if (!beforeById.ContainsKey(id))
                    changes.Add(new(added, id, Name(item), null, null, null));
            foreach (var (id, item) in beforeById)
                if (!afterById.ContainsKey(id))
                    changes.Add(new(removed, id, Name(item), null, null, null));
            foreach (var (id, oldItem) in beforeById)
                if (afterById.TryGetValue(id, out var newItem))
                    compare(oldItem, newItem);
        }

        // Campos de driver são comparados apenas aqui, em uma única entrada por driver. Comparar os mesmos campos
        // no adaptador duplicaria a alteração e inflaria a contagem.
        void CompareAdapter(GraphicsAdapter before, GraphicsAdapter after)
        {
            CompareField(GraphicsChangeType.StateChanged, before.StableId, before.Name, "windows_state",
                before.WindowsState, after.WindowsState);
            CompareField(GraphicsChangeType.StateChanged, before.StableId, before.Name, "presence",
                before.Presence.ToString(), after.Presence.ToString());
            CompareField(GraphicsChangeType.StateChanged, before.StableId, before.Name, "reported_adapter_ram",
                before.ReportedAdapterRamBytes?.ToString(CultureInfo.InvariantCulture),
                after.ReportedAdapterRamBytes?.ToString(CultureInfo.InvariantCulture));
        }

        void CompareMonitor(GraphicsMonitor before, GraphicsMonitor after)
        {
            CompareField(GraphicsChangeType.ConfigurationChanged, before.StableId, before.Name, "width",
                before.Width?.ToString(CultureInfo.InvariantCulture), after.Width?.ToString(CultureInfo.InvariantCulture));
            CompareField(GraphicsChangeType.ConfigurationChanged, before.StableId, before.Name, "height",
                before.Height?.ToString(CultureInfo.InvariantCulture), after.Height?.ToString(CultureInfo.InvariantCulture));
            CompareField(GraphicsChangeType.ConfigurationChanged, before.StableId, before.Name, "refresh_hz",
                before.RefreshHz, after.RefreshHz);
            CompareField(GraphicsChangeType.ConfigurationChanged, before.StableId, before.Name, "hdr_enabled",
                before.HdrEnabled?.ToString(), after.HdrEnabled?.ToString());
        }

        void CompareDrivers(IReadOnlyList<GraphicsDriver> before, IReadOnlyList<GraphicsDriver> after)
        {
            var beforeById = before.ToDictionary(item => item.StableId, StringComparer.Ordinal);
            foreach (var item in after)
                if (beforeById.TryGetValue(item.StableId, out var oldItem))
                {
                    CompareField(GraphicsChangeType.DriverChanged, item.StableId, item.Name, "driver_version",
                        oldItem.Version, item.Version);
                    CompareField(GraphicsChangeType.DriverChanged, item.StableId, item.Name, "driver_provider",
                        oldItem.Provider, item.Provider);
                    CompareField(GraphicsChangeType.DriverChanged, item.StableId, item.Name, "driver_inf",
                        oldItem.InfName, item.InfName);
                    CompareField(GraphicsChangeType.DriverChanged, item.StableId, item.Name, "driver_date",
                        oldItem.DateUtc?.ToUniversalTime().ToString("O"),
                        item.DateUtc?.ToUniversalTime().ToString("O"));
                }
        }

        void CompareField(GraphicsChangeType type, string subject, string? name, string field,
            string? before, string? after)
        {
            if (string.Equals(before, after, StringComparison.Ordinal)) return;
            changes.Add(new(type, subject, name, field, before, after));
        }

        static string Id<T>(T item) => item switch
        {
            GraphicsAdapter adapter => adapter.StableId,
            GraphicsMonitor monitor => monitor.StableId,
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };

        static string? Name<T>(T item) => item switch
        {
            GraphicsAdapter adapter => adapter.Name,
            GraphicsMonitor monitor => monitor.Name,
            _ => null
        };

        static bool HasDuplicateAdapterIdentity(IReadOnlyList<GraphicsAdapter> items) =>
            items.Select(item => item.StableId).Distinct(StringComparer.Ordinal).Count() != items.Count;

        static bool HasDuplicateMonitorIdentity(IReadOnlyList<GraphicsMonitor> items) =>
            items.Select(item => item.StableId).Distinct(StringComparer.Ordinal).Count() != items.Count;

        static bool HasDuplicateDriverIdentity(IReadOnlyList<GraphicsDriver> items) =>
            items.Select(item => item.StableId).Distinct(StringComparer.Ordinal).Count() != items.Count;
    }
}
