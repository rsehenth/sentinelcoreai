using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Network;

/// <summary>
/// Contrato nativo com a IP Helper API. Toda alocação nativa é liberada no mesmo método
/// (<see cref="Marshal.AllocHGlobal"/>/<see cref="Marshal.FreeHGlobal"/>) e a leitura das tabelas
/// é feita por <see cref="ReadOnlySpan{T}"/>, sem <c>unsafe</c> e sem alocação por linha.
/// </summary>
/// <remarks>
/// IP Helper devolve campos em ordens distintas, e confundi-las produz portas e endereços inválidos:
/// <list type="bullet">
/// <item><c>dwLocalAddr</c>/<c>dwRemoteAddr</c> (IPv4) estão em ordem de rede; os 4 bytes em memória
/// já são os octetos na ordem correta.</item>
/// <item><c>dwLocalPort</c>/<c>dwRemotePort</c> guardam a porta em ordem de rede nos 16 bits baixos;
/// os 2 bytes em memória são big-endian (equivalente a <c>ntohs((u_short)dwPort)</c>).</item>
/// <item>Estado, PID e scope id são inteiros hospedeiros (little-endian em x64).</item>
/// </list>
/// </remarks>
internal static class NativeNetwork
{
    internal const int AfInet = 2;
    internal const int AfInet6 = 23;

    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    private const int TcpRowSize = 24;
    private const int Tcp6RowSize = 56;
    private const int UdpRowSize = 12;
    private const int Udp6RowSize = 28;
    private const int TableHeaderSize = 4;

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetTcpStatisticsEx(out MibTcpStats statistics, uint addressFamily);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetUdpStatisticsEx(out MibUdpStats statistics, uint addressFamily);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit,
        out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags,
        [Out] char[] buffer, ref int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcpStats
    {
        public uint RtoAlgorithm;
        public uint RtoMin;
        public uint RtoMax;
        public uint MaxConn;
        public uint ActiveOpens;
        public uint PassiveOpens;
        public uint AttemptFails;
        public uint EstabResets;
        public uint CurrEstab;
        public uint InSegs;
        public uint OutSegs;
        public uint RetransSegs;
        public uint InErrs;
        public uint OutRsts;
        public uint NumConns;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdpStats
    {
        public uint InDatagrams;
        public uint NoPorts;
        public uint InErrors;
        public uint OutDatagrams;
        public uint NumAddrs;
    }

    internal sealed record RawTable(byte[] Buffer, int Length, int InitialBytes);

    /// <summary>
    /// Lê uma tabela TCP ou UDP com retentativas limitadas quando o tamanho exigido muda entre chamadas.
    /// Nunca trunca em silêncio: tabela maior que o teto configurado resulta em falha explícita.
    /// </summary>
    internal static bool TryReadTable(bool tcp, int addressFamily, NetworkLimits limits,
        out RawTable? table, out string? failure, out int attempts)
    {
        table = null;
        failure = null;
        attempts = 0;
        var rowSize = tcp
            ? addressFamily == AfInet6 ? Tcp6RowSize : TcpRowSize
            : addressFamily == AfInet6 ? Udp6RowSize : UdpRowSize;
        var size = Math.Max(TableHeaderSize + rowSize, limits.InitialTableBytes);

        for (var attempt = 0; attempt <= limits.MaxTableRetries; attempt++)
        {
            attempts = attempt + 1;
            if (size > limits.MaxTableBytes)
            {
                failure = $"Tabela {(tcp ? "TCP" : "UDP")} excede o teto de {limits.MaxTableBytes} bytes; "
                    + "coleta interrompida para não truncar dados.";
                return false;
            }

            var buffer = Marshal.AllocHGlobal(size);
            var probeSize = size;
            try
            {
                var result = tcp
                    ? GetExtendedTcpTable(buffer, ref probeSize, false, addressFamily, TcpTableOwnerPidAll, 0)
                    : GetExtendedUdpTable(buffer, ref probeSize, false, addressFamily, UdpTableOwnerPid, 0);

                if (result == 0)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(buffer, bytes, 0, size);
                    if (!TryValidate(bytes, rowSize, limits.MaxRowsPerTable, out var used, out var invalid))
                    {
                        failure = invalid;
                        return false;
                    }
                    table = new(bytes, used, size);
                    return true;
                }

                if (result != ErrorInsufficientBuffer)
                {
                    failure = $"GetExtended{(tcp ? "TcpTable" : "UdpTable")} falhou com código {result}"
                        + (result == 5 ? " (acesso negado; elevação não é solicitada)." : ".");
                    return false;
                }

                if (probeSize <= 0)
                {
                    failure = $"Tabela {(tcp ? "TCP" : "UDP")} reportou tamanho exigido inválido.";
                    return false;
                }

                if (probeSize > limits.MaxTableBytes)
                {
                    failure = $"Tabela {(tcp ? "TCP" : "UDP")} exige {probeSize} bytes, acima do teto de "
                        + $"{limits.MaxTableBytes} bytes; coleta interrompida para não truncar dados.";
                    return false;
                }

                // Folga para o crescimento entre a medição e a leitura seguinte.
                size = Math.Min(limits.MaxTableBytes, probeSize + 4096);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        failure = $"Tabela {(tcp ? "TCP" : "UDP")} mudou de tamanho em todas as "
            + $"{limits.MaxTableRetries + 1} tentativas; amostra descartada.";
        return false;
    }

    private static bool TryValidate(byte[] buffer, int rowSize, int maxRows, out int used, out string? failure)
    {
        used = 0;
        failure = null;
        if (buffer.Length < TableHeaderSize)
        {
            failure = "Tabela nativa menor que o cabeçalho; dado rejeitado.";
            return false;
        }
        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (count < 0 || count > maxRows)
        {
            failure = $"Tabela nativa reportou {count} linhas, fora do limite de {maxRows}; dado rejeitado.";
            return false;
        }
        var required = (long)TableHeaderSize + (long)count * rowSize;
        if (required > buffer.Length)
        {
            failure = $"Tabela nativa inconsistente: {count} linhas exigem {required} bytes, "
                + $"buffer tem {buffer.Length}; dado rejeitado.";
            return false;
        }
        used = (int)required;
        return true;
    }

    /// <summary>Converte <c>MIB_TCP_STATE</c> para o contrato. Valores fora de 1..12 não viram estado válido.</summary>
    internal static TcpConnectionState MapTcpState(uint state) => state switch
    {
        1 => TcpConnectionState.Closed,
        2 => TcpConnectionState.Listen,
        3 => TcpConnectionState.SynSent,
        4 => TcpConnectionState.SynReceived,
        5 => TcpConnectionState.Established,
        6 => TcpConnectionState.FinWait1,
        7 => TcpConnectionState.FinWait2,
        8 => TcpConnectionState.CloseWait,
        9 => TcpConnectionState.Closing,
        10 => TcpConnectionState.LastAck,
        11 => TcpConnectionState.TimeWait,
        12 => TcpConnectionState.DeleteTcb,
        _ => TcpConnectionState.Unknown
    };

    /// <summary>
    /// Lê o contador do cabeçalho e o confronta com os bytes válidos (<paramref name="length"/>), não com o
    /// tamanho do array de apoio. Contador negativo ou maior que o dado disponível é rejeitado com
    /// <see cref="ArgumentException"/>, que o coletor converte em limitação.
    /// </summary>
    private static int RowCount(byte[] buffer, int length, int rowSize)
    {
        var available = Math.Min(length, buffer.Length);
        if (available < TableHeaderSize)
            throw new ArgumentException("Tabela nativa menor que o cabeçalho.", nameof(buffer));
        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (count < 0 || (long)TableHeaderSize + (long)count * rowSize > available)
            throw new ArgumentException(
                $"Tabela nativa anuncia {count} linhas de {rowSize} bytes, mas só há {available} bytes válidos.",
                nameof(buffer));
        return count;
    }

    internal static IEnumerable<(uint State, IPAddress Local, ushort LocalPort, IPAddress Remote,
        ushort RemotePort, uint Pid)> ReadTcp4(byte[] buffer, int length)
    {
        var count = RowCount(buffer, length, TcpRowSize);
        for (var index = 0; index < count; index++)
        {
            var offset = TableHeaderSize + index * TcpRowSize;
            var row = new ReadOnlySpan<byte>(buffer, offset, TcpRowSize);
            yield return (
                BinaryPrimitives.ReadUInt32LittleEndian(row[..4]),
                new IPAddress(row.Slice(4, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(row.Slice(8, 2)),
                new IPAddress(row.Slice(12, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(row.Slice(16, 2)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4)));
        }
    }

    internal static IEnumerable<(uint State, IPAddress Local, ushort LocalPort, IPAddress Remote,
        ushort RemotePort, uint Pid)> ReadTcp6(byte[] buffer, int length)
    {
        var count = RowCount(buffer, length, Tcp6RowSize);
        for (var index = 0; index < count; index++)
        {
            var offset = TableHeaderSize + index * Tcp6RowSize;
            var row = new ReadOnlySpan<byte>(buffer, offset, Tcp6RowSize);
            // MIB_TCP6ROW_OWNER_PID: ucLocalAddr 0, dwLocalScopeId 16, dwLocalPort 20, ucRemoteAddr 24,
            // dwRemoteScopeId 40, dwRemotePort 44, dwState 48, dwOwningPid 52; 56 bytes.
            var localScope = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4));
            var remoteScope = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(40, 4));
            yield return (
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(48, 4)),
                new IPAddress(row.Slice(0, 16), localScope),
                BinaryPrimitives.ReadUInt16BigEndian(row.Slice(20, 2)),
                new IPAddress(row.Slice(24, 16), remoteScope),
                BinaryPrimitives.ReadUInt16BigEndian(row.Slice(44, 2)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(52, 4)));
        }
    }

    internal static IEnumerable<(IPAddress Local, ushort LocalPort, uint Pid)> ReadUdp4(byte[] buffer, int length)
    {
        var count = RowCount(buffer, length, UdpRowSize);
        for (var index = 0; index < count; index++)
        {
            var offset = TableHeaderSize + index * UdpRowSize;
            var row = new ReadOnlySpan<byte>(buffer, offset, UdpRowSize);
            yield return (
                new IPAddress(row.Slice(0, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(row.Slice(4, 2)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4)));
        }
    }

    internal static IEnumerable<(IPAddress Local, ushort LocalPort, uint Pid)> ReadUdp6(byte[] buffer, int length)
    {
        var count = RowCount(buffer, length, Udp6RowSize);
        for (var index = 0; index < count; index++)
        {
            var offset = TableHeaderSize + index * Udp6RowSize;
            var row = new ReadOnlySpan<byte>(buffer, offset, Udp6RowSize);
            var scope = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4));
            yield return (
                new IPAddress(row.Slice(0, 16), scope),
                BinaryPrimitives.ReadUInt16BigEndian(row.Slice(20, 2)),
                BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(24, 4)));
        }
    }

    internal static bool TryReadTcpStatistics(int addressFamily, out MibTcpStats statistics) =>
        GetTcpStatisticsEx(out statistics, (uint)addressFamily) == 0;

    internal static bool TryReadUdpStatistics(int addressFamily, out MibUdpStats statistics) =>
        GetUdpStatisticsEx(out statistics, (uint)addressFamily) == 0;

    /// <summary>
    /// Intervalo monotônico em milissegundos do sistema. Usado apenas para custo de coleta;
    /// durações de sessão usam <see cref="System.Diagnostics.Stopwatch"/>.
    /// </summary>
    internal static long TickMilliseconds() => Environment.TickCount64;

    internal static string RowSizeDescription(bool tcp, int family) =>
        $"{(tcp ? "TCP" : "UDP")} {(family == AfInet6 ? "IPv6" : "IPv4")} "
        + $"({(tcp ? family == AfInet6 ? Tcp6RowSize : TcpRowSize : family == AfInet6 ? Udp6RowSize : UdpRowSize)} B/linha, "
        + $"cabeçalho {TableHeaderSize} B)";
}
