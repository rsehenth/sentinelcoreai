using System.Net;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Network;

/// <summary>
/// Coleta as tabelas TCP e UDP, IPv4 e IPv6, e associa cada linha ao processo responsável.
/// Nenhuma conexão é aberta, nenhum pacote é capturado e nenhum endereço é resolvido por DNS.
/// </summary>
internal sealed class NetworkEndpointCollector : INetworkSampleCollector
{
    private readonly NetworkLimits limits;
    private readonly NetworkProcessResolver resolver = new();

    public NetworkEndpointCollector(NetworkLimits? limits = null) => this.limits = limits ?? NetworkLimits.Default;

    public string Id => "network-endpoints";
    public string Version => "1.0.0";
    public string Source => "GetExtendedTcpTable/GetExtendedUdpTable (IP Helper, owner PID)";
    public NetworkSourceCategory Category => NetworkSourceCategory.Endpoints;

    public Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context,
        DateTimeOffset timestampUtc, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        resolver.BeginTick();
        var limitations = new List<string>();
        var endpoints = new List<NetworkEndpoint>();
        var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var failedTables = 0;

        void Read(bool tcp, int family, NetworkProtocol protocol, NetworkAddressFamily addressFamily)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeNetwork.TryReadTable(tcp, family, limits, out var table, out var failure, out var attempts))
            {
                failedTables++;
                limitations.Add(failure ?? "Tabela nativa indisponível.");
                return;
            }
            if (attempts > 1)
                limitations.Add($"Tabela {NativeNetwork.RowSizeDescription(tcp, family)} exigiu {attempts} tentativas "
                    + "porque o tamanho mudou entre chamadas; amostra não atômica.");
            try
            {
                if (tcp)
                {
                    foreach (var row in family == NativeNetwork.AfInet6
                                 ? NativeNetwork.ReadTcp6(table!.Buffer, table.Length)
                                 : NativeNetwork.ReadTcp4(table!.Buffer, table.Length))
                        Add(protocol, addressFamily, row.Local, row.LocalPort, row.Remote, row.RemotePort,
                            NativeNetwork.MapTcpState(row.State), (int)row.Pid, timestampUtc);
                }
                else
                {
                    foreach (var row in family == NativeNetwork.AfInet6
                                 ? NativeNetwork.ReadUdp6(table!.Buffer, table.Length)
                                 : NativeNetwork.ReadUdp4(table!.Buffer, table.Length))
                        Add(protocol, addressFamily, row.Local, row.LocalPort, null, null, null,
                            (int)row.Pid, timestampUtc);
                }
            }
            catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException)
            {
                failedTables++;
                limitations.Add($"Tabela {NativeNetwork.RowSizeDescription(tcp, family)} rejeitada: "
                    + $"dado nativo inconsistente ({error.GetType().Name}).");
            }
        }

        void Add(NetworkProtocol protocol, NetworkAddressFamily family, IPAddress local, ushort localPort,
            IPAddress? remote, ushort? remotePort, TcpConnectionState? state, int pid, DateTimeOffset at)
        {
            var process = resolver.Resolve(pid);
            var endpoint = new NetworkEndpoint(protocol, family,
                local.ToString(), localPort,
                remote?.ToString(), remotePort, state,
                pid, process.Identity, process.Name, process.Status, at, Source);
            if (!duplicateKeys.Add(endpoint.Key)) { duplicates++; return; }
            endpoints.Add(endpoint);
        }

        Read(true, NativeNetwork.AfInet, NetworkProtocol.Tcp, NetworkAddressFamily.IPv4);
        Read(true, NativeNetwork.AfInet6, NetworkProtocol.Tcp, NetworkAddressFamily.IPv6);
        Read(false, NativeNetwork.AfInet, NetworkProtocol.Udp, NetworkAddressFamily.IPv4);
        Read(false, NativeNetwork.AfInet6, NetworkProtocol.Udp, NetworkAddressFamily.IPv6);

        limitations.AddRange(resolver.Notes);
        if (failedTables > 0)
            limitations.Add($"{failedTables} de 4 tabelas não foram lidas; a amostra está incompleta.");
        if (duplicates > 0)
            limitations.Add($"{duplicates} linha(s) duplicada(s) na tabela nativa foram contabilizadas uma única vez.");
        resolver.EndTick();

        var processes = Summarize(endpoints);
        var unresolved = endpoints.Count(endpoint => endpoint.ProcessStatus != NetworkProcessStatus.Resolved
            && endpoint.ProcessStatus != NetworkProcessStatus.SystemProcess);
        if (unresolved > 0)
        {
            var denied = endpoints.Count(endpoint => endpoint.ProcessStatus == NetworkProcessStatus.AccessDenied);
            var exited = endpoints.Count(endpoint => endpoint.ProcessStatus == NetworkProcessStatus.Exited);
            limitations.Add($"{unresolved} endpoint(s) sem processo identificável "
                + $"({denied} com acesso negado, {exited} com processo encerrado); "
                + "PID isolado não é tratado como identidade.");
        }

        return new NetworkSampleReading(Id, false, endpoints, processes, null, null,
            duplicates, limitations.Distinct(StringComparer.Ordinal).ToArray());
    }, cancellationToken);

    internal static IReadOnlyList<NetworkProcessCount> Summarize(IReadOnlyList<NetworkEndpoint> endpoints) =>
        endpoints
            .GroupBy(endpoint => endpoint.ProcessIdentity ?? $"pid:{endpoint.Pid}", StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new NetworkProcessCount(
                    group.Key.StartsWith("pid:", StringComparison.Ordinal) ? null : group.Key,
                    first.Pid, first.ProcessName,
                    group.Count(endpoint => endpoint.Protocol == NetworkProtocol.Tcp),
                    group.Count(endpoint => endpoint.Protocol == NetworkProtocol.Udp),
                    group.Where(endpoint => endpoint.TcpState == TcpConnectionState.Listen)
                        .Select(endpoint => endpoint.LocalPort).Distinct().Count());
            })
            .OrderByDescending(summary => summary.TcpConnections + summary.UdpEndpoints)
            .ThenBy(summary => summary.Pid)
            .ToArray();
}
