using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows.Performance;

namespace SentinelCore.Infrastructure.Windows.Monitoring;

/// <summary>
/// Composição das fontes reais do monitoramento. CPU/memória usam o adaptador nativo de baixo impacto; eventos
/// usam leitura incremental; rede reutiliza os coletores da Fase 3.3A. Nenhuma consulta pesada é executada a cada
/// ciclo e nenhum serviço ou processo auxiliar é criado.
/// </summary>
public static class WindowsMonitoringCollectors
{
    public static IMonitoringEventSource CreateEventSource(MonitoringLimits? limits = null) =>
        new WindowsMonitoringEventSource(limits?.MaxEventsPerTick ?? 40);

    public static IPerformanceCollector CreatePerformanceCollector() => new MonitoringPerformanceCollector();
}
