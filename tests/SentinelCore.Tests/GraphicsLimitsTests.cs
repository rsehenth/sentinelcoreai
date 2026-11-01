using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// R8.1 — limites gráficos positivos. Valores inválidos devem ser rejeitados na construção, nunca produzir
/// truncamento silenciosamente incorreto.
/// </summary>
public sealed class GraphicsLimitsTests
{
    private static GraphicsLimits Limits(string name, int value) => name switch
    {
        nameof(GraphicsLimits.MaxAdapters) => new GraphicsLimits { MaxAdapters = value },
        nameof(GraphicsLimits.MaxMonitors) => new GraphicsLimits { MaxMonitors = value },
        nameof(GraphicsLimits.MaxDrivers) => new GraphicsLimits { MaxDrivers = value },
        nameof(GraphicsLimits.MaxEvents) => new GraphicsLimits { MaxEvents = value },
        nameof(GraphicsLimits.MaxWerReports) => new GraphicsLimits { MaxWerReports = value },
        nameof(GraphicsLimits.MaxMessageChars) => new GraphicsLimits { MaxMessageChars = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Limite gráfico desconhecido.")
    };

    [Fact]
    public void DefaultLimitsAreExplicitAndPositive()
    {
        var limits = GraphicsLimits.Default;
        Assert.Equal(32, limits.MaxAdapters);
        Assert.Equal(64, limits.MaxMonitors);
        Assert.Equal(128, limits.MaxDrivers);
        Assert.Equal(500, limits.MaxEvents);
        Assert.Equal(300, limits.MaxWerReports);
        Assert.Equal(1200, limits.MaxMessageChars);
    }

    [Theory]
    [InlineData(nameof(GraphicsLimits.MaxAdapters))]
    [InlineData(nameof(GraphicsLimits.MaxMonitors))]
    [InlineData(nameof(GraphicsLimits.MaxDrivers))]
    [InlineData(nameof(GraphicsLimits.MaxEvents))]
    [InlineData(nameof(GraphicsLimits.MaxWerReports))]
    [InlineData(nameof(GraphicsLimits.MaxMessageChars))]
    public void ZeroAndNegativeLimitsAreRejectedAtConstruction(string name)
    {
        foreach (var value in new[] { 0, -1, int.MinValue })
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new GraphicsInvestigationRunner(GraphicsSamples.RealisticHardware(),
                    GraphicsSamples.RealisticEvents(), limits: Limits(name, value)));
            Assert.Equal("limits", error.ParamName);
        }
    }

    [Fact]
    public void InvalidCombinationIsRejectedEvenWhenOtherLimitsAreValid()
    {
        var limits = new GraphicsLimits { MaxAdapters = 1, MaxEvents = 0, MaxMessageChars = 64 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsInvestigationRunner(GraphicsSamples.RealisticHardware(), GraphicsSamples.RealisticEvents(),
                limits: limits));
    }

    [Fact]
    public async Task MaximumAcceptedValuesDoNotChangeTheInvestigationOutcome()
    {
        var limits = new GraphicsLimits
        {
            MaxAdapters = int.MaxValue,
            MaxMonitors = int.MaxValue,
            MaxDrivers = int.MaxValue,
            MaxEvents = int.MaxValue,
            MaxWerReports = int.MaxValue,
            MaxMessageChars = int.MaxValue
        };

        var investigation = await GraphicsSamples.Runner(limits: limits)
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Equal(GraphicsInvestigationStatus.Completed, investigation.Status);
        Assert.Equal(GraphicsCoverageStatus.Complete, investigation.Coverage.Status);
        Assert.Single(investigation.Adapters);
        Assert.Single(investigation.Monitors);
    }

    [Fact]
    public async Task MinimumValidLimitOfOneIsAppliedWithoutSilentLoss()
    {
        var twoAdapters = GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ =>
            [GraphicsSamples.Gpu("gpu-a", "GPU A"), GraphicsSamples.Gpu("gpu-b", "GPU B")]);
        var investigation = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(gpu: twoAdapters),
                limits: new GraphicsLimits { MaxAdapters = 1 })
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Single(investigation.Adapters);
        Assert.True(investigation.Sources.Single(source =>
            source.Category == GraphicsSourceCategory.Adapters).LimitHit);
        Assert.Equal(GraphicsCoverageStatus.Partial, investigation.Coverage.Status);
    }
}
