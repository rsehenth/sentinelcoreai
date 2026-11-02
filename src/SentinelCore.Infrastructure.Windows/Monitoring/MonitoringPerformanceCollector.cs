using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Performance;

/// <summary>
/// Adaptador de baixo impacto para observação contínua. Reutiliza as mesmas APIs nativas e o mesmo normalizador
/// da Fase 3.2, mas executa somente <c>GetSystemTimes</c> e <c>GlobalMemoryStatusEx</c>: nenhuma consulta WMI,
/// enumeração de processos ou leitura de disco é feita a cada ciclo. O coletor de auditoria permanece inalterado.
/// </summary>
internal sealed class MonitoringPerformanceCollector : IPerformanceCollector
{
    private const string CollectorId = "monitoring-performance";

    public string Id => CollectorId;
    public string Version => "1.0.0";
    public string Source => "GetSystemTimes + GlobalMemoryStatusEx (sem WMI)";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;
    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session();

    private sealed class Session : IPerformanceCollectorSession
    {
        private readonly int processors = NativeSystem.LogicalProcessors();
        private readonly ushort groupCount = NativeSystem.ProcessorGroupCount();
        private CpuCounterSample? previous;
        private DateTimeOffset? previousAt;

        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
            CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limitations = new List<string>();
            CpuReading? cpu = null;
            MemoryReading? memory = null;

            if (groupCount > 1 || processors > 64)
                limitations.Add("Topologia com múltiplos grupos de processadores (> 64 CPUs): CPU total mantida " +
                    "indisponível nesta sessão para não distorcer a medição.");
            else if (!NativeSystem.TryGetSystemTimes(out var current))
                limitations.Add("GetSystemTimes indisponível; CPU não observada neste ciclo.");
            else
            {
                var interval = previousAt is { } last ? timestampUtc - last : TimeSpan.Zero;
                cpu = PerformanceNormalizers.NormalizeCpu(previous, current, interval.TotalSeconds, processors,
                    null);
                previous = current;
                previousAt = timestampUtc;
                limitations.AddRange(cpu.Limitations);
            }

            if (!NativeSystem.TryGetMemory(out var installed, out var available))
                limitations.Add("GlobalMemoryStatusEx indisponível; memória não observada neste ciclo.");
            else
            {
                memory = PerformanceNormalizers.NormalizeMemory(installed, available, null, null, null, null);
                limitations.AddRange(memory.Limitations);
            }

            if (cpu is null && memory is null)
                return PerformanceCollectorTick.Empty(CollectorId, timestampUtc,
                    "Fontes nativas de CPU e memória indisponíveis neste ciclo.");

            return new PerformanceCollectorTick(CollectorId, timestampUtc, false, cpu, memory, [], [], [],
                limitations.Distinct(StringComparer.Ordinal).ToArray());
        }, cancellationToken);

        public void Dispose()
        {
            previous = null;
            previousAt = null;
        }
    }
}
