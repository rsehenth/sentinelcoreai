using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SelfOverheadIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InjectedMeterIsCreatedPerSessionAndDisposedOnSuccessFailureAndCancellation(bool network)
    {
        var meters = new List<FakeSelfOverheadMeter>();
        ISelfOverheadMeter Meter() { var meter = new FakeSelfOverheadMeter(); meters.Add(meter); return meter; }
        var clock = new SyntheticSessionClock();
        using var cancel = new CancellationTokenSource();
        if (network)
        {
            var runner = new NetworkSessionRunner([new SyntheticNetworkCollector()], clock: clock, selfMeterFactory: Meter);
            var success = await runner.RunAsync(NetworkSessionPreset.Snapshot);
            Assert.Equal(2.5, success.SelfOverhead.CpuPercentMean);
            Assert.Equal(4096UL, success.SelfOverhead.WorkingSetBytesMax);
            cancel.Cancel();
            Assert.Equal(NetworkSessionStatus.Cancelled, (await runner.RunAsync(NetworkSessionPreset.Snapshot, cancellationToken: cancel.Token)).Status);
            Assert.Equal(NetworkSessionStatus.Failed, (await new NetworkSessionRunner([new SyntheticNetworkCollector(true)],
                clock: clock, selfMeterFactory: Meter).RunAsync(NetworkSessionPreset.Snapshot)).Status);
        }
        else
        {
            var request = new PerformanceSessionRequest(PerformanceSessionPreset.Custom, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));
            var runner = new PerformanceSessionRunner([new SyntheticPerformanceCollector()], clock, selfMeterFactory: Meter);
            var success = await runner.RunAsync(request);
            Assert.Equal(4, success.Samples.Count);
            Assert.Equal(2.5, success.SelfOverhead.CpuPercentMean);
            Assert.Equal(8192UL, success.SelfOverhead.PrivateBytesMax);
            cancel.Cancel();
            Assert.Equal(PerformanceSessionStatus.Cancelled, (await runner.RunAsync(request, cancellationToken: cancel.Token)).Status);
            Assert.Equal(PerformanceSessionStatus.Failed, (await new PerformanceSessionRunner([new SyntheticPerformanceCollector(true)],
                clock, selfMeterFactory: Meter).RunAsync(request)).Status);
        }
        Assert.Equal(3, meters.Count);
        Assert.All(meters, meter => Assert.True(meter.Disposed));
        Assert.True(meters[0].Reads > 0);
        Assert.Equal(0, meters[1].Reads);
    }

    [Fact]
    public void ProcessSensorsAreConfinedToTheDefaultMeter()
    {
        foreach (var file in new[] { "PerformanceSessionRunner.cs", "NetworkSessionRunner.cs", "MonitoringSessionRunner.cs" })
        {
            var source = File.ReadAllText(Path.Combine(TestRepository.Root(), "src", "SentinelCore.Application", file));
            Assert.DoesNotContain("Process.GetCurrentProcess", source);
            Assert.DoesNotContain("TotalProcessorTime", source);
            Assert.DoesNotContain("WorkingSet64", source);
            Assert.DoesNotContain("Environment.ProcessorCount", source);
        }
    }
}
