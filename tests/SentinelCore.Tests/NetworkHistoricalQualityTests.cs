using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows.Network;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Sinalização conservadora, derivada em leitura, dos registros gravados pelo coletor de endpoints 1.0.0, cujo
/// <c>ReadTcp6</c> lia escopo e porta remotos IPv6 dos offsets 36/40 (D1). A anotação nunca altera o envelope
/// durável e nunca inventa valor corrigido.
/// </summary>
public sealed class NetworkHistoricalQualityTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 31, 19, 5, 18, TimeSpan.Zero);

    private static NetworkSourceSummary EndpointSource(string collectorVersion) =>
        new(NetworkQualityCatalog.EndpointCollectorId, collectorVersion, NetworkSourceCategory.Endpoints,
            "GetExtendedTcpTable/GetExtendedUdpTable (IP Helper, owner PID)", NetworkSourceStatus.Completed,
            Observed, Observed, TimeSpan.FromMilliseconds(5), 1, false, []);

    private static NetworkEndpoint Endpoint(NetworkProtocol protocol, NetworkAddressFamily family, ushort localPort) =>
        new(protocol, family, null, localPort, null, protocol == NetworkProtocol.Tcp ? (ushort)0 : null,
            protocol == NetworkProtocol.Tcp ? TcpConnectionState.Listen : null, 100 + localPort, null, null,
            NetworkProcessStatus.AccessDenied, Observed, "Test");

    private static NetworkSession Session(string collectorVersion, bool details, params NetworkEndpoint[] endpoints) =>
        NetworkPersistenceTests.CreateValidSession(sampleCount: 2) with
        {
            Sources = [EndpointSource(collectorVersion)],
            IncludesEndpointDetails = details,
            EndpointDetails = endpoints
        };

    [Fact]
    public void CollectorThatFixedTheOffsetsReportsANewVersionDistinctFromTheDefectiveOne()
    {
        var collector = new NetworkEndpointCollector();

        Assert.Equal(NetworkQualityCatalog.EndpointCollectorId, collector.Id);
        Assert.Equal(NetworkQualityCatalog.FixedCollectorVersion, collector.Version);
        Assert.NotEqual(NetworkQualityCatalog.DefectiveCollectorVersion, collector.Version);
        Assert.Equal("1.0.0", NetworkQualityCatalog.DefectiveCollectorVersion);
    }

    [Fact]
    public void SessionFromDefectiveCollectorWithTcpIpv6RowsIsFlaggedAsUnverifiableWithoutClaimingAnyCorrectValue()
    {
        var session = Session("1.0.0", true,
            Endpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv6, 135),
            Endpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv6, 445),
            Endpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 80),
            Endpoint(NetworkProtocol.Udp, NetworkAddressFamily.IPv6, 546));

        var annotation = NetworkQualityCatalog.Evaluate(session);

        Assert.NotNull(annotation);
        Assert.Equal("NET-TCP6-REMOTE-OFFSET", annotation!.DefectId);
        Assert.Equal(2, annotation.AffectedEndpoints);
        Assert.Contains("remotePort", annotation.AffectedFields);
        Assert.Contains("remoteScopeId", annotation.AffectedFields);
        Assert.Contains("não verificáveis", annotation.Summary, StringComparison.OrdinalIgnoreCase);
        // Não afirma que as contagens são sempre independentes: a porta remota compõe a chave de deduplicação.
        Assert.Contains("chave de deduplicação", annotation.Summary);
        Assert.DoesNotContain("e contagens não dependem", annotation.Summary);
        Assert.Contains("Nenhum valor corrigido", annotation.Summary);
        Assert.False(annotation.ValuesProvenWrong);
    }

    [Theory]
    [InlineData("1.0.1")]
    [InlineData("2.0.0")]
    public void SessionFromTheFixedCollectorIsNotFlagged(string version)
    {
        var session = Session(version, true, Endpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv6, 135));

        Assert.Null(NetworkQualityCatalog.Evaluate(session));
    }

    [Fact]
    public void DefectiveCollectorSessionWithoutTcpIpv6RowsOrWithoutEndpointDetailsIsNotFlagged()
    {
        Assert.Null(NetworkQualityCatalog.Evaluate(Session("1.0.0", true,
            Endpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 80),
            Endpoint(NetworkProtocol.Udp, NetworkAddressFamily.IPv6, 546))));

        // Sem detalhes por endpoint não há campo persistido a questionar.
        Assert.Null(NetworkQualityCatalog.Evaluate(Session("1.0.0", false)));
    }

    [Fact]
    public void SessionWithoutAnEndpointSourceIsNotFlagged()
    {
        var session = Session("1.0.0", true, Endpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv6, 135)) with
        {
            Sources = []
        };

        Assert.Null(NetworkQualityCatalog.Evaluate(session));
    }

    [Fact]
    public void EvaluatingTheAnnotationDoesNotMutateTheSession()
    {
        var session = Session("1.0.0", true, Endpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv6, 135));
        var before = session.EndpointDetails.ToArray();

        _ = NetworkQualityCatalog.Evaluate(session);

        Assert.Equal(before, session.EndpointDetails);
        Assert.Equal("1.0.0", session.Sources[0].CollectorVersion);
    }
}
