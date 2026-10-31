using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventRunnerTests
{
    private sealed class FakeCollector(string id, EventSourceStatus status = EventSourceStatus.Completed,
        bool throws = false, bool waits = false, bool limitHit = false, bool addLimitation = false,
        string? reportedId = null) : IEventEvidenceCollector
    {
        public string Id => id;
        public string Version => "1.0";
        public string Source => "Fake: testes";

        public async Task<EventCollectorResult> CollectAsync(Guid investigationId, EventQueryWindow window,
            CancellationToken cancellationToken)
        {
            if (waits) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (throws) throw new InvalidOperationException("falha simulada");
            var at = DateTimeOffset.UtcNow;
            RawEventRecord[] events = status == EventSourceStatus.Completed
                ? [
                    new RawEventRecord("System", "Application Error", 1000, 1, at, EventLevel.Error, 2,
                        null, null, "falha", false, [])
                ]
                : [];
            RawWerReport[] reports = status == EventSourceStatus.Completed
                ? [
                    new RawWerReport("R1", true, "Sistema", "ReportArchive", "AppCrash_app_1", "AppCrash",
                        null, at, at, "Loaded", "app.exe", null, null, null, null, [], [],
                        EvidenceConfidence.High)
                ]
                : [];
            string[] limitations = addLimitation || status is not EventSourceStatus.Completed
                ? ["Limitação sintética."]
                : [];
            return new(reportedId ?? id, Version, Source, at, at, TimeSpan.Zero, status, events.Length,
                reports.Length, limitHit, events, reports, [], limitations);
        }
    }

    [Fact]
    public async Task CompletedSourcesProduceCompletedInvestigation()
    {
        var result = await new EventInvestigationRunner([
            new FakeCollector("event-log"), new FakeCollector("wer")], new FixedTime())
            .RunAsync(EventWindow.Last7Days);
        Assert.Equal(EventInvestigationStatus.Completed, result.Status);
        Assert.Equal(2, result.Sources.Count);
        Assert.NotEmpty(result.Groups);
        Assert.All(result.Groups, g => Assert.Equal(CausalityStatus.NotAssessed, g.Causality));
    }

    [Fact]
    public async Task MixedOutcomeIsPartial()
    {
        var result = await new EventInvestigationRunner([
            new FakeCollector("event-log"), new FakeCollector("wer", throws: true)], new FixedTime())
            .RunAsync(EventWindow.Last7Days);
        Assert.Equal(EventInvestigationStatus.Partial, result.Status);
        Assert.Contains(result.Sources, s => s.Status == EventSourceStatus.Failed && s.Limitations.Count > 0);
    }

    [Fact]
    public async Task AllFailedIsFailed()
    {
        var result = await new EventInvestigationRunner([new FakeCollector("event-log", throws: true)],
            new FixedTime()).RunAsync(EventWindow.Last7Days);
        Assert.Equal(EventInvestigationStatus.Failed, result.Status);
    }

    [Fact]
    public async Task AllUnsupportedIsUnsupported()
    {
        var result = await new EventInvestigationRunner([
            new FakeCollector("event-log", EventSourceStatus.Unsupported)], new FixedTime())
            .RunAsync(EventWindow.Last7Days);
        Assert.Equal(EventInvestigationStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task CancellationPreservesEverySource()
    {
        using var cancellation = new CancellationTokenSource();
        var running = new EventInvestigationRunner([
            new FakeCollector("event-log", waits: true), new FakeCollector("wer")], new FixedTime())
            .RunAsync(EventWindow.Last7Days, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        var result = await running;
        Assert.Equal(EventInvestigationStatus.Cancelled, result.Status);
        Assert.Equal(2, result.Sources.Count);
        Assert.All(result.Sources, s => Assert.Equal(EventSourceStatus.Cancelled, s.Status));
    }

    [Fact]
    public async Task CompletedWithLimitationsBecomesPartial()
    {
        var result = await new EventInvestigationRunner([
            new FakeCollector("event-log", addLimitation: true)], new FixedTime())
            .RunAsync(EventWindow.Last7Days);
        Assert.Equal(EventInvestigationStatus.Partial, result.Status);
        Assert.Equal(EventSourceStatus.Partial, result.Sources[0].Status);
    }

    [Fact]
    public async Task LimitHitBecomesPartialAndNeverCompleted()
    {
        var result = await new EventInvestigationRunner([
            new FakeCollector("event-log", limitHit: true)], new FixedTime())
            .RunAsync(EventWindow.Last7Days);
        Assert.Equal(EventInvestigationStatus.Partial, result.Status);
        Assert.True(result.Sources[0].LimitHit);
    }

    [Fact]
    public async Task CollectorWithWrongIdIsFailed()
    {
        var result = await new EventInvestigationRunner([
            new FakeCollector("event-log", reportedId: "outro")], new FixedTime())
            .RunAsync(EventWindow.Last7Days);
        Assert.Equal(EventSourceStatus.Failed, result.Sources[0].Status);
        Assert.Contains(result.Sources[0].Limitations, l => l.Contains("Contrato"));
    }

    [Fact]
    public void DuplicateCollectorsAreRejected() =>
        Assert.Throws<ArgumentException>(() => new EventInvestigationRunner([
            new FakeCollector("a"), new FakeCollector("a")]));

    [Fact]
    public void NoCollectorsAreRejected() =>
        Assert.Throws<ArgumentException>(() => new EventInvestigationRunner([]));

    [Fact]
    public async Task InvalidWindowIsRejected()
    {
        var runner = new EventInvestigationRunner([new FakeCollector("event-log")], new FixedTime());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(EventWindow.Custom));
    }

    [Fact]
    public async Task ProgressNeverReportsCompletedBeforeTheEnd()
    {
        var updates = new List<EventInvestigationProgress>();
        var progress = new Progress<EventInvestigationProgress>(updates.Add);
        await new EventInvestigationRunner([new FakeCollector("event-log"), new FakeCollector("wer")],
            new FixedTime()).RunAsync(EventWindow.Last7Days, progress: progress);
        await Task.Delay(50);
        Assert.Contains(updates, u => u.Status == EventSourceStatus.Completed && u.Finished == 2);
    }

    [Fact]
    public async Task PeriodIsStoredInUtcWithTheRequestedPreset()
    {
        var clock = new FixedTime();
        var result = await new EventInvestigationRunner([new FakeCollector("event-log")], clock)
            .RunAsync(EventWindow.Last24Hours);
        Assert.Equal(EventWindow.Last24Hours, result.Window);
        Assert.Equal(TimeSpan.Zero, result.PeriodFromUtc.Offset);
        Assert.Equal(clock.Now, result.PeriodToUtc);
    }
}
