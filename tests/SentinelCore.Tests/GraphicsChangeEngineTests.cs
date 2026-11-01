using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// R8.2 — identidades duplicadas e comparabilidade entre investigações. A comparação é conservadora: leitura
/// incompleta, anterior ou atual, nunca vira adição/remoção de dispositivo, e identidade duplicada é erro
/// explícito nas duas leituras.
/// </summary>
public sealed class GraphicsChangeEngineTests
{
    private static Task<GraphicsInvestigation> BaselineAsync() =>
        GraphicsSamples.Runner().RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

    private static GraphicsInvestigation WithSourceStatus(GraphicsInvestigation investigation,
        GraphicsSourceCategory category, GraphicsSourceStatus status) =>
        investigation with
        {
            Sources = investigation.Sources
                .Select(source => source.Category == category ? source with { Status = status } : source)
                .ToArray()
        };

    [Fact]
    public async Task MissingPreviousInvestigationIsNeverComparable()
    {
        var current = await BaselineAsync();

        var result = GraphicsChangeEngine.Compare(null, current.Adapters, current.Monitors, current.Drivers, true,
            true, true);

        Assert.False(result.Comparable);
        Assert.False(result.AdaptersComparable);
        Assert.False(result.MonitorsComparable);
        Assert.False(result.DriversComparable);
        Assert.Empty(result.Changes);
        Assert.Contains(result.Limitations, limitation =>
            limitation.Contains("anterior", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DuplicateAdapterIdentityInCurrentReadingIsRejected()
    {
        var previous = await BaselineAsync();
        var adapter = previous.Adapters[0];

        var error = Assert.Throws<InvalidDataException>(() => GraphicsChangeEngine.Compare(previous,
            [adapter, adapter], previous.Monitors, previous.Drivers, true, true, true));
        Assert.Contains("duplicada", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateMonitorIdentityInCurrentReadingIsRejected()
    {
        var previous = await BaselineAsync();
        var monitor = previous.Monitors[0];

        Assert.Throws<InvalidDataException>(() => GraphicsChangeEngine.Compare(previous, previous.Adapters,
            [monitor, monitor], previous.Drivers, true, true, true));
    }

    [Fact]
    public async Task DuplicateDriverIdentityInCurrentReadingIsRejected()
    {
        var previous = await BaselineAsync();
        var driver = previous.Drivers[0];

        Assert.Throws<InvalidDataException>(() => GraphicsChangeEngine.Compare(previous, previous.Adapters,
            previous.Monitors, [driver, driver], true, true, true));
    }

    [Fact]
    public async Task DuplicateAdapterIdentityInPreviousReadingIsRejected()
    {
        var previous = await BaselineAsync();
        var adapter = previous.Adapters[0];
        var tampered = previous with { Adapters = [adapter, adapter] };

        Assert.Throws<InvalidDataException>(() => GraphicsChangeEngine.Compare(tampered, previous.Adapters,
            previous.Monitors, previous.Drivers, true, true, true));
    }

    [Fact]
    public async Task DuplicateMonitorIdentityInPreviousReadingIsRejected()
    {
        var previous = await BaselineAsync();
        var monitor = previous.Monitors[0];
        var tampered = previous with { Monitors = [monitor, monitor] };

        Assert.Throws<InvalidDataException>(() => GraphicsChangeEngine.Compare(tampered, previous.Adapters,
            previous.Monitors, previous.Drivers, true, true, true));
    }

    [Fact]
    public async Task DuplicateDriverIdentityInPreviousReadingIsRejected()
    {
        var previous = await BaselineAsync();
        var driver = previous.Drivers[0];
        var tampered = previous with { Drivers = [driver, driver] };

        Assert.Throws<InvalidDataException>(() => GraphicsChangeEngine.Compare(tampered, previous.Adapters,
            previous.Monitors, previous.Drivers, true, true, true));
    }

    [Fact]
    public async Task PreviousAdapterCoverageGapSuppressesPresenceComparison()
    {
        var previous = await BaselineAsync();
        var current = await GraphicsSamples.Runner(hardware: GraphicsSamples.RealisticHardware(
                gpu: GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu,
                    _ => [GraphicsSamples.Gpu(), GraphicsSamples.Gpu("gpu-b", "GPU B")])))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours), previous);
        var partialPrevious = WithSourceStatus(previous, GraphicsSourceCategory.Adapters,
            GraphicsSourceStatus.Partial);

        var result = GraphicsChangeEngine.Compare(partialPrevious, current.Adapters, current.Monitors,
            current.Drivers, true, true, true);

        Assert.False(result.AdaptersComparable);
        Assert.True(result.MonitorsComparable);
        Assert.DoesNotContain(result.Changes, change =>
            change.Type is GraphicsChangeType.AdapterAdded or GraphicsChangeType.AdapterRemoved);
        Assert.Contains(result.Limitations, limitation =>
            limitation.Contains("Adaptadores", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviousAdapterUnavailableFlagSuppressesPresenceComparison()
    {
        var previous = await BaselineAsync();
        var extra = previous.Adapters[0] with { StableId = "gpu-extra", Name = "GPU extra" };
        var unavailable = previous with
        {
            Coverage = previous.Coverage with { AdaptersUnavailable = true }
        };

        var result = GraphicsChangeEngine.Compare(unavailable, [.. previous.Adapters, extra],
            previous.Monitors, previous.Drivers, true, true, true);

        Assert.False(result.AdaptersComparable);
        Assert.DoesNotContain(result.Changes, change => change.Type == GraphicsChangeType.AdapterAdded);
    }

    [Fact]
    public async Task CurrentAdapterCoverageGapSuppressesPresenceComparison()
    {
        var previous = await BaselineAsync();
        var extra = previous.Adapters[0] with { StableId = "gpu-extra", Name = "GPU extra" };

        var result = GraphicsChangeEngine.Compare(previous, [.. previous.Adapters, extra], previous.Monitors,
            previous.Drivers, false, true, true);

        Assert.False(result.AdaptersComparable);
        Assert.DoesNotContain(result.Changes, change =>
            change.Type is GraphicsChangeType.AdapterAdded or GraphicsChangeType.AdapterRemoved);
        Assert.Contains(result.Limitations, limitation =>
            limitation.Contains("leitura anterior ou atual", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviousMonitorCoverageGapSuppressesPresenceComparison()
    {
        var previous = await BaselineAsync();
        var extra = previous.Monitors[0] with { StableId = "monitor-extra", Name = "Monitor extra" };
        var partialPrevious = WithSourceStatus(previous, GraphicsSourceCategory.Monitors,
            GraphicsSourceStatus.Partial);

        var result = GraphicsChangeEngine.Compare(partialPrevious, previous.Adapters,
            [.. previous.Monitors, extra], previous.Drivers, true, true, true);

        Assert.False(result.MonitorsComparable);
        Assert.DoesNotContain(result.Changes, change => change.Type == GraphicsChangeType.MonitorAdded);
    }

    [Fact]
    public async Task CurrentMonitorCoverageGapSuppressesPresenceComparison()
    {
        var previous = await BaselineAsync();
        var extra = previous.Monitors[0] with { StableId = "monitor-extra", Name = "Monitor extra" };

        var result = GraphicsChangeEngine.Compare(previous, previous.Adapters,
            [.. previous.Monitors, extra], previous.Drivers, true, false, true);

        Assert.False(result.MonitorsComparable);
        Assert.DoesNotContain(result.Changes, change =>
            change.Type is GraphicsChangeType.MonitorAdded or GraphicsChangeType.MonitorRemoved);
    }

    [Fact]
    public async Task PreviousDriverCoverageGapSuppressesDriverChanges()
    {
        var previous = await BaselineAsync();
        var changedDrivers = previous.Drivers
            .Select(driver => driver with { Version = "99.9.9.9" })
            .ToArray();
        var partialPrevious = WithSourceStatus(previous, GraphicsSourceCategory.Drivers,
            GraphicsSourceStatus.Partial);

        var result = GraphicsChangeEngine.Compare(partialPrevious, previous.Adapters, previous.Monitors,
            changedDrivers, true, true, true);

        Assert.False(result.DriversComparable);
        Assert.DoesNotContain(result.Changes, change => change.Type == GraphicsChangeType.DriverChanged);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("Drivers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CurrentDriverCoverageGapSuppressesDriverChanges()
    {
        var previous = await BaselineAsync();
        var changedDrivers = previous.Drivers
            .Select(driver => driver with { Version = "99.9.9.9" })
            .ToArray();

        var result = GraphicsChangeEngine.Compare(previous, previous.Adapters, previous.Monitors, changedDrivers,
            true, true, false);

        Assert.False(result.DriversComparable);
        Assert.DoesNotContain(result.Changes, change => change.Type == GraphicsChangeType.DriverChanged);
    }
}
