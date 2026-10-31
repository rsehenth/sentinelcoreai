using Microsoft.Win32;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Network;

/// <summary>Configuração bruta de uma família, como observada — ou como ausente.</summary>
internal sealed record PortRangeConfiguration(
    int? StartPort, int? PortCount,
    IReadOnlyList<NetworkPortRangeExclusion> Reserved,
    IReadOnlyList<string> Limitations);

/// <summary>Fonte substituível nos testes; a implementação real é somente leitura.</summary>
internal interface IDynamicPortRangeSource
{
    PortRangeConfiguration Read(NetworkAddressFamily family);
}

/// <summary>
/// Leitura exclusivamente de consulta do Registro. Nenhum valor é criado, alterado ou removido
/// e nenhuma elevação é solicitada; chave inacessível vira limitação.
/// </summary>
internal sealed class RegistryDynamicPortRangeSource : IDynamicPortRangeSource
{
    private const string TcpV4Key = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    private const string TcpV6Key = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";

    public PortRangeConfiguration Read(NetworkAddressFamily family)
    {
        var path = family == NetworkAddressFamily.IPv6 ? TcpV6Key : TcpV4Key;
        var limitations = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (key is null)
                return new(null, null, [], [$"Chave HKLM\\{path} inexistente; intervalo dinâmico não observado."]);

            var start = key.GetValue("DynamicPortRangeStartPort") as int?;
            var count = key.GetValue("DynamicPortRangeNumberOfPorts") as int?;
            if (key.GetValue("DynamicPortRangeStartPort") is not null && start is null)
                limitations.Add("DynamicPortRangeStartPort não é DWORD; valor ignorado.");
            if (key.GetValue("DynamicPortRangeNumberOfPorts") is not null && count is null)
                limitations.Add("DynamicPortRangeNumberOfPorts não é DWORD; valor ignorado.");

            var reserved = new List<NetworkPortRangeExclusion>();
            if (key.GetValue("ReservedPorts") is string[] entries)
            {
                foreach (var entry in entries)
                    if (TryParseReserved(entry, out var exclusion))
                        reserved.Add(exclusion with { Source = "ReservedPorts (Registro)" });
                    else
                        limitations.Add($"Entrada ReservedPorts inválida ('{entry}') ignorada.");
            }
            return new(start, count, reserved, limitations);
        }
        catch (Exception error) when (error is System.Security.SecurityException
            or UnauthorizedAccessException or System.IO.IOException or ArgumentException)
        {
            return new(null, null, [],
                [$"Intervalo dinâmico de {family} indisponível ({error.GetType().Name}); nenhuma elevação foi solicitada."]);
        }
    }

    internal static bool TryParseReserved(string? entry, out NetworkPortRangeExclusion exclusion)
    {
        exclusion = default!;
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var parts = entry.Trim().Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], out var from) || from is < 1 or > 65_535) return false;
        var to = from;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out to) || to is < 1 or > 65_535 || to < from))
            return false;
        exclusion = new NetworkPortRangeExclusion(from, to, "ReservedPorts (Registro)");
        return true;
    }
}

/// <summary>
/// Lê os intervalos dinâmicos por protocolo e família. O Windows compartilha um único intervalo
/// dinâmico entre TCP e UDP; isso é reportado como fato, nunca como medição independente.
/// </summary>
internal sealed class DynamicPortRangeReader : INetworkFactsCollector
{
    internal const int WindowsDefaultStart = 49_152;
    internal const int WindowsDefaultCount = 16_384;

    private readonly IDynamicPortRangeSource source;

    public DynamicPortRangeReader(IDynamicPortRangeSource? source = null) =>
        this.source = source ?? new RegistryDynamicPortRangeSource();

    public string Id => "network-port-range";
    public string Version => "1.0.0";
    public string Source => @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip{,6}\Parameters (somente leitura)";
    public NetworkSourceCategory Category => NetworkSourceCategory.PortRange;

    public Task<NetworkFactsReading> ReadAsync(NetworkCollectionContext context,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ranges = new List<NetworkDynamicPortRange>();
        var limitations = new List<string>();
        var unavailable = 0;

        foreach (var family in new[] { NetworkAddressFamily.IPv4, NetworkAddressFamily.IPv6 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = source.Read(family);
            limitations.AddRange(configuration.Limitations);

            var start = configuration.StartPort;
            var count = configuration.PortCount;
            var isDefault = start is null || count is null;
            if (isDefault)
            {
                start = WindowsDefaultStart;
                count = WindowsDefaultCount;
            }

            if (start is not (>= 1 and <= 65_535) || count is not (>= 1 and <= 65_535) ||
                start + count - 1 > 65_535)
            {
                unavailable++;
                limitations.Add($"Intervalo dinâmico de {family} inválido " +
                    $"(início {start}, quantidade {count}); valores rejeitados e não substituídos por padrão.");
                continue;
            }

            var rangeLimitations = new List<string>();
            if (isDefault)
                rangeLimitations.Add("Nenhum valor explícito no Registro: o intervalo reportado é o padrão " +
                    "documentado do Windows, não um valor medido nesta máquina.");
            if (configuration.Reserved.Count > 0)
                rangeLimitations.Add("Intervalos reservados no Registro foram considerados; exclusões gerenciadas " +
                    "por Hyper-V/WSL não são enumeráveis por API documentada de leitura e podem reduzir a disponibilidade real.");

            ranges.Add(new NetworkDynamicPortRange(NetworkProtocol.Tcp, family, start!.Value, count.Value,
                isDefault ? "WindowsDefault" : @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip{,6}\Parameters",
                isDefault, configuration.Reserved, rangeLimitations.ToArray()));
            ranges.Add(new NetworkDynamicPortRange(NetworkProtocol.Udp, family, start.Value, count.Value,
                "Compartilhado com TCP (Windows)", isDefault, configuration.Reserved,
                rangeLimitations.Concat([
                    "O Windows usa o mesmo intervalo dinâmico para TCP e UDP; este valor não foi medido para UDP."
                ]).ToArray()));
        }

        if (ranges.Count == 0) unavailable = 2;
        var unsupported = unavailable >= 2;
        if (unavailable > 0 && !unsupported)
            limitations.Add($"{unavailable} família(s) sem intervalo dinâmico observável.");

        return new NetworkFactsReading(Id, unsupported, ranges, [],
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }, cancellationToken);
}
