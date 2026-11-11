using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Integrações sintéticas entre o runner de rede, a persistência Markdown/SQLite e a reabertura.
/// Nenhum coletor operacional, socket ou recurso real do computador é consultado.
/// </summary>
public sealed class Stage7b3IntegrationTests
{
    private static NetworkSessionRequest IntervalRequest() =>
        new(NetworkSessionPreset.Seconds30, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task CancelledRunnerSessionPersistsAndReopensWithPartialCoverage()
    {
        var root = NewRoot();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var clock = new SyntheticSessionClock();
            var collector = new CancellingCollector(cancellation, cancelAt: 2);
            var runner = new NetworkSessionRunner([collector], clock: clock,
                selfMeterFactory: () => new FakeSelfOverheadMeter());

            var session = await runner.RunAsync(IntervalRequest(), cancellationToken: cancellation.Token);

            Assert.Equal(NetworkSessionStatus.Cancelled, session.Status);
            Assert.NotNull(session.Coverage);
            Assert.Equal(NetworkExecutionStatus.Cancelled, session.Coverage!.Execution);
            Assert.Equal(NetworkCoverageStatus.Partial, session.Coverage.Status);
            Assert.Equal(3, session.Samples.Count);

            var store = new MemoryStore(root);
            var bytes = await store.SaveNetworkSessionAsync(session);
            var reopened = await new MemoryStore(root).LoadNetworkSessionsAsync();

            Assert.True(bytes > 0);
            Assert.Empty(reopened.Warnings);
            var copy = Assert.Single(reopened.Sessions);
            Assert.Equal(session.Id, copy.Id);
            Assert.Equal(NetworkSessionStatus.Cancelled, copy.Status);
            Assert.Equal(session.Samples.Count, copy.Samples.Count);
            Assert.NotNull(copy.Coverage);
            Assert.Equal(NetworkExecutionStatus.Cancelled, copy.Coverage!.Execution);
            Assert.Equal(NetworkCoverageStatus.Partial, copy.Coverage.Status);
            Assert.Equal(session.Coverage.MissedOpportunities, copy.Coverage.MissedOpportunities);
            Assert.True(File.Exists(store.GetNetworkReportPath(session.Id)));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task MissedOpportunityCoverageSurvivesPersistAndReopenWithoutRecomputation()
    {
        var root = NewRoot();
        try
        {
            var clock = new SyntheticSessionClock();
            var collector = new SlowFirstCollector(clock);
            var runner = new NetworkSessionRunner([collector], clock: clock,
                selfMeterFactory: () => new FakeSelfOverheadMeter());

            var session = await runner.RunAsync(NetworkSessionRequest.ForPreset(NetworkSessionPreset.Seconds30));

            Assert.Equal(NetworkSessionStatus.Partial, session.Status);
            Assert.NotNull(session.Coverage);
            Assert.Equal(NetworkExecutionStatus.Completed, session.Coverage!.Execution);
            Assert.Equal(NetworkCoverageStatus.Partial, session.Coverage.Status);
            Assert.True(session.Coverage.MissedOpportunities >= 1);

            var store = new MemoryStore(root);
            await store.SaveNetworkSessionAsync(session);
            var reopened = await new MemoryStore(root).LoadNetworkSessionsAsync();

            Assert.Empty(reopened.Warnings);
            var copy = Assert.Single(reopened.Sessions);
            Assert.Equal(session.Id, copy.Id);
            Assert.Equal(NetworkSessionStatus.Partial, copy.Status);
            Assert.Equal(NetworkExecutionStatus.Completed, copy.Coverage!.Execution);
            Assert.Equal(NetworkCoverageStatus.Partial, copy.Coverage.Status);
            Assert.Equal(session.Coverage.MissedOpportunities, copy.Coverage.MissedOpportunities);
            Assert.Equal(session.Samples.Count, copy.Samples.Count);
        }
        finally
        {
            Delete(root);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "SentinelCore7b3Tests", Guid.NewGuid().ToString("N"));

    private static void Delete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CancellingCollector(CancellationTokenSource cancellation, int cancelAt)
        : INetworkSampleCollector
    {
        private readonly SyntheticNetworkCollector inner = new();
        private int index;

        public string Id => inner.Id;
        public string Version => inner.Version;
        public string Source => inner.Source;
        public NetworkSourceCategory Category => inner.Category;

        public Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context, DateTimeOffset timestampUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref index) - 1;
            var reading = inner.ReadAsync(context, timestampUtc, cancellationToken);
            if (current == cancelAt) cancellation.Cancel();
            return reading;
        }
    }

    private sealed class SlowFirstCollector(SyntheticSessionClock clock) : INetworkSampleCollector
    {
        private readonly SyntheticNetworkCollector inner = new();
        private int index;

        public string Id => inner.Id;
        public string Version => inner.Version;
        public string Source => inner.Source;
        public NetworkSourceCategory Category => inner.Category;

        public Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context, DateTimeOffset timestampUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref index) - 1;
            if (current == 0) clock.Advance(TimeSpan.FromMilliseconds(1150));
            return inner.ReadAsync(context, timestampUtc, cancellationToken);
        }
    }
}
