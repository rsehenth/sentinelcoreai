using System.Net.NetworkInformation;
using System.Net.Sockets;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Network;

/// <summary>
/// Inventário mínimo de adaptadores para interpretar cobertura: permite distinguir
/// "sem endpoints IPv6 porque não há IPv6 no sistema" de "coleta falhou".
/// Somente estado e presença de família são retidos; endereços, máscaras e MAC nunca são lidos
/// para o registro.
/// </summary>
internal sealed class NetworkAdapterInventory : INetworkFactsCollector
{
    public string Id => "network-adapters";
    public string Version => "1.0.0";
    public string Source => "System.Net.NetworkInformation.NetworkInterface";
    public NetworkSourceCategory Category => NetworkSourceCategory.Adapters;

    public Task<NetworkFactsReading> ReadAsync(NetworkCollectionContext context,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException)
        {
            return new NetworkFactsReading(Id, true, [], [],
                [$"Adaptadores indisponíveis ({error.GetType().Name})."]);
        }

        var limitations = new List<string>();
        var summaries = new List<NetworkAdapterSummary>(interfaces.Length);
        foreach (var networkInterface in interfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var properties = networkInterface.GetIPProperties();
                var addresses = properties.UnicastAddresses;
                var ipv4 = addresses.Count(address => address.Address.AddressFamily == AddressFamily.InterNetwork);
                var ipv6 = addresses.Count(address => address.Address.AddressFamily == AddressFamily.InterNetworkV6);
                summaries.Add(new NetworkAdapterSummary(networkInterface.Id,
                    networkInterface.NetworkInterfaceType.ToString(),
                    networkInterface.OperationalStatus.ToString(),
                    ipv4 > 0, ipv6 > 0, ipv4, ipv6));
            }
            catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException
                or UnauthorizedAccessException or NotSupportedException)
            {
                limitations.Add($"Adaptador {networkInterface.Id}: propriedades indisponíveis ({error.GetType().Name}).");
            }
        }

        var operational = summaries.Count(summary => summary.OperationalStatus == "Up");
        if (summaries.Count == 0)
            limitations.Add("Nenhum adaptador enumerado; a ausência de endpoints não pode ser atribuída à rede.");
        else
            limitations.Add($"{summaries.Count} adaptador(es) enumerado(s), {operational} operacional(is). "
                + "Endereços e MAC não são lidos para o registro.");

        return new NetworkFactsReading(Id, false, [], summaries,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }, cancellationToken);
}
