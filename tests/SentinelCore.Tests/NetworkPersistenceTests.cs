using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class NetworkPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset StartUtc = new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(), "SentinelCoreNetworkTests", Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(root);

    [Fact]
    public async Task SaveLoadAndReopenPreservesSessionStatisticsSamplesAndObservations()
    {
        var session = CreateValidSession(sampleCount: 5, includeObservation: true);
        var store = NewStore();

        var bytes = await store.SaveNetworkSessionAsync(session);
        var loaded = await NewStore().LoadNetworkSessionsAsync();

        Assert.Empty(loaded.Warnings);
        var copy = Assert.Single(loaded.Sessions);
        Assert.Equal(session.Id, copy.Id);
        Assert.Equal(session.Preset, copy.Preset);
        Assert.Equal(session.Status, copy.Status);
        Assert.Equal(session.Samples.Count, copy.Samples.Count);
        Assert.Equal(session.Statistics.PeakTcpConnections, copy.Statistics.PeakTcpConnections);
        Assert.Equal(session.Statistics.MeanTcpConnections, copy.Statistics.MeanTcpConnections);
        Assert.Equal(session.Observations.Count, copy.Observations.Count);
        Assert.NotNull(copy.Coverage);
        Assert.Equal(NetworkExecutionStatus.Completed, copy.Coverage!.Execution);
        Assert.Equal(NetworkCoverageStatus.Complete, copy.Coverage.Status);
        Assert.True(bytes > 0);
        Assert.True(File.Exists(store.GetNetworkReportPath(session.Id)));
        Assert.True(File.Exists(Path.Combine(root, "memory", "NETWORK.md")));
        Assert.Equal(9, ReadUserVersion());
    }

    [Fact]
    public async Task NetworkRecordEnvelopeContainsPayloadAndMatchingSqliteChecksum()
    {
        var session = CreateValidSession(sampleCount: 3);
        var store = NewStore();
        await store.SaveNetworkSessionAsync(session);

        var recordPath = Path.Combine(root, "memory", "network", $"{session.Id:D}.md");
        Assert.True(File.Exists(recordPath));

        var markdown = await File.ReadAllTextAsync(recordPath, Encoding.UTF8);
        Assert.Contains("<!-- sentinelcore:payload:v1 -->", markdown);
        Assert.Contains("<!-- sha256:", markdown);

        var json = RecordEnvelope.Unwrap(markdown.Replace("\r\n", "\n"));
        var expectedHash = RecordEnvelope.Hash(json);

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT PayloadHash, SampleCount, IncludesAddresses FROM NetworkSessions WHERE Id = $id";
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedHash, reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
    }

    [Fact]
    public async Task RetryIsIdempotentAndDoesNotRewriteHistory()
    {
        var session = CreateValidSession(sampleCount: 2);
        var store = NewStore();
        await store.SaveNetworkSessionAsync(session);

        var record = Directory.GetFiles(Path.Combine(root, "memory", "network"), "*.md").Single();
        var before = await File.ReadAllTextAsync(record, Encoding.UTF8);
        var modified = File.GetLastWriteTimeUtc(record);

        await store.SaveNetworkSessionAsync(session);

        Assert.Equal(before, await File.ReadAllTextAsync(record, Encoding.UTF8));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(record));
        Assert.Single((await NewStore().LoadNetworkSessionsAsync()).Sessions);
    }

    [Fact]
    public async Task SameIdDifferentPayloadIsRejectedAndPreservesOriginal()
    {
        var session = CreateValidSession(sampleCount: 2);
        var store = NewStore();
        await store.SaveNetworkSessionAsync(session);

        var tampered = session with { CompletedUtc = session.CompletedUtc.AddSeconds(5) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveNetworkSessionAsync(tampered));

        var loaded = await NewStore().LoadNetworkSessionsAsync();
        var persisted = Assert.Single(loaded.Sessions);
        Assert.Equal(session.CompletedUtc, persisted.CompletedUtc);
    }

    [Fact]
    public async Task SessionIdCannotCollideWithAuditHardwareEventOrPerformanceHistory()
    {
        var store = NewStore();
        var audit = Samples.Audit(StartUtc);
        await store.SaveAsync(audit);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveNetworkSessionAsync(CreateValidSession() with { Id = audit.Id }));

        var networkSession = CreateValidSession();
        await store.SaveNetworkSessionAsync(networkSession);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Samples.Audit(StartUtc, id: networkSession.Id)));
    }

    [Fact]
    public async Task CorruptNetworkSessionIsPreservedAndExcludedWithWarning()
    {
        var validSession = CreateValidSession(sampleCount: 2);
        var corruptSession = CreateValidSession(sampleCount: 2);
        var store = NewStore();
        await store.SaveNetworkSessionAsync(validSession);
        await store.SaveNetworkSessionAsync(corruptSession);

        var corruptPath = Path.Combine(root, "memory", "network", $"{corruptSession.Id:D}.md");
        await File.WriteAllTextAsync(corruptPath, "corrupted-network-envelope", Encoding.UTF8);

        var loaded = await NewStore().LoadNetworkSessionsAsync();
        var recovered = Assert.Single(loaded.Sessions);
        Assert.Equal(validSession.Id, recovered.Id);
        Assert.NotEmpty(loaded.Warnings);
        Assert.Contains(loaded.Warnings, warning => warning.Contains(corruptSession.Id.ToString("D")));
        Assert.Equal("corrupted-network-envelope", await File.ReadAllTextAsync(corruptPath, Encoding.UTF8));
    }

    [Fact]
    public async Task NetworkIndexIsRebuiltFromMarkdownAfterDatabaseLoss()
    {
        var session = CreateValidSession(sampleCount: 4, includeObservation: true);
        await NewStore().SaveNetworkSessionAsync(session);

        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));

        var loaded = await NewStore().LoadNetworkSessionsAsync();
        var rebuilt = Assert.Single(loaded.Sessions);
        Assert.Equal(session.Id, rebuilt.Id);
        Assert.Equal(4, rebuilt.Samples.Count);
        Assert.Single(rebuilt.Observations);
        Assert.Equal(9, ReadUserVersion());

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NetworkSessions; SELECT COUNT(*) FROM NetworkSources; SELECT COUNT(*) FROM NetworkObservations;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.NextResult() && reader.Read());
        Assert.Equal((long)session.Sources.Count, reader.GetInt64(0));
        Assert.True(reader.NextResult() && reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
    }

    [Fact]
    public async Task MissingNetworkReportIsRecreatedOnLoad()
    {
        var session = CreateValidSession(sampleCount: 2);
        var store = NewStore();
        await store.SaveNetworkSessionAsync(session);

        var reportPath = store.GetNetworkReportPath(session.Id);
        File.Delete(reportPath);
        Assert.False(File.Exists(reportPath));

        await NewStore().LoadNetworkSessionsAsync();
        Assert.True(File.Exists(reportPath));
    }

    [Fact]
    public async Task DefaultPrivacyOmitsAddressesAndRejectsUnauthorizedAddresses()
    {
        var endpointWithIp = new NetworkEndpoint(
            Protocol: NetworkProtocol.Tcp,
            Family: NetworkAddressFamily.IPv4,
            LocalAddress: "192.0.2.15",
            LocalPort: 50100,
            RemoteAddress: "198.51.100.42",
            RemotePort: 443,
            TcpState: TcpConnectionState.Established,
            Pid: 1234,
            ProcessIdentity: "1234:100",
            ProcessName: "app.exe",
            ProcessStatus: NetworkProcessStatus.Resolved,
            ObservedUtc: StartUtc,
            Source: "IPHelper");

        var unauthorized = CreateValidSession(
            sampleCount: 1,
            preset: NetworkSessionPreset.Snapshot,
            includeAddresses: false,
            endpoints: [endpointWithIp]);

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveNetworkSessionAsync(unauthorized));

        var redactedEndpoint = endpointWithIp with { LocalAddress = null, RemoteAddress = null };
        var defaultPrivate = CreateValidSession(
            sampleCount: 1,
            preset: NetworkSessionPreset.Snapshot,
            includeAddresses: false,
            endpoints: [redactedEndpoint]);

        var store = NewStore();
        await store.SaveNetworkSessionAsync(defaultPrivate);
        var recordText = await File.ReadAllTextAsync(
            Path.Combine(root, "memory", "network", $"{defaultPrivate.Id:D}.md"), Encoding.UTF8);
        var reportText = await File.ReadAllTextAsync(
            store.GetNetworkReportPath(defaultPrivate.Id), Encoding.UTF8);

        Assert.DoesNotContain("192.0.2.15", recordText);
        Assert.DoesNotContain("198.51.100.42", recordText);
        Assert.DoesNotContain("192.0.2.15", reportText);

        var optInSession = CreateValidSession(
            sampleCount: 1,
            preset: NetworkSessionPreset.Snapshot,
            includeAddresses: true,
            endpoints: [endpointWithIp]);
        await store.SaveNetworkSessionAsync(optInSession);

        var loaded = await NewStore().LoadNetworkSessionsAsync();
        var loadedOptIn = Assert.Single(loaded.Sessions, item => item.Id == optInSession.Id);
        Assert.True(loadedOptIn.IncludesAddresses);
        var detail = Assert.Single(loadedOptIn.EndpointDetails);
        Assert.Equal("192.0.2.15", detail.LocalAddress);
        Assert.Equal("198.51.100.42", detail.RemoteAddress);
    }

    [Fact]
    public async Task NetworkSessionDoesNotRenewAuditLastSuccess()
    {
        var store = NewStore();
        var clock = new FixedTime();
        var oldAudit = Samples.Audit(clock.Now.AddDays(-8));
        await store.SaveAsync(oldAudit);
        await store.SaveNetworkSessionAsync(CreateValidSession(sampleCount: 2));

        var audits = (await store.LoadAsync()).Audits;
        var freshness = new FreshnessEngine(clock).Evaluate(audits, AuditKind.Basic);
        Assert.Equal(FreshnessState.Outdated, freshness.State);
    }

    [Fact]
    public async Task MigrationFromSchemaFiveAddsNetworkTablesAndPreservesExistingRecords()
    {
        var store = NewStore();
        await store.SaveAsync(Samples.Audit(StartUtc));
        await store.SaveSessionAsync(PerformanceSamples.Session());

        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE NetworkObservations; DROP TABLE NetworkSources; DROP TABLE NetworkSessions; PRAGMA user_version=5";
            command.ExecuteNonQuery();
        }

        var networkLoaded = await NewStore().LoadNetworkSessionsAsync();
        Assert.Empty(networkLoaded.Sessions);
        Assert.Single((await NewStore().LoadAsync()).Audits);
        Assert.Single((await NewStore().LoadSessionsAsync()).Sessions);
        Assert.Equal(9, ReadUserVersion());
    }

    [Fact]
    public async Task FutureSchemaTenIsRejectedWithoutCorruptingDatabase()
    {
        await NewStore().LoadNetworkSessionsAsync();
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=10";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadNetworkSessionsAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "data"), "*.corrupt-*"));
    }

    [Fact]
    public async Task ReportSanitizesUntrustedTextAndKeepsCausalityNotAssessed()
    {
        var baseSession = CreateValidSession(sampleCount: 3, includeObservation: true);
        var hostileObservation = baseSession.Observations[0] with
        {
            SubjectName = "<script>alert(1)</script> `hostile`\r\nlinha"
        };
        var session = baseSession with { Observations = [hostileObservation] };

        var store = NewStore();
        await store.SaveNetworkSessionAsync(session);
        var report = await File.ReadAllTextAsync(store.GetNetworkReportPath(session.Id), Encoding.UTF8);

        Assert.DoesNotContain("<script>", report);
        Assert.Contains("&lt;script&gt;", report);
        Assert.Contains("não avaliada", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não renova", report, StringComparison.OrdinalIgnoreCase);
    }

    internal static NetworkSession CreateValidSession(
        int sampleCount = 3,
        NetworkSessionPreset preset = NetworkSessionPreset.Seconds30,
        bool includeAddresses = false,
        bool includeObservation = false,
        IReadOnlyList<NetworkEndpoint>? endpoints = null)
    {
        var sessionId = Guid.NewGuid();
        var requestedDuration = preset == NetworkSessionPreset.Snapshot
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(30);
        var sampleInterval = preset == NetworkSessionPreset.Snapshot
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1);
        var duration = TimeSpan.FromSeconds(Math.Max(1, sampleCount - 1));
        var completedUtc = StartUtc.Add(duration);

        var samples = Enumerable.Range(0, sampleCount)
            .Select(index => new NetworkSampleAggregate(
                Index: index,
                TimestampUtc: StartUtc.AddSeconds(index),
                ElapsedSeconds: index,
                Warmup: index == 0 && sampleCount > 1,
                TcpConnections: 20 + index * 2,
                UdpEndpoints: 8 + index,
                ListeningTcp: 5,
                DistinctLocalPorts: 18 + index,
                EphemeralTcpConnections: 12 + index,
                EphemeralUdpEndpoints: 4,
                TcpAttemptFailures: (uint)(2 + index),
                TcpStates:
                [
                    new NetworkStateCount(TcpConnectionState.Established, 15 + index * 2),
                    new NetworkStateCount(TcpConnectionState.Listen, 5)
                ],
                TopLocalPorts:
                [
                    new NetworkPortCount(443, 10 + index, 2),
                    new NetworkPortCount(50100, 5 + index, 1)
                ],
                TopProcesses:
                [
                    new NetworkProcessCount("1234:100", 1234, "app.exe", 15 + index * 2, 4, 1)
                ],
                ResolvedProcesses: 24 + index * 3,
                UnresolvedProcesses: 4,
                DuplicateRowsIgnored: 0,
                CollectionDurationMilliseconds: 6,
                Limitations: []))
            .ToArray();

        var statistics = new NetworkStatisticsEngine().Compute(samples);
        var details = endpoints ??
        [
            new NetworkEndpoint(
                Protocol: NetworkProtocol.Tcp,
                Family: NetworkAddressFamily.IPv4,
                LocalAddress: includeAddresses ? "192.0.2.10" : null,
                LocalPort: 50100,
                RemoteAddress: includeAddresses ? "198.51.100.20" : null,
                RemotePort: 443,
                TcpState: TcpConnectionState.Established,
                Pid: 1234,
                ProcessIdentity: "1234:100",
                ProcessName: "app.exe",
                ProcessStatus: NetworkProcessStatus.Resolved,
                ObservedUtc: completedUtc,
                Source: "IPHelper")
        ];

        var sources = new[]
        {
            new NetworkSourceSummary(
                CollectorId: "network-endpoints",
                CollectorVersion: "1.0.0",
                Category: NetworkSourceCategory.Endpoints,
                Source: "GetExtendedTcpTable/GetExtendedUdpTable",
                Status: NetworkSourceStatus.Completed,
                StartedUtc: StartUtc,
                CompletedUtc: completedUtc,
                Duration: duration,
                SamplesRead: sampleCount,
                LimitHit: false,
                Limitations: [])
            {
                MeanReadMilliseconds = 4.2,
                MaxReadMilliseconds = 6.5
            }
        };

        var portRanges = new[]
        {
            new NetworkDynamicPortRange(
                Protocol: NetworkProtocol.Tcp,
                Family: NetworkAddressFamily.IPv4,
                StartPort: 49152,
                PortCount: 16384,
                Source: "HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters",
                IsWindowsDefault: false,
                Exclusions: [],
                Limitations: [])
        };

        var adapters = new[]
        {
            new NetworkAdapterSummary("eth0", "Ethernet", "Up", true, true, 1, 1)
        };

        var observations = includeObservation
            ? new[]
            {
                new NetworkObservation(
                    Id: $"NET-ENDPOINT-GROWTH@{StartUtc.UtcDateTime:O}#session",
                    RuleId: "NET-ENDPOINT-GROWTH",
                    RuleVersion: "1.0",
                    Kind: NetworkObservationKind.EndpointGrowth,
                    Title: "Crescimento de conexões TCP observado",
                    Statement: "As conexões TCP cresceram durante a janela observada; não identifica causa.",
                    Severity: EvidenceSeverity.PotentialImpact,
                    Confidence: EvidenceConfidence.Low,
                    Causality: CausalityStatus.NotAssessed,
                    Metric: "network.tcp.connections",
                    Unit: "conexões",
                    Threshold: 200,
                    Subject: "1234:100",
                    SubjectName: "app.exe",
                    WindowFromUtc: StartUtc,
                    WindowToUtc: completedUtc,
                    Duration: duration,
                    SupportingSampleCount: sampleCount,
                    EvaluatedSampleCount: sampleCount,
                    ObservedMin: 20,
                    ObservedMax: 20 + (sampleCount - 1) * 2,
                    ObservedMean: 22,
                    ObservedP95: null,
                    Source: "network-endpoints",
                    Points: samples.Select(s => new NetworkEvidencePoint(s.TimestampUtc, s.Index, s.TcpConnections, "conexões")).ToArray(),
                    Limitations: ["Crescimento observado não comprova esgotamento."])
            }
            : Array.Empty<NetworkObservation>();

        var notes = new[]
        {
            new NetworkAnalysisNote(
                RuleId: "NET-ENDPOINT-GROWTH",
                RuleVersion: "1.0",
                Evaluated: true,
                Detail: includeObservation ? "Observado." : "Condição não observada na janela.",
                Reason: includeObservation ? NetworkNoteReason.Observed : NetworkNoteReason.ConditionNotObserved)
        };

        var coverage = new NetworkCoverage(
            Execution: NetworkExecutionStatus.Completed,
            Status: NetworkCoverageStatus.Complete,
            ExpectedSamples: sampleCount,
            CollectedSamples: sampleCount,
            MissedOpportunities: 0,
            SampleLimitReached: false,
            DetailLimitReached: false,
            PortRangeUnavailable: false,
            UnavailableCollectors: [],
            Limitations: []);

        var selfOverhead = new SelfOverheadSummary(
            Samples: sampleCount,
            CpuPercentMean: 0.4,
            CpuPercentMax: 0.9,
            WorkingSetBytesMax: 42UL * 1024 * 1024,
            PrivateBytesMax: 30UL * 1024 * 1024,
            CollectionDurationMeanMilliseconds: 4.5,
            CollectionDurationMaxMilliseconds: 6.0);

        return new NetworkSession(
            FormatVersion: 1,
            Id: sessionId,
            SoftwareVersion: SoftwareInfo.Version,
            SchemaVersion: SoftwareInfo.NetworkSchemaVersion,
            Preset: preset,
            RequestedDuration: requestedDuration,
            SampleInterval: sampleInterval,
            StartedUtc: StartUtc,
            CompletedUtc: completedUtc,
            Duration: duration,
            Status: NetworkSessionStatus.Completed,
            IncludesEndpointDetails: details.Count > 0,
            IncludesAddresses: includeAddresses,
            Sources: sources,
            PortRanges: portRanges,
            Adapters: adapters,
            TcpStatistics: null,
            UdpStatistics: null,
            Statistics: statistics,
            Observations: observations,
            AnalysisNotes: notes,
            Samples: samples,
            EndpointDetails: details,
            SelfOverhead: selfOverhead,
            TcpipEvents: null,
            Limitations: [],
            Coverage: coverage);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False");
        connection.Open();
        return connection;
    }

    private int ReadUserVersion()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
