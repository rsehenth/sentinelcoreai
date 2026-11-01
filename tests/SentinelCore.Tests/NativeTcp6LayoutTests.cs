using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows.Network;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Protege o contrato nativo de <c>MIB_TCP6ROW_OWNER_PID</c> com uma fixture construída a partir do layout
/// OFICIAL da Microsoft (tcpmib.h), e não a partir dos offsets do parser. Os números esperados abaixo são
/// literais da documentação; nenhum deles é lido de <see cref="NativeNetwork"/>.
/// https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcp6row_owner_pid
/// </summary>
public sealed class NativeTcp6LayoutTests
{
    private const int Header = 4;
    private const int OfficialRowSize = 56;

    /// <summary>Espelho gerenciado do struct nativo; o marshaller, não o parser, decide onde cada campo cai.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Mib6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    private sealed record Row(string Local, uint LocalScope, ushort LocalPort,
        string Remote, uint RemoteScope, ushort RemotePort, uint State, uint Pid);

    private static readonly Row First = new("fe80::1234:5678", 11, 49152, "2001:db8::abcd", 22, 443, 5, 31337);
    private static readonly Row Second = new("2001:db8:aa::10", 0, 8080, "2001:db8:bb::99", 0, 51234, 11, 4242);
    private static readonly Row Third = new("::1", 0, 135, "::", 0, 0, 2, 2036);

    /// <summary><c>dwPort</c> guarda a porta em ordem de rede nos 16 bits baixos: <c>ntohs((u_short)dwPort)</c>.</summary>
    private static uint NetworkOrderPort(ushort port) => (ushort)IPAddress.HostToNetworkOrder((short)port);

    private static byte[] BuildTable(params Row[] rows)
    {
        var buffer = new byte[Header + rows.Length * OfficialRowSize];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            var native = new Mib6Row
            {
                ucLocalAddr = IPAddress.Parse(r.Local).GetAddressBytes(),
                dwLocalScopeId = r.LocalScope,
                dwLocalPort = NetworkOrderPort(r.LocalPort),
                ucRemoteAddr = IPAddress.Parse(r.Remote).GetAddressBytes(),
                dwRemoteScopeId = r.RemoteScope,
                dwRemotePort = NetworkOrderPort(r.RemotePort),
                dwState = r.State,
                dwOwningPid = r.Pid
            };
            var ptr = Marshal.AllocHGlobal(OfficialRowSize);
            try
            {
                Marshal.StructureToPtr(native, ptr, false);
                Marshal.Copy(ptr, buffer, Header + i * OfficialRowSize, OfficialRowSize);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        return buffer;
    }

    private static void AssertRow(Row expected,
        (uint State, IPAddress Local, ushort LocalPort, IPAddress Remote, ushort RemotePort, uint Pid) actual)
    {
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Pid, actual.Pid);
        Assert.Equal(expected.LocalPort, actual.LocalPort);
        Assert.Equal(expected.RemotePort, actual.RemotePort);
        Assert.Equal(IPAddress.Parse(expected.Local).GetAddressBytes(), actual.Local.GetAddressBytes());
        Assert.Equal(IPAddress.Parse(expected.Remote).GetAddressBytes(), actual.Remote.GetAddressBytes());
        Assert.Equal((long)expected.LocalScope, actual.Local.ScopeId);
        Assert.Equal((long)expected.RemoteScope, actual.Remote.ScopeId);
    }

    [Fact]
    public void ManagedMirrorMatchesOfficialOffsetsAndSize()
    {
        // Valores literais da tabela oficial; nenhum vem do parser.
        Assert.Equal(56, Marshal.SizeOf<Mib6Row>());
        Assert.Equal(0, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.ucLocalAddr)));
        Assert.Equal(16, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.dwLocalScopeId)));
        Assert.Equal(20, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.dwLocalPort)));
        Assert.Equal(24, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.ucRemoteAddr)));
        Assert.Equal(40, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.dwRemoteScopeId)));
        Assert.Equal(44, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.dwRemotePort)));
        Assert.Equal(48, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.dwState)));
        Assert.Equal(52, (int)Marshal.OffsetOf<Mib6Row>(nameof(Mib6Row.dwOwningPid)));
    }

    [Fact]
    public void FixtureUsesPairwiseDistinctValuesSoNoFieldCanPassByCoincidence()
    {
        var row = First;
        var scalars = new[] { (long)row.LocalScope, row.LocalPort, row.RemoteScope, row.RemotePort, row.State, row.Pid };
        Assert.Equal(scalars.Length, scalars.Distinct().Count());
        Assert.NotEqual(0u, row.LocalScope);
        Assert.NotEqual(0u, row.RemoteScope);
    }

    [Fact]
    public void ReadTcp6ParsesEveryFieldOfOneRowFromTheOfficialLayout()
    {
        var table = BuildTable(First);

        var parsed = Assert.Single(NativeNetwork.ReadTcp6(table, table.Length));

        AssertRow(First, parsed);
        Assert.Equal(TcpConnectionState.Established, NativeNetwork.MapTcpState(parsed.State));
    }

    [Fact]
    public void ReadTcp6KeepsRemoteScopeRemotePortAndRemoteAddressSeparate()
    {
        var table = BuildTable(First);

        var parsed = Assert.Single(NativeNetwork.ReadTcp6(table, table.Length));

        Assert.Equal(443, parsed.RemotePort);
        Assert.Equal(22L, parsed.Remote.ScopeId);
        Assert.Equal(11L, parsed.Local.ScopeId);
        // Os bytes 36..39 da linha pertencem ao fim do endereço remoto (24..39); escopo e porta não podem pisá-los.
        Assert.Equal(IPAddress.Parse("2001:db8::abcd").GetAddressBytes(), parsed.Remote.GetAddressBytes());
    }

    [Fact]
    public void ReadTcp6ParsesSeveralRowsAndAdvancesBySizeOfOneNativeRow()
    {
        var table = BuildTable(First, Second, Third);

        var parsed = NativeNetwork.ReadTcp6(table, table.Length).ToArray();

        Assert.Equal(3, parsed.Length);
        AssertRow(First, parsed[0]);
        AssertRow(Second, parsed[1]);
        AssertRow(Third, parsed[2]);
        Assert.Equal(TcpConnectionState.Listen, NativeNetwork.MapTcpState(parsed[2].State));
        Assert.Equal(TcpConnectionState.TimeWait, NativeNetwork.MapTcpState(parsed[1].State));
    }

    [Fact]
    public void ReadTcp6KeepsUnknownStateZeroAsReportedByWindowsInsteadOfInventingOne()
    {
        var table = BuildTable(Second with { State = 0 });

        var parsed = Assert.Single(NativeNetwork.ReadTcp6(table, table.Length));

        Assert.Equal(0u, parsed.State);
        Assert.Equal(TcpConnectionState.Unknown, NativeNetwork.MapTcpState(parsed.State));
        Assert.Equal(51234, parsed.RemotePort);
    }

    [Fact]
    public void ReadTcp6RejectsACountThatExceedsTheBufferInsteadOfReadingOutOfBounds()
    {
        var table = BuildTable(First);
        BinaryPrimitives.WriteInt32LittleEndian(table, 2); // anuncia 2 linhas, só existe 1

        Assert.Throws<ArgumentException>(() => NativeNetwork.ReadTcp6(table, table.Length).ToArray());
    }

    [Fact]
    public void ReadTcp6HonorsTheValidLengthEvenWhenTheBackingArrayIsLarger()
    {
        // O array nativo é sempre alocado maior que o usado; só `length` delimita os dados válidos.
        var table = BuildTable(First, Second);
        var valid = Header + OfficialRowSize; // apenas a primeira linha é válida

        Assert.Throws<ArgumentException>(() => NativeNetwork.ReadTcp6(table, valid).ToArray());
    }

    [Fact]
    public void ReadTcp6RejectsNegativeCountAndBufferSmallerThanHeader()
    {
        var table = BuildTable(First);
        BinaryPrimitives.WriteInt32LittleEndian(table, -1);
        Assert.Throws<ArgumentException>(() => NativeNetwork.ReadTcp6(table, table.Length).ToArray());

        Assert.Throws<ArgumentException>(() => NativeNetwork.ReadTcp6(new byte[3], 3).ToArray());
    }

    [Fact]
    public void FixtureDetectsTheOffsetsUsedByTheDefectiveParser()
    {
        // Reimplementa o parser defeituoso (escopo remoto @36, porta remota @40) APENAS neste teste, para provar
        // que a fixture oficial o reprova e que o parser corrigido a satisfaz. Se alguém reintroduzir 36/40,
        // ReadTcp6ParsesEveryFieldOfOneRowFromTheOfficialLayout falha; aqui se demonstra por que falharia.
        static (long RemoteScope, ushort RemotePort, byte[] RemoteAddress) ReadWith(
            byte[] table, int remoteScopeOffset, int remotePortOffset)
        {
            var row = new ReadOnlySpan<byte>(table, Header, OfficialRowSize);
            var scope = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(remoteScopeOffset, 4));
            var port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(remotePortOffset, 2));
            return (scope, port, row.Slice(24, 16).ToArray());
        }

        var table = BuildTable(First);

        var defective = ReadWith(table, remoteScopeOffset: 36, remotePortOffset: 40);
        Assert.NotEqual(First.RemoteScope, (uint)defective.RemoteScope);
        Assert.NotEqual(First.RemotePort, defective.RemotePort);
        // Com 36/40 o "escopo" lido é um fragmento do endereço remoto (os 4 últimos bytes de 2001:db8::abcd).
        Assert.Equal(0xCDAB0000u, (uint)defective.RemoteScope);
        // E a "porta" lida é, na verdade, o escopo remoto 22 em big-endian: 0x1600 = 5632, não 443.
        Assert.Equal((ushort)0x1600, defective.RemotePort);

        var official = ReadWith(table, remoteScopeOffset: 40, remotePortOffset: 44);
        Assert.Equal(First.RemoteScope, (uint)official.RemoteScope);
        Assert.Equal(First.RemotePort, official.RemotePort);

        // O parser real tem que concordar com os offsets oficiais, não com os defeituosos.
        var actual = Assert.Single(NativeNetwork.ReadTcp6(table, table.Length));
        Assert.Equal(official.RemotePort, actual.RemotePort);
        Assert.Equal(official.RemoteScope, actual.Remote.ScopeId);
        Assert.NotEqual(defective.RemotePort, actual.RemotePort);
    }
}
