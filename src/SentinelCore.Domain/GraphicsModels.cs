namespace SentinelCore.Domain;

public enum GraphicsSourceCategory { Adapters, Monitors, Drivers, Events, Wer }
public enum GraphicsSourceStatus { Running, Completed, Partial, Failed, Unsupported, Cancelled }
public enum GraphicsExecutionStatus { Running, Completed, Cancelled, Failed, Unsupported }
public enum GraphicsCoverageStatus { Complete, Partial, Unavailable }
public enum GraphicsInvestigationStatus { Running, Completed, Partial, Failed, Cancelled, Unsupported }

/// <summary>
/// Classificação conservadora de uma evidência de evento. Nenhum valor identifica causa ou defeito físico.
/// </summary>
public enum GraphicsEventKind
{
    DisplayDriverReset, DisplayDriverOther, GraphicsDriverVendor, GraphicsProvider
}

/// <summary>
/// Códigos LiveKernelEvent de vídeo preservados nesta fase. <c>LiveKernelEventOther</c> mantém o código
/// original sem inferir significado.
/// </summary>
public enum GraphicsWerKind
{
    LiveKernelEvent117, LiveKernelEvent141, LiveKernelEvent142, LiveKernelEventOther, GraphicsWerOther
}

public enum GraphicsObservationKind
{
    DisplayDriverReset, LiveKernelEvent, MultipleAdapters, AdapterState, MonitorTopology,
    AdapterChange, DriverChange, MonitorChange, WerMetadata
}

/// <summary>
/// Motivo de uma regra não ter produzido observação. Distingue ausência de evidência de condição observada.
/// </summary>
public enum GraphicsNoteReason
{
    NotSpecified, Unavailable, InsufficientEvidence, PreconditionNotObserved,
    ConditionNotObserved, Observed
}

public enum GraphicsChangeType
{
    AdapterAdded, AdapterRemoved, MonitorAdded, MonitorRemoved,
    DriverChanged, ConfigurationChanged, StateChanged
}

/// <summary>
/// Identificadores e códigos compartilhados entre o runner, o relatório e a interface. O evento Display 4101 é
/// o único código que esta fase trata como reinício do driver de vídeo (TDR) registrado pelo Windows.
/// </summary>
public static class GraphicsEvidenceClassification
{
    public const string EventLogCollectorId = "event-log";
    public const string WerMetadataCollectorId = "wer-metadata";
    public const int DisplayDriverResetEventId = 4101;
    public const int LiveKernelEvent117 = 117;
    public const int LiveKernelEvent141 = 141;
    public const int LiveKernelEvent142 = 142;

    public static readonly IReadOnlyList<int> LiveKernelProblemCodes =
        [LiveKernelEvent117, LiveKernelEvent141, LiveKernelEvent142];

    public static bool IsDisplayDriverReset(string? provider, int eventId) =>
        eventId == DisplayDriverResetEventId &&
        provider?.Contains("display", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsGraphicsProvider(string? provider) =>
        provider?.Contains("display", StringComparison.OrdinalIgnoreCase) == true ||
        provider?.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase) == true ||
        provider?.Contains("nvidia", StringComparison.OrdinalIgnoreCase) == true ||
        provider?.Contains("amdkmdap", StringComparison.OrdinalIgnoreCase) == true ||
        provider?.Contains("amd", StringComparison.OrdinalIgnoreCase) == true ||
        provider?.Contains("igfx", StringComparison.OrdinalIgnoreCase) == true ||
        provider?.Contains("intel", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsLiveKernelCode(string? problemCode) =>
        int.TryParse(problemCode, out var code) && LiveKernelProblemCodes.Contains(code);

    public static GraphicsEventKind ClassifyEvent(string? provider, int eventId)
    {
        if (IsDisplayDriverReset(provider, eventId)) return GraphicsEventKind.DisplayDriverReset;
        if (provider?.Contains("display", StringComparison.OrdinalIgnoreCase) == true)
            return GraphicsEventKind.DisplayDriverOther;
        if (provider?.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase) == true ||
            provider?.Contains("nvidia", StringComparison.OrdinalIgnoreCase) == true ||
            provider?.Contains("amdkmdap", StringComparison.OrdinalIgnoreCase) == true ||
            provider?.Contains("amd", StringComparison.OrdinalIgnoreCase) == true ||
            provider?.Contains("igfx", StringComparison.OrdinalIgnoreCase) == true ||
            provider?.Contains("intel", StringComparison.OrdinalIgnoreCase) == true)
            return GraphicsEventKind.GraphicsDriverVendor;
        return GraphicsEventKind.GraphicsProvider;
    }

    public static GraphicsWerKind ClassifyWer(string? eventType, string? problemCode)
    {
        // O código isolado não define o tipo do relatório: BlueScreen/AppCrash com o mesmo número não são
        // LiveKernelEvent. O tipo é exigido antes de qualquer classificação por código.
        if (!IsLiveKernelType(eventType))
            return GraphicsWerKind.GraphicsWerOther;
        return int.TryParse(problemCode, out var code)
            ? KindForCode(code)
            : GraphicsWerKind.LiveKernelEventOther;
    }

    public static GraphicsWerKind KindForCode(int code) => code switch
    {
        LiveKernelEvent117 => GraphicsWerKind.LiveKernelEvent117,
        LiveKernelEvent141 => GraphicsWerKind.LiveKernelEvent141,
        LiveKernelEvent142 => GraphicsWerKind.LiveKernelEvent142,
        _ => GraphicsWerKind.LiveKernelEventOther
    };

    public static bool IsLiveKernelKind(GraphicsWerKind kind) =>
        kind is GraphicsWerKind.LiveKernelEvent117 or GraphicsWerKind.LiveKernelEvent141
            or GraphicsWerKind.LiveKernelEvent142;

    public static int CountLiveKernelReports(IEnumerable<GraphicsWerEvidence> reports) =>
        reports.Count(report => IsLiveKernelKind(report.Kind));

    public static bool IsLiveKernelType(string? eventType) =>
        string.Equals(eventType, "LiveKernelEvent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventType, "Kernel", StringComparison.OrdinalIgnoreCase);

    public static string LiveKernelCodeLabel(GraphicsWerKind kind) => kind switch
    {
        GraphicsWerKind.LiveKernelEvent117 => "117",
        GraphicsWerKind.LiveKernelEvent141 => "141",
        GraphicsWerKind.LiveKernelEvent142 => "142",
        _ => "não classificado"
    };

    public static string ExtraScopeLimitation =>
        "Temperatura, potência, frequência, utilização global e VRAM real da GPU não são coletadas: o Windows " +
        "não as expõe de forma confiável pelas interfaces genéricas usadas aqui. O valor AdapterRAM é o " +
        "reportado pelo driver, sujeito a limite de 32 bits, e não substitui a VRAM física.";

    public const string CausalityLimitation =
        "Evento gráfico, LiveKernelEvent ou alteração de driver não identifica causa, defeito físico ou correlação " +
        "com uma tela preta. A causalidade permanece não avaliada nesta fase.";

    public const string NoChangeLimitation =
        "Nenhuma correção, reparo, instalação, atualização ou reversão foi executada; nenhuma elevação foi solicitada.";
}

/// <summary>
/// Apresentação de mensagem de evento. Nenhum conteúdo ausente é reconstruído: quando a mensagem foi truncada,
/// o texto apresentado declara explicitamente que é parcial.
/// </summary>
public static class GraphicsMessagePresentation
{
    public const string TruncatedMarker = "(parcial)";
    public const string TruncatedNotice =
        "Mensagem truncada; o conteúdo apresentado é parcial e não substitui a mensagem original.";

    public static string Text(string? message, bool truncated)
    {
        if (string.IsNullOrEmpty(message))
            return truncated ? $"{TruncatedMarker} mensagem indisponível (truncada na origem)" : "—";
        return truncated ? $"{TruncatedMarker} {message}" : message;
    }
}

public sealed record GraphicsLimits
{
    public int MaxAdapters { get; init; } = 32;
    public int MaxMonitors { get; init; } = 64;
    public int MaxDrivers { get; init; } = 128;
    public int MaxEvents { get; init; } = 500;
    public int MaxWerReports { get; init; } = 300;
    public int MaxMessageChars { get; init; } = 1200;
    public static GraphicsLimits Default { get; } = new();
}

/// <summary>Janela de investigação. O período é resolvido em UTC e apresentado no fuso local.</summary>
public sealed record GraphicsInvestigationRequest(EventWindow Window, DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

public sealed record GraphicsAdapter(
    string StableId,
    string? Name,
    string? Manufacturer,
    string? Model,
    string? HardwareId,
    string? DriverProvider,
    string? DriverVersion,
    DateTimeOffset? DriverDateUtc,
    string? DriverClass,
    string? DriverInfName,
    string? WindowsState,
    DevicePresence Presence,
    ulong? ReportedAdapterRamBytes,
    string? Source,
    IReadOnlyList<HardwareProperty> Properties,
    IReadOnlyList<string> Limitations)
{
    public string? Property(string name) =>
        Properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal))?.Value;
}

public sealed record GraphicsMonitor(
    string StableId,
    string? Name,
    string? Manufacturer,
    string? Model,
    string? HardwareId,
    string? WindowsState,
    DevicePresence Presence,
    uint? Width,
    uint? Height,
    string? RefreshHz,
    bool? HdrSupported,
    bool? HdrEnabled,
    string? Connector,
    string? GdiDeviceName,
    IReadOnlyList<string> SupportedModes,
    bool SupportedModesTruncated,
    string? Source,
    IReadOnlyList<HardwareProperty> Properties,
    IReadOnlyList<string> Limitations);

public sealed record GraphicsDriver(
    string StableId,
    string? Name,
    string? Provider,
    string? Version,
    DateTimeOffset? DateUtc,
    string? DeviceClass,
    string? InfName,
    string? HardwareId,
    DevicePresence Presence,
    string? WindowsState,
    string? Source,
    IReadOnlyList<string> Limitations);

public sealed record GraphicsEventEvidence(
    string Id,
    string GroupId,
    string Channel,
    string Provider,
    int EventId,
    long? RecordId,
    DateTimeOffset OccurredUtc,
    EventLevel Level,
    int? TaskCode,
    Guid? ActivityId,
    string? Message,
    bool MessageTruncated,
    GraphicsEventKind Kind,
    IReadOnlyList<EventProperty> Data,
    IReadOnlyList<string> Limitations);

public sealed record GraphicsWerEvidence(
    string Id,
    string GroupId,
    string ReportId,
    bool ReportIdFromFile,
    string Source,
    string Store,
    string FolderName,
    string? EventType,
    string? ProblemCode,
    DateTimeOffset? EventTimeUtc,
    DateTimeOffset? CreationTimeUtc,
    string? ReportStatus,
    string? Application,
    string? Module,
    string? ModuleVersion,
    string? BucketId,
    GraphicsWerKind Kind,
    EvidenceConfidence Confidence,
    IReadOnlyList<EventProperty> Data,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Observação determinística. Severidade, confiança e causalidade são eixos separados; a causalidade é sempre
/// <see cref="CausalityStatus.NotAssessed"/> nesta fase.
/// </summary>
public sealed record GraphicsObservation(
    string Id,
    string RuleId,
    string RuleVersion,
    GraphicsObservationKind Kind,
    string Title,
    string Statement,
    EvidenceSeverity Severity,
    EvidenceConfidence Confidence,
    CausalityStatus Causality,
    string Metric,
    string Unit,
    double? ObservedValue,
    DateTimeOffset WindowFromUtc,
    DateTimeOffset WindowToUtc,
    DateTimeOffset? FirstObservedUtc,
    DateTimeOffset? LastObservedUtc,
    int EvidenceCount,
    string Source,
    IReadOnlyList<string> Limitations);

public sealed record GraphicsAnalysisNote(
    string RuleId,
    string RuleVersion,
    bool Evaluated,
    string Detail,
    GraphicsNoteReason Reason = GraphicsNoteReason.NotSpecified);

public sealed record GraphicsChange(
    GraphicsChangeType Type,
    string Subject,
    string? SubjectName,
    string? Field,
    string? Before,
    string? After);

public sealed record GraphicsSourceSummary(
    string CollectorId,
    string CollectorVersion,
    GraphicsSourceCategory Category,
    string Source,
    GraphicsSourceStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    int RecordsRead,
    int EvidenceCount,
    bool LimitHit,
    IReadOnlyList<string> Limitations);

public sealed record GraphicsCoverage(
    GraphicsExecutionStatus Execution,
    GraphicsCoverageStatus Status,
    int ExpectedSources,
    int CompletedSources,
    bool AdaptersUnavailable,
    bool MonitorsUnavailable,
    bool DriversUnavailable,
    bool EventsUnavailable,
    IReadOnlyList<string> UnavailableSources,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Investigação gráfica composta por uma leitura efêmera dos coletores existentes de GPU, monitores e drivers
/// e por uma leitura efêmera dos coletores existentes de eventos e WER. Apenas este registro é persistido; os
/// inventários e investigações de evento originais não são duplicados no histórico.
/// </summary>
public sealed record GraphicsInvestigation(
    int FormatVersion,
    Guid Id,
    string SoftwareVersion,
    string SchemaVersion,
    EventWindow Window,
    DateTimeOffset PeriodFromUtc,
    DateTimeOffset PeriodToUtc,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    GraphicsInvestigationStatus Status,
    Guid HardwareReadingId,
    Guid? EventReadingId,
    Guid? PreviousInvestigationId,
    IReadOnlyList<GraphicsSourceSummary> Sources,
    IReadOnlyList<GraphicsAdapter> Adapters,
    IReadOnlyList<GraphicsMonitor> Monitors,
    IReadOnlyList<GraphicsDriver> Drivers,
    IReadOnlyList<GraphicsEventEvidence> Events,
    IReadOnlyList<GraphicsWerEvidence> WerReports,
    IReadOnlyList<GraphicsObservation> Observations,
    IReadOnlyList<GraphicsAnalysisNote> AnalysisNotes,
    IReadOnlyList<GraphicsChange> Changes,
    bool ChangesComparable,
    IReadOnlyList<string> ComparisonLimitations,
    GraphicsCoverage Coverage,
    IReadOnlyList<string> Limitations);

public sealed record GraphicsProgress(string SourceId, GraphicsSourceStatus Status, int Finished, int Total,
    string Phase);

public sealed record GraphicsStoreSnapshot(
    IReadOnlyList<GraphicsInvestigation> Investigations, IReadOnlyList<string> Warnings);

public interface IGraphicsInvestigationStore
{
    Task<GraphicsStoreSnapshot> LoadGraphicsInvestigationsAsync(CancellationToken cancellationToken = default);
    Task<long> SaveGraphicsInvestigationAsync(GraphicsInvestigation investigation,
        CancellationToken cancellationToken = default);
    string GetGraphicsReportPath(Guid investigationId);
}
