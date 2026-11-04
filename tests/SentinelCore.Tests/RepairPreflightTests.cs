using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RepairPreflightTests
{
    [Fact]
    public async Task SfcAndDismAreSimulatableButRealExecutionIsAlwaysBlocked()
    {
        using var scope = new RepairScope();
        foreach (var id in new[]
                 {
                     RepairCatalog.SfcVerifyId, RepairCatalog.SfcRepairId,
                     RepairCatalog.DismCheckHealthId, RepairCatalog.DismScanHealthId,
                     RepairCatalog.DismRestoreHealthId, RepairCatalog.WinReInfoId
                 })
        {
            var plan = await scope.Center.PrepareAsync(id, null);

            Assert.True(plan.SimulationAllowed, id);
            Assert.False(plan.RealExecutionAllowed, id);
            Assert.Contains(RepairBlocker.RealExecutionDisabled, plan.Preflight.Blockers);
            Assert.Contains(RepairBlocker.ElevationBrokerUnavailable, plan.Preflight.Blockers);
            Assert.True(plan.Preflight.Tool.Available);
        }
    }

    [Fact]
    public async Task WindowsTenBlocksSimulation()
    {
        using var scope = new RepairScope();
        scope.Profile.Profile = scope.Profile.Profile with
        {
            ProductName = "Windows 10", Build = 19045, IsWindows11 = false
        };

        var plan = await scope.Center.PrepareAsync(RepairCatalog.SfcVerifyId, null);

        Assert.False(plan.SimulationAllowed);
        Assert.Contains(RepairBlocker.UnsupportedWindows, plan.Preflight.SimulationBlockers);
    }

    [Fact]
    public async Task ChkdskRequiresAValidatedLocalVolumeAndNtfsForScan()
    {
        using var scope = new RepairScope();

        var missing = await scope.Center.PrepareAsync(RepairCatalog.ChkdskScanId, null);
        var exfat = await scope.Center.PrepareAsync(RepairCatalog.ChkdskScanId, "E:");
        var network = await scope.Center.PrepareAsync(RepairCatalog.ChkdskScanId, "N:");
        var unknown = await scope.Center.PrepareAsync(RepairCatalog.ChkdskScanId, "Q:");
        var valid = await scope.Center.PrepareAsync(RepairCatalog.ChkdskScanId, "C:");

        Assert.False(missing.SimulationAllowed);
        Assert.Contains(RepairBlocker.VolumeRequired, missing.Preflight.SimulationBlockers);
        Assert.False(exfat.SimulationAllowed);
        Assert.Contains(RepairBlocker.IncompatibleFileSystem, exfat.Preflight.SimulationBlockers);
        Assert.False(network.SimulationAllowed);
        Assert.Contains(RepairBlocker.VolumeNotLocal, network.Preflight.SimulationBlockers);
        Assert.False(unknown.SimulationAllowed);
        Assert.Contains(RepairBlocker.VolumeUnavailable, unknown.Preflight.SimulationBlockers);
        Assert.True(valid.SimulationAllowed);
        Assert.Equal(["C:", "/scan"], valid.Invocation.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData("C")]
    [InlineData("C:\\")]
    [InlineData("C:; calc")]
    [InlineData(@"\\server\share")]
    [InlineData("C: /scan")]
    public async Task VolumeTextIsNeverAcceptedAsAnArgument(string hostile)
    {
        using var scope = new RepairScope();

        var plan = await scope.Center.PrepareAsync(RepairCatalog.ChkdskScanId, hostile);

        Assert.False(plan.SimulationAllowed);
        Assert.Contains(RepairBlocker.VolumeRequired, plan.Preflight.SimulationBlockers);
        Assert.DoesNotContain(hostile, plan.Invocation.Arguments);
    }

    [Fact]
    public async Task OperationThatDoesNotAcceptVolumeRejectsAnExtraVolume()
    {
        using var scope = new RepairScope();

        var plan = await scope.Center.PrepareAsync(RepairCatalog.SfcVerifyId, "C:");

        Assert.False(plan.SimulationAllowed);
        Assert.Contains(RepairBlocker.VolumeUnavailable, plan.Preflight.SimulationBlockers);
        Assert.Equal(["/verifyonly"], plan.Invocation.Arguments);
    }

    [Fact]
    public async Task MissingOfficialToolBlocksSimulationAndRealExecution()
    {
        using var scope = new RepairScope();
        scope.Tools.Locations[RepairTool.Dism] = RepairToolLocation.Missing(RepairTool.Dism,
            "dism.exe não encontrado em System32; PATH não é consultado.");

        var plan = await scope.Center.PrepareAsync(RepairCatalog.DismCheckHealthId, null);

        Assert.False(plan.SimulationAllowed);
        Assert.Contains(RepairBlocker.ToolNotFound, plan.Preflight.SimulationBlockers);
        Assert.Contains(RepairBlocker.ToolNotFound, plan.Preflight.Blockers);
    }
}
