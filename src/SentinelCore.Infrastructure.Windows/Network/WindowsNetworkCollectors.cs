using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Network;

/// <summary>
/// Composição dos coletores reais de rede. Tudo é leitura local: nenhuma porta é aberta,
/// nenhum pacote é capturado, nenhum DNS reverso é feito e nenhuma configuração é alterada.
/// </summary>
public static class WindowsNetworkCollectors
{
    public static IReadOnlyList<INetworkSampleCollector> CreateSampleCollectors(NetworkLimits? limits = null) =>
    [
        new NetworkEndpointCollector(limits),
        new NetworkStatisticsCollector()
    ];

    public static IReadOnlyList<INetworkFactsCollector> CreateFactsCollectors() => CreateFactsCollectorsWith(null);

    internal static IReadOnlyList<INetworkFactsCollector> CreateFactsCollectorsWith(
        IDynamicPortRangeSource? portRangeSource) =>
    [
        new DynamicPortRangeReader(portRangeSource),
        new NetworkAdapterInventory()
    ];
}
