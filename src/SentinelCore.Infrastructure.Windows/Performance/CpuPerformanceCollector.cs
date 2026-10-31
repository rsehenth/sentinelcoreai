using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Performance;

internal sealed class CpuPerformanceCollector(int? forcedProcessors = null, ushort? forcedGroupCount = null) : IPerformanceCollector
{
    private const string CollectorId = "performance-cpu";

    public string Id => CollectorId;
    public string Version => "1.0.0";
    public string Source => "GetSystemTimes + WMI PerfOS_System (fila)";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;
    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) =>
        new Session(forcedProcessors ?? NativeSystem.LogicalProcessors(),
            forcedGroupCount ?? NativeSystem.ProcessorGroupCount());

    private sealed class Session(int processors, ushort groupCount) : IPerformanceCollectorSession
    {
        private CpuCounterSample? previous;

        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
            CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (groupCount > 1 || processors > 64)
            {
                var multiGroupLimit = "Topologia com múltiplos grupos de processadores (> 64 CPUs) detectada; " +
                    "GetSystemTimes não agrega todos os grupos e CPU total é mantida indisponível para evitar medição distorcida.";
                var dummyReading = new CpuReading(0, 0, 0, null, processors, false, [multiGroupLimit]);
                return new PerformanceCollectorTick(CollectorId, timestampUtc, false, dummyReading, null, [], [], [],
                    [multiGroupLimit]);
            }

            if (!NativeSystem.TryGetSystemTimes(out var current))
            {
                previous = null;
                return PerformanceCollectorTick.Empty(CollectorId, timestampUtc,
                    "GetSystemTimes indisponível; CPU não coletada nesta amostra.");
            }

            var limitations = new List<string>();
            double? queue = null;
            try
            {
                var wmi = new WmiRead(cancellationToken);
                var row = wmi.Query("SELECT ProcessorQueueLength FROM Win32_PerfFormattedData_PerfOS_System",
                    allowEmpty: true).FirstOrDefault();
                queue = wmi.UInt32Opt(row, "ProcessorQueueLength");
                limitations.AddRange(wmi.Limitations);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                limitations.Add($"Fila do processador indisponível ({error.GetType().Name}); CPU permanece válida.");
            }
            var reading = PerformanceNormalizers.NormalizeCpu(previous, current,
                elapsed.TotalSeconds, processors, queue);
            previous = current;
            return new PerformanceCollectorTick(CollectorId, timestampUtc, false, reading, null, [], [], [],
                reading.Limitations.Concat(limitations).Distinct(StringComparer.Ordinal).ToArray());
        }, cancellationToken);

        public void Dispose() { }
    }
}
