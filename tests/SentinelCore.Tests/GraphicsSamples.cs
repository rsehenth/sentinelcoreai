using System.Globalization;
using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.Tests;

/// <summary>
/// Fixtures sintéticas para a Fase 3.3B. Nenhum dado real de GPU é usado e nenhuma falha de hardware é provocada.
/// </summary>
public static class GraphicsSamples
{
    public static readonly DateTimeOffset StartUtc = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public static HardwareItem Gpu(string stableId = "gpu-1", string name = "GPU sintética",
        string driverVersion = "31.0.15.3623", ulong? ram = 4294967295,
        string state = "OK", string errorCode = "0", string manufacturer = "Fabricante",
        IReadOnlyList<string>? limitations = null) =>
        new(HardwareCategory.Gpu, "gpu", stableId, name, manufacturer, name, "PCI\\VEN_10DE&DEV_2482",
            "Fabricante", driverVersion, StartUtc.AddDays(-30), "Display", "nvlddmkm.inf", null, state,
            DevicePresence.Present,
            [
                new("adapter_ram_wmi_bytes", ram?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                new("video_processor", name),
                new("config_manager_error_code", errorCode)
            ], limitations ?? []);

    public static HardwareItem Monitor(string stableId = "monitor-1", string name = "Monitor sintético",
        uint width = 1920, uint height = 1080, string refresh = "60", bool hdr = false,
        IReadOnlyList<string>? limitations = null) =>
        new(HardwareCategory.Monitor, "monitor", stableId, name, "Fabricante", name, "DEL1234", null, null,
            null, null, null, null, null, DevicePresence.Present,
            [
                new("active_width", width.ToString(CultureInfo.InvariantCulture)),
                new("active_height", height.ToString(CultureInfo.InvariantCulture)),
                new("active_refresh_hz", refresh),
                new("hdr_supported", hdr ? "Sim" : "Não"),
                new("hdr_enabled", hdr ? "Sim" : "Não"),
                new("connector", "DisplayPort (externo)"),
                new("gdi_device", @"\\.\DISPLAY1"),
                new("supported_resolutions", $"{width}x{height} @ {refresh} Hz"),
                new("supported_resolution_count", "1")
            ], limitations ?? []);

    public static HardwareItem Driver(string stableId = "gpu-1", string name = "GPU sintética",
        string version = "31.0.15.3623", string deviceClass = "Display",
        string hardwareId = "PCI\\VEN_10DE&DEV_2482") =>
        new(HardwareCategory.Driver, "driver", stableId, name, "Fabricante", null,
            hardwareId, "Fabricante", version, StartUtc.AddDays(-30), deviceClass,
            "nvlddmkm.inf", null, "OK", DevicePresence.Present, [new("pnp_class", deviceClass)], []);

    public static RawEventRecord DisplayTdr(long recordId = 100, DateTimeOffset? at = null,
        string? message = null, bool messageTruncated = false) =>
        new("System", "Display", GraphicsEvidenceClassification.DisplayDriverResetEventId, recordId,
            at ?? StartUtc.AddHours(-2), EventLevel.Warning, 3, null, null,
            message ?? "O driver de vídeo parou de responder e foi recuperado.", messageTruncated,
            [new("param1", "0")]);

    public static RawEventRecord ApplicationError(long recordId = 101, DateTimeOffset? at = null) =>
        new("Application", "Application Error", 1000, recordId, at ?? StartUtc.AddHours(-3),
            EventLevel.Error, 2, null, null, "Falha do aplicativo.", false, []);

    public static RawWerReport LiveKernel(string reportId, string code, DateTimeOffset? at = null,
        string eventType = "LiveKernelEvent", string application = "explorer.exe") =>
        new(reportId, true, "Sistema", "ReportArchive", $"Kernel_{code}", eventType, code,
            at ?? StartUtc.AddHours(-1), at ?? StartUtc.AddHours(-1), "Loaded", application, null,
            "dxgkrnl.sys", "10.0.1", "bucket", [], [], EvidenceConfidence.High);

    public static RawWerReport AppCrash(string reportId, DateTimeOffset? at = null) =>
        new(reportId, true, "Sistema", "ReportArchive", $"AppCrash_{reportId}", "AppCrash", null,
            at ?? StartUtc.AddHours(-4), at ?? StartUtc.AddHours(-4), "Loaded", "app.exe", null,
            "app.dll", "1.0", "bucket", [], [], EvidenceConfidence.High);

    public static FakeHardwareCollector Hardware(string id, HardwareCategory category,
        Func<Guid, IReadOnlyList<HardwareItem>> items, string? version = null,
        string? limitation = null) =>
        new(id, category, version ?? "1.0.0", snapshotId => new HardwareCollectorResult(id, version ?? "1.0.0",
            category, "FakeGraphicsCollector", StartUtc, StartUtc.AddMilliseconds(5), TimeSpan.FromMilliseconds(5),
            limitation is null ? HardwareCollectionStatus.Completed : HardwareCollectionStatus.Partial,
            items(snapshotId), limitation is null ? [] : [limitation]));

    public static FakeEventCollector Events(string id, string source,
        IReadOnlyList<RawEventRecord> events, IReadOnlyList<RawWerReport>? reports = null,
        string? limitation = null) =>
        new(id, source, events, reports ?? [], limitation);

    public static IReadOnlyList<IHardwareCollector> RealisticHardware(
        FakeHardwareCollector? gpu = null, FakeHardwareCollector? monitor = null,
        FakeHardwareCollector? driver = null) =>
    [
        gpu ?? Hardware("gpu", HardwareCategory.Gpu, _ => [Gpu()]),
        monitor ?? Hardware("monitor", HardwareCategory.Monitor, _ => [Monitor()]),
        driver ?? Hardware("driver", HardwareCategory.Driver, _ => [Driver()])
    ];

    public static IReadOnlyList<IEventEvidenceCollector> RealisticEvents(
        IReadOnlyList<RawEventRecord>? events = null, IReadOnlyList<RawWerReport>? reports = null) =>
    [
        Events("event-log", "Windows Event Log (leitura nativa)", events ?? []),
        Events("wer-metadata", "Windows Error Reporting (metadados)", [], reports ?? [])
    ];

    public static GraphicsInvestigationRunner Runner(TimeProvider? clock = null,
        IReadOnlyList<IHardwareCollector>? hardware = null,
        IReadOnlyList<IEventEvidenceCollector>? events = null,
        GraphicsLimits? limits = null) =>
        new(hardware ?? RealisticHardware(), events ?? RealisticEvents(),
            clock ?? new FixedTime { Now = StartUtc }, limits);

    public sealed class FakeHardwareCollector(
        string id,
        HardwareCategory category,
        string version,
        Func<Guid, HardwareCollectorResult> read) : IHardwareCollector
    {
        public string Id { get; } = id;
        public string Version { get; } = version;
        public HardwareCategory Category { get; } = category;
        public string Source => "FakeGraphicsCollector";

        public Task<HardwareCollectorResult> CollectAsync(Guid snapshotId, IHardwareCollectionSession session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(read(snapshotId));
        }
    }

    public sealed class FailingEventCollector(string id, string source) : IEventEvidenceCollector
    {
        public string Id { get; } = id;
        public string Version => "1.0.0";
        public string Source { get; } = source;

        public Task<EventCollectorResult> CollectAsync(Guid investigationId, EventQueryWindow window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EventCollectorResult(Id, Version, Source, StartUtc,
                StartUtc.AddMilliseconds(1), TimeSpan.FromMilliseconds(1), EventSourceStatus.Failed, 0, 0, false,
                [], [], [], ["Fonte de eventos indisponível neste teste."]));
        }
    }

    public sealed class FakeEventCollector(
        string id,
        string source,
        IReadOnlyList<RawEventRecord> events,
        IReadOnlyList<RawWerReport> reports,
        string? limitation = null) : IEventEvidenceCollector
    {
        public string Id { get; } = id;
        public string Version => "1.0.0";
        public string Source { get; } = source;

        public Task<EventCollectorResult> CollectAsync(Guid investigationId, EventQueryWindow window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = limitation is null ? EventSourceStatus.Completed : EventSourceStatus.Partial;
            return Task.FromResult(new EventCollectorResult(Id, Version, Source, StartUtc,
                StartUtc.AddMilliseconds(5), TimeSpan.FromMilliseconds(5), status, events.Count, reports.Count,
                false, events, reports,
                [new("System", true, true, true, null)],
                limitation is null ? [] : [limitation]));
        }
    }
}
