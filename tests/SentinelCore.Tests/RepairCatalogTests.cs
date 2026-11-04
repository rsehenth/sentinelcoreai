using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RepairCatalogTests
{
    [Fact]
    public void CatalogExposesOnlyTheClosedSetOfOfficialToolsAndFixedArguments()
    {
        var catalog = RepairCatalog.CreateDefault();

        Assert.Equal("r1-windows-repair-1", catalog.Version);
        Assert.Equal(8, catalog.All.Count);
        Assert.Equal(8, RepairCatalog.AvailableIds.Count);
        Assert.Equal(9, catalog.DocumentedOnly.Count);
        Assert.All(catalog.All, operation =>
        {
            Assert.False(operation.IsDocumentedOnly);
            Assert.Equal(RepairAvailability.Available, operation.Availability);
            Assert.True(operation.Risk is RiskLevel.Low or RiskLevel.Medium);
            Assert.Contains(operation.Tool, new[]
            {
                RepairTool.Sfc, RepairTool.Dism, RepairTool.Chkdsk, RepairTool.Reagentc
            });
            Assert.All(operation.FixedArguments, argument =>
                Assert.True(RepairArgumentPolicy.IsAllowed(operation.Tool, argument)));
        });
        Assert.All(catalog.DocumentedOnly, operation =>
        {
            Assert.True(operation.IsDocumentedOnly);
            Assert.Equal(RepairAvailability.DocumentedOnly, operation.Availability);
            Assert.Equal(RiskLevel.High, operation.Risk);
            Assert.False(catalog.TryGet(operation.Id, out _));
        });
    }

    [Theory]
    [InlineData(RepairCatalog.SfcVerifyId, "sfc.exe /verifyonly")]
    [InlineData(RepairCatalog.SfcRepairId, "sfc.exe /scannow")]
    [InlineData(RepairCatalog.DismCheckHealthId, "dism.exe /Online /Cleanup-Image /CheckHealth")]
    [InlineData(RepairCatalog.DismScanHealthId, "dism.exe /Online /Cleanup-Image /ScanHealth")]
    [InlineData(RepairCatalog.DismRestoreHealthId, "dism.exe /Online /Cleanup-Image /RestoreHealth")]
    [InlineData(RepairCatalog.ChkdskCheckId, "chkdsk.exe <volume>")]
    [InlineData(RepairCatalog.ChkdskScanId, "chkdsk.exe <volume> /scan")]
    [InlineData(RepairCatalog.WinReInfoId, "reagentc.exe /info")]
    public void OfficialCommandsAreStableAndContainNoFreeFormArguments(string id, string expected)
    {
        var operation = RepairCatalog.CreateDefault().Get(id);

        Assert.Equal(expected, operation.DisplayCommand);
    }

    [Theory]
    [InlineData(RepairTool.Sfc, "/f")]
    [InlineData(RepairTool.Sfc, "/scannow /f")]
    [InlineData(RepairTool.Dism, "/RestoreHealth /Source:C:\\Image")]
    [InlineData(RepairTool.Chkdsk, @"C:\Windows")]
    [InlineData(RepairTool.Chkdsk, "C:; calc")]
    [InlineData(RepairTool.Chkdsk, "& powershell")]
    [InlineData(RepairTool.Chkdsk, "cmd.exe")]
    [InlineData(RepairTool.Reagentc, "/setreimage")]
    public void ArgumentPolicyRejectsAnythingOutsideTheOfficialAllowlist(RepairTool tool, string argument) =>
        Assert.False(RepairArgumentPolicy.IsAllowed(tool, argument));

    [Fact]
    public void CatalogRejectsAvailableHighRiskDocumentedLowRiskAndMalformedVolumeOperations()
    {
        var valid = RepairCatalog.CreateDefault().Get(RepairCatalog.SfcVerifyId);
        var highRisk = valid with { Id = "SC-RP-TEST-01", Risk = RiskLevel.High };
        var lowRiskDocumented = (RepairCatalog.CreateDefault().DocumentedOnly[0]) with
        {
            Id = "SC-RP-TEST-02", Risk = RiskLevel.Low
        };
        var chkdskWithoutVolume = RepairCatalog.CreateDefault().Get(RepairCatalog.ChkdskScanId) with
        {
            Id = "SC-RP-TEST-03", FixedArguments = ["/scan"], RequiresVolume = false
        };
        var sfcWithVolume = valid with
        {
            Id = "SC-RP-TEST-04", FixedArguments = ["{volume}", "/verifyonly"], RequiresVolume = true
        };
        var arbitrary = valid with { Id = "SC-RP-TEST-05", FixedArguments = ["/source:C:\\x"] };
        var advancedAvailable = RepairCatalog.CreateDefault().Get(RepairCatalog.ChkdskScanId) with
        {
            Id = "SC-RP-TEST-06", FixedArguments = ["{volume}", "/f"]
        };
        var winReAvailable = RepairCatalog.CreateDefault().Get(RepairCatalog.WinReInfoId) with
        {
            Id = "SC-RP-TEST-07", FixedArguments = ["/enable"]
        };

        Assert.Throws<InvalidDataException>(() => new RepairCatalog("test", [valid, highRisk], []));
        Assert.Throws<InvalidDataException>(() => new RepairCatalog("test", [valid], [lowRiskDocumented]));
        Assert.Throws<InvalidDataException>(() => new RepairCatalog("test", [chkdskWithoutVolume], []));
        Assert.Throws<InvalidDataException>(() => new RepairCatalog("test", [sfcWithVolume], []));
        Assert.Throws<InvalidDataException>(() => new RepairCatalog("test", [arbitrary], []));
        Assert.Throws<InvalidDataException>(() => new RepairCatalog("test", [advancedAvailable], []));
        Assert.Throws<InvalidDataException>(() => new RepairCatalog("test", [winReAvailable], []));
    }

    [Fact]
    public void ContentHashIsDeterministicAndChangesWithTheCatalog()
    {
        var first = RepairCatalog.CreateDefault();
        var second = RepairCatalog.CreateDefault();
        var changed = new RepairCatalog(first.Version,
            first.All.Select(item => item.Id == RepairCatalog.SfcVerifyId
                ? item with { DisplayName = item.DisplayName + " alterado" }
                : item),
            first.DocumentedOnly);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.NotEqual(first.ContentHash, changed.ContentHash);
    }
}
