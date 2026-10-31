using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class HardwareDiffTests
{
    private static HardwareSnapshot Snapshot(HardwareCollectorResult result) => HardwareSamples.Snapshot(result);

    [Fact] public void AddedDeviceIsReportedOnlyWhenBothCategoriesCompleted()
    {
        var before = Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Completed));
        var added = HardwareSamples.Item("New GPU", HardwareCategory.Gpu, "gpu", stableId: "sha256:gpu2");
        var after = Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Completed, added));
        var diff = HardwareDiffEngine.Compare(before, after);
        var change = Assert.Single(diff.Changes);
        Assert.Equal(HardwareChangeType.Added, change.Type);
        Assert.Equal("sha256:gpu2", change.StableId);
    }

    [Fact] public void RemovedDeviceIsSuppressedWhenCategoryFailed()
    {
        var existing = HardwareSamples.Item("GPU", HardwareCategory.Gpu, "gpu", stableId: "sha256:gpu1");
        var before = Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Completed, existing));
        var after = Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Failed));
        var diff = HardwareDiffEngine.Compare(before, after);
        Assert.Empty(diff.Changes);
        Assert.Contains(HardwareCategory.Gpu, diff.NotComparable);
        Assert.NotEmpty(diff.Limitations);
    }

    [Theory] [InlineData(HardwareCollectionStatus.Partial)] [InlineData(HardwareCollectionStatus.Unsupported)]
    [InlineData(HardwareCollectionStatus.Cancelled)]
    public void RemovedDeviceIsSuppressedWhenCategoryIncomplete(HardwareCollectionStatus status)
    {
        var existing = HardwareSamples.Item("GPU", HardwareCategory.Gpu, "gpu", stableId: "sha256:gpu1");
        var before = Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Completed, existing));
        var after = Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, status));
        var diff = HardwareDiffEngine.Compare(before, after);
        Assert.DoesNotContain(diff.Changes, c => c.Type == HardwareChangeType.Removed);
        Assert.Contains(HardwareCategory.Gpu, diff.NotComparable);
    }

    [Fact] public void MissingCategoryIsNotInterpretedAsRemoval()
    {
        var existing = HardwareSamples.Item("Monitor", HardwareCategory.Monitor, "monitor", stableId: "sha256:mon1");
        var before = Snapshot(HardwareSamples.Result(HardwareCategory.Monitor, HardwareCollectionStatus.Completed, existing));
        var after = Snapshot(HardwareSamples.Result(HardwareCategory.Cpu, HardwareCollectionStatus.Completed));
        var diff = HardwareDiffEngine.Compare(before, after);
        Assert.DoesNotContain(diff.Changes, c => c.Type == HardwareChangeType.Removed);
        Assert.Contains(HardwareCategory.Monitor, diff.NotComparable);
    }

    [Fact] public void SameStableIdWithDifferentNameIsConfigurationChange()
    {
        var beforeItem = HardwareSamples.Item("Old name", HardwareCategory.Network, "nic", stableId: "sha256:nic1");
        var afterItem = HardwareSamples.Item("New name", HardwareCategory.Network, "nic", stableId: "sha256:nic1");
        var diff = HardwareDiffEngine.Compare(
            Snapshot(HardwareSamples.Result(HardwareCategory.Network, HardwareCollectionStatus.Completed, beforeItem)),
            Snapshot(HardwareSamples.Result(HardwareCategory.Network, HardwareCollectionStatus.Completed, afterItem)));
        Assert.DoesNotContain(diff.Changes, c => c.Type is HardwareChangeType.Added or HardwareChangeType.Removed);
        var change = Assert.Single(diff.Changes);
        Assert.Equal(HardwareChangeType.ConfigurationChanged, change.Type);
        Assert.Equal("name", change.Field);
    }

    [Fact] public void DriverFirmwareCapacityAndConfigChangesAreDistinguished()
    {
        var beforeItem = HardwareSamples.Item("Disk", HardwareCategory.Storage, "disk", stableId: "sha256:d1",
            driverVersion: "1.0", firmwareVersion: "FW1",
            properties: [new("capacity_bytes", "1000"), new("interface", "NVMe")]);
        var afterItem = beforeItem with
        {
            DriverVersion = "2.0", FirmwareVersion = "FW2",
            Properties = [new("capacity_bytes", "2000"), new("interface", "SATA")]
        };
        var diff = HardwareDiffEngine.Compare(
            Snapshot(HardwareSamples.Result(HardwareCategory.Storage, HardwareCollectionStatus.Completed, beforeItem)),
            Snapshot(HardwareSamples.Result(HardwareCategory.Storage, HardwareCollectionStatus.Completed, afterItem)));
        Assert.Contains(diff.Changes, c => c.Type == HardwareChangeType.DriverChanged && c.Field == "driver_version");
        Assert.Contains(diff.Changes, c => c.Type == HardwareChangeType.FirmwareChanged);
        Assert.Contains(diff.Changes, c => c.Type == HardwareChangeType.CapacityChanged && c.Field == "capacity_bytes");
        Assert.Contains(diff.Changes, c => c.Type == HardwareChangeType.ConfigurationChanged && c.Field == "interface");
    }

    [Fact] public void VolatilePropertiesAreIgnored()
    {
        var beforeItem = HardwareSamples.Item("Disk", HardwareCategory.Storage, "disk", stableId: "sha256:d1",
            properties: [new("free_bytes", "100", Volatile: true)]);
        var afterItem = beforeItem with { Properties = [new("free_bytes", "200", Volatile: true)] };
        var diff = HardwareDiffEngine.Compare(
            Snapshot(HardwareSamples.Result(HardwareCategory.Storage, HardwareCollectionStatus.Completed, beforeItem)),
            Snapshot(HardwareSamples.Result(HardwareCategory.Storage, HardwareCollectionStatus.Completed, afterItem)));
        Assert.Empty(diff.Changes);
    }

    [Fact] public void PartialCategoryStillComparesPresentItemsButNeverRemoves()
    {
        var beforeItem = HardwareSamples.Item("GPU", HardwareCategory.Gpu, "gpu", stableId: "sha256:g1",
            driverVersion: "1.0");
        var afterItem = beforeItem with { DriverVersion = "2.0" };
        var diff = HardwareDiffEngine.Compare(
            Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Completed, beforeItem)),
            Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Partial, afterItem)));
        Assert.Contains(diff.Changes, c => c.Type == HardwareChangeType.DriverChanged);
        Assert.DoesNotContain(diff.Changes, c => c.Type == HardwareChangeType.Removed);
    }

    [Fact] public void NullToKnownValueIsAChangeNotZero()
    {
        var beforeItem = HardwareSamples.Item("GPU", HardwareCategory.Gpu, "gpu", stableId: "sha256:g1",
            driverVersion: null);
        var afterItem = beforeItem with { DriverVersion = "31.0" };
        var diff = HardwareDiffEngine.Compare(
            Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Completed, beforeItem)),
            Snapshot(HardwareSamples.Result(HardwareCategory.Gpu, HardwareCollectionStatus.Completed, afterItem)));
        var change = Assert.Single(diff.Changes);
        Assert.Equal(HardwareChangeType.DriverChanged, change.Type);
        Assert.Null(change.Before);
        Assert.Equal("31.0", change.After);
    }
}
