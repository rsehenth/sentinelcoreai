using System.Text.Json;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public static class HardwareSamples
{
    public static HardwareItem Item(string name = "Device", HardwareCategory category = HardwareCategory.Other,
        string kind = "pnp", string? stableId = null, string? driverVersion = null, string? firmwareVersion = null,
        string? state = "OK", DevicePresence presence = DevicePresence.Present,
        IReadOnlyList<HardwareProperty>? properties = null) =>
        new(category, kind, stableId ?? "sha256:" + Guid.NewGuid().ToString("N")[..24], name, "Vendor", "Model",
            null, driverVersion is null ? null : "Vendor", driverVersion, null, null, null, firmwareVersion, state,
            presence, properties ?? [], []);

    public static HardwareCollectorResult Result(HardwareCategory category, HardwareCollectionStatus status,
        params HardwareItem[] items) =>
        new(category.ToString().ToLowerInvariant(), "1.0.0", category, "Fake: tests only",
            new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 30, 12, 0, 1, TimeSpan.Zero), TimeSpan.FromSeconds(1),
            status, items, status == HardwareCollectionStatus.Completed ? [] : ["Limitação sintética."]);

    public static HardwareSnapshot Snapshot(params HardwareCollectorResult[] results)
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 2, TimeSpan.Zero);
        return new(1, Guid.NewGuid(), "0.2.0", "1", at.AddSeconds(-2), at, TimeSpan.FromSeconds(2),
            HardwareCollectionStatus.Completed, results);
    }
}

public sealed class FakeHardwareCollector(string id, HardwareCategory category,
    HardwareCollectionStatus status = HardwareCollectionStatus.Completed, bool throws = false, bool waits = false,
    HardwareCategory? reportedCategory = null, string? reportedId = null) : IHardwareCollector
{
    public string Id => id;
    public string Version => "1.0";
    public HardwareCategory Category => category;
    public string Source => "Fake: tests only";

    public async Task<HardwareCollectorResult> CollectAsync(Guid snapshotId, IHardwareCollectionSession session,
        CancellationToken cancellationToken)
    {
        if (waits) await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (throws) throw new InvalidOperationException("simulated hardware collector failure");
        var at = DateTimeOffset.UtcNow;
        return new(reportedId ?? Id, Version, reportedCategory ?? Category, Source, at, at, TimeSpan.Zero, status,
            [HardwareSamples.Item()], status == HardwareCollectionStatus.Completed ? [] : ["Falha sintética."]);
    }
}

public sealed class HardwareContractTests
{
    [Fact] public async Task AllCompletedIsCompleted()
    {
        var snapshot = await new HardwareRunner([new FakeHardwareCollector("cpu", HardwareCategory.Cpu),
            new FakeHardwareCollector("gpu", HardwareCategory.Gpu)]).RunAsync();
        Assert.Equal(HardwareCollectionStatus.Completed, snapshot.Status);
        Assert.Equal(2, snapshot.Results.Count);
    }

    [Fact] public async Task MixedOutcomeIsPartial()
    {
        var snapshot = await new HardwareRunner([new FakeHardwareCollector("cpu", HardwareCategory.Cpu),
            new FakeHardwareCollector("gpu", HardwareCategory.Gpu, throws: true)]).RunAsync();
        Assert.Equal(HardwareCollectionStatus.Partial, snapshot.Status);
        Assert.Contains(snapshot.Results, r => r.Status == HardwareCollectionStatus.Failed && r.Limitations.Count > 0);
    }

    [Fact] public async Task AllFailedIsFailed()
    {
        var snapshot = await new HardwareRunner([new FakeHardwareCollector("cpu", HardwareCategory.Cpu, throws: true)])
            .RunAsync();
        Assert.Equal(HardwareCollectionStatus.Failed, snapshot.Status);
    }

    [Fact] public async Task AllUnsupportedIsUnsupported()
    {
        var snapshot = await new HardwareRunner([new FakeHardwareCollector("monitor", HardwareCategory.Monitor,
            HardwareCollectionStatus.Unsupported)]).RunAsync();
        Assert.Equal(HardwareCollectionStatus.Unsupported, snapshot.Status);
    }

    [Fact] public async Task CancellationPreservesEveryCollector()
    {
        using var cancellation = new CancellationTokenSource();
        var running = new HardwareRunner([new FakeHardwareCollector("a", HardwareCategory.Other, waits: true),
            new FakeHardwareCollector("b", HardwareCategory.Other)]).RunAsync(cancellationToken: cancellation.Token);
        cancellation.Cancel();
        var snapshot = await running;
        Assert.Equal(HardwareCollectionStatus.Cancelled, snapshot.Status);
        Assert.Equal(2, snapshot.Results.Count);
        Assert.All(snapshot.Results, r => Assert.Equal(HardwareCollectionStatus.Cancelled, r.Status));
    }

    [Fact] public async Task CompletedWithLimitationsBecomesPartial()
    {
        var collector = new LimitingCollector();
        var snapshot = await new HardwareRunner([collector]).RunAsync();
        Assert.Equal(HardwareCollectionStatus.Partial, snapshot.Results[0].Status);
    }

    [Fact] public async Task CollectorWithWrongCategoryIsFailed()
    {
        var snapshot = await new HardwareRunner([new FakeHardwareCollector("cpu", HardwareCategory.Cpu,
            reportedCategory: HardwareCategory.Gpu)]).RunAsync();
        Assert.Equal(HardwareCollectionStatus.Failed, snapshot.Results[0].Status);
    }

    [Fact] public void EmptyOrDuplicateCollectorsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new HardwareRunner([]));
        Assert.Throws<ArgumentException>(() => new HardwareRunner([new FakeHardwareCollector("a", HardwareCategory.Other),
            new FakeHardwareCollector("a", HardwareCategory.Other)]));
    }

    [Fact] public void SessionBuildsSharedIndexOnce()
    {
        var session = new HardwareCollectionSession(); var calls = 0;
        var first = session.GetOrAdd("index", () => { calls++; return new object(); });
        var second = session.GetOrAdd("index", () => { calls++; return new object(); });
        Assert.Same(first, second); Assert.Equal(1, calls);
    }

    [Fact] public void JsonRoundTripPreservesNullsEnumsAndProperties()
    {
        var snapshot = HardwareSamples.Snapshot(HardwareSamples.Result(HardwareCategory.Memory,
            HardwareCollectionStatus.Partial, HardwareSamples.Item("Module", HardwareCategory.Memory, "memory-module",
                properties: [new("capacity_bytes", "8589934592")]) with { Manufacturer = null, DriverVersion = null }));
        var copy = JsonSerializer.Deserialize<HardwareSnapshot>(JsonSerializer.Serialize(snapshot, AuditJson.Options),
            AuditJson.Options)!;
        Assert.Equal(snapshot.Id, copy.Id);
        Assert.Equal(HardwareCollectionStatus.Partial, copy.Results[0].Status);
        Assert.Null(copy.Results[0].Items[0].Manufacturer);
        Assert.Equal("8589934592", copy.Results[0].Items[0].Property("capacity_bytes"));
    }

    private sealed class LimitingCollector : IHardwareCollector
    {
        public string Id => "cpu";
        public string Version => "1.0";
        public HardwareCategory Category => HardwareCategory.Cpu;
        public string Source => "Fake";
        public Task<HardwareCollectorResult> CollectAsync(Guid snapshotId, IHardwareCollectionSession session,
            CancellationToken cancellationToken)
        {
            var at = DateTimeOffset.UtcNow;
            return Task.FromResult(new HardwareCollectorResult(Id, Version, Category, Source, at, at, TimeSpan.Zero,
                HardwareCollectionStatus.Completed, [HardwareSamples.Item()], ["Clock indisponível."]));
        }
    }
}
