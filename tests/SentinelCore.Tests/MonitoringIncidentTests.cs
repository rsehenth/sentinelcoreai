using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Registro manual de incidentes: o momento do clique é preservado separado de um horário aproximado informado,
/// descrições são limitadas, o limite é declarado e nenhum incidente vira evento do Windows.
/// </summary>
public sealed class MonitoringIncidentTests
{
    private static async Task<(MonitoringSession Session, MonitoringIncident Incident)> RecordWhileRunning(
        MonitoringLimits? limits = null, MonitoringIncidentRequest? request = null)
    {
        var store = new FakeMonitoringStore();
        var blocking = new BlockingMonitoringSource();
        var runner = MonitoringSamples.Runner(store, [blocking], limits);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(1)),
            cancellationToken: cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var occurred = DateTimeOffset.UtcNow.AddMinutes(-2);
        var recorded = runner.TryRecordIncident(request ?? new MonitoringIncidentRequest(
            MonitoringIncidentCategory.BlackScreen, "tela preta no navegador", occurred), out var incident);
        Assert.True(recorded);
        Assert.NotNull(incident);

        blocking.Release.TrySetResult();
        var session = await running;
        return (session, incident!);
    }

    [Fact]
    public async Task IncidentKeepsRecordedTimeSeparateFromInformedOccurrence()
    {
        var (session, incident) = await RecordWhileRunning();

        var copy = Assert.Single(session.Incidents);
        Assert.Equal(incident.Id, copy.Id);
        Assert.Equal(session.Id, copy.SessionId);
        Assert.Equal(MonitoringIncidentCategory.BlackScreen, copy.Category);
        Assert.Equal(MonitoringIncidentPrecision.Approximate, copy.Precision);
        Assert.NotNull(copy.OccurredUtc);
        Assert.True(copy.RecordedUtc > copy.OccurredUtc);
        Assert.Contains(MonitoringGuidance.IncidentLimitation, copy.Limitations);
        Assert.Contains(copy.Limitations,
            item => item.Contains("manual", StringComparison.OrdinalIgnoreCase));
        Assert.True(copy.SessionElapsedSeconds >= 0);
    }

    [Fact]
    public async Task IncidentWithoutInformedOccurrenceHasUnknownPrecision()
    {
        var (session, _) = await RecordWhileRunning(request: new MonitoringIncidentRequest(
            MonitoringIncidentCategory.Slowness, null, null));

        var incident = Assert.Single(session.Incidents);
        Assert.Null(incident.OccurredUtc);
        Assert.Equal(MonitoringIncidentPrecision.Unknown, incident.Precision);
        Assert.Contains(incident.Limitations,
            item => item.Contains("Nenhum horário de ocorrência", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExcessiveDescriptionIsTruncatedAndDeclared()
    {
        var limits = MonitoringSamples.FastLimits();
        var description = new string('x', 120);
        var (session, _) = await RecordWhileRunning(limits, new MonitoringIncidentRequest(
            MonitoringIncidentCategory.Other, description, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var incident = Assert.Single(session.Incidents);
        Assert.Equal(limits.MaxIncidentDescriptionLength, incident.Description!.Length);
        Assert.Contains(incident.Limitations,
            item => item.Contains("truncada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FutureOccurrenceIsRejectedWithoutInventingTime()
    {
        var store = new FakeMonitoringStore();
        var blocking = new BlockingMonitoringSource();
        var runner = MonitoringSamples.Runner(store, [blocking]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(1)),
            cancellationToken: cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<ArgumentException>(() => runner.TryRecordIncident(
            new MonitoringIncidentRequest(MonitoringIncidentCategory.Freeze, null,
                DateTimeOffset.UtcNow.AddHours(3)), out _));
        Assert.Empty(runner.Incidents);

        blocking.Release.TrySetResult();
        var session = await running;
        Assert.Empty(session.Incidents);
    }

    [Fact]
    public async Task IncidentLimitStopsFurtherRegistrations()
    {
        var store = new FakeMonitoringStore();
        var blocking = new BlockingMonitoringSource();
        var limits = MonitoringSamples.FastLimits();
        limits = limits with { MaxIncidents = 2 };
        var runner = MonitoringSamples.Runner(store, [blocking], limits);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(1)),
            cancellationToken: cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runner.TryRecordIncident(new MonitoringIncidentRequest(
            MonitoringIncidentCategory.Slowness, "primeiro", null), out _));
        Assert.True(runner.TryRecordIncident(new MonitoringIncidentRequest(
            MonitoringIncidentCategory.Freeze, "segundo", null), out _));
        Assert.False(runner.TryRecordIncident(new MonitoringIncidentRequest(
            MonitoringIncidentCategory.Other, "terceiro", null), out _));

        blocking.Release.TrySetResult();
        var session = await running;
        Assert.Equal(2, session.Incidents.Count);
        Assert.Equal(new[] { "primeiro", "segundo" }, session.Incidents.Select(item => item.Description));
    }

    [Fact]
    public async Task IncidentCannotBeRegisteredAfterTheSessionEnds()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            [new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())]);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromMilliseconds(300)));

        Assert.False(runner.TryRecordIncident(new MonitoringIncidentRequest(
            MonitoringIncidentCategory.Other, "tarde demais", null), out _));
        Assert.Empty(session.Incidents);
    }
}
