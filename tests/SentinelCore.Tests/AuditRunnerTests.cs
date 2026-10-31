using System.Text.Json;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class FakeCollector(string id, DiagnosticStatus status = DiagnosticStatus.Completed,
    bool throws = false, bool waits = false) : IDiagnosticCollector
{
    public string Id => id;
    public string Version => "1.0";
    public string Source => "Fake: tests only";
    public async Task<CollectorResult> CollectAsync(Guid auditId, CancellationToken cancellationToken)
    {
        if (waits) await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (throws) throw new InvalidOperationException("simulated collector failure");
        var at = DateTimeOffset.UtcNow;
        return new(auditId, Id, Version, at, at, TimeSpan.Zero, status, Source,
            new InventoryData(Memory: new MemoryInfo(null, null)), status == DiagnosticStatus.Partial ? ["InstalledBytes indisponível"] : []);
    }
}

public sealed class AuditRunnerTests
{
    [Fact] public async Task EveryMandatoryCollectorMustComplete()
    {
        var result = await new AuditRunner([new FakeCollector("ok"), new FakeCollector("bad", throws: true)]).RunBasicAsync();
        Assert.Equal(DiagnosticStatus.Partial, result.Status);
        Assert.Contains(result.Results, x => x.Status == DiagnosticStatus.Failed && x.Limitations.Count > 0);
    }
    [Fact] public async Task AllFailedIsFailed()
    {
        var result = await new AuditRunner([new FakeCollector("bad", throws: true)]).RunBasicAsync();
        Assert.Equal(DiagnosticStatus.Failed, result.Status);
    }
    [Fact] public async Task MissingInformationIsPartial()
    {
        var result = await new AuditRunner([new FakeCollector("missing", DiagnosticStatus.Partial)]).RunBasicAsync();
        Assert.Equal(DiagnosticStatus.Partial, result.Status);
        Assert.Null(result.Results[0].Data!.Memory!.InstalledBytes);
    }
    [Fact] public async Task CancelledBeforeStartPreservesEveryCollector()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await new AuditRunner([new FakeCollector("a"), new FakeCollector("b")]).RunBasicAsync(cancellationToken: cancellation.Token);
        Assert.Equal(DiagnosticStatus.Cancelled, result.Status);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, x => Assert.Equal(DiagnosticStatus.Cancelled, x.Status));
    }
    [Fact] public async Task CancellationDuringCollectionReturnsRecord()
    {
        using var cancellation = new CancellationTokenSource();
        var running = new AuditRunner([new FakeCollector("waiting", waits: true)]).RunBasicAsync(cancellationToken: cancellation.Token);
        cancellation.Cancel();
        var result = await running;
        Assert.Equal(DiagnosticStatus.Cancelled, result.Status);
    }
    [Fact] public async Task CompleteAuditHasConsistentIdsAndTimings()
    {
        var audit = await new AuditRunner([new FakeCollector("a"), new FakeCollector("b")]).RunBasicAsync();
        Assert.Equal(DiagnosticStatus.Completed, audit.Status);
        Assert.All(audit.Results, x => Assert.Equal(audit.Id, x.AuditId));
        Assert.True(audit.Duration >= TimeSpan.Zero);
    }
    [Fact] public void EmptyCollectorSetIsRejected() => Assert.Throws<ArgumentException>(() => new AuditRunner([]));
    [Fact] public void DuplicateCollectorsAreRejected() => Assert.Throws<ArgumentException>(() => new AuditRunner([new FakeCollector("a"), new FakeCollector("a")]));
    [Fact] public void JsonRoundTripRetainsNullAndEnums()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow);
        var copy = JsonSerializer.Deserialize<AuditRecord>(JsonSerializer.Serialize(audit, AuditJson.Options), AuditJson.Options)!;
        Assert.Equal(audit.Id, copy.Id);
        Assert.Equal(DiagnosticStatus.Completed, copy.Status);
        Assert.Null(copy.Results[0].Data!.Memory!.AvailableBytes);
    }
    [Fact] public void UnitsPreserveNull() { Assert.Null(Units.KiBToBytes(null)); Assert.Equal("Indisponível", Units.GiB(null)); }
    [Fact] public void KiBIsBinary() => Assert.Equal(1048576UL, Units.KiBToBytes(1024));
    [Fact] public void OverflowIsNotSilentlyConverted() => Assert.Throws<OverflowException>(() => Units.KiBToBytes(ulong.MaxValue));
}
