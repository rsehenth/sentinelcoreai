using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2bCatalogTests
{
    [Fact]
    public void CatalogIsTheSingleExpandedClosedCatalog()
    {
        var catalog = T2bCatalog.Create();

        Assert.Equal(T2bCatalog.CatalogVersion, catalog.Version);
        Assert.Equal(9, catalog.All.Count);
        Assert.Equal(T2bCatalog.OperationIds.OrderBy(item => item, StringComparer.Ordinal),
            catalog.All.Select(item => item.Id).OrderBy(item => item, StringComparer.Ordinal));
        Assert.All(T2aCatalog.OperationIds, id => Assert.True(catalog.TryGet(id, out _)));
        Assert.All(catalog.All, operation =>
        {
            Assert.Equal(OperationScope.CurrentUser, operation.Scope);
            Assert.Equal(RiskLevel.Low, operation.Risk);
            Assert.Equal(OperationAvailability.Enabled, operation.Availability);
            Assert.True(operation.Protection.RequiresBackup);
            Assert.False(operation.Protection.RequiresFullTarget);
            Assert.False(operation.Protection.RequiresRestorePoint);
            Assert.Equal(ChangeKind.SetValue, operation.Kind);
            Assert.Equal("22000", operation.Compatibility.MinimumBuild);
            Assert.True(operation.Compatibility.Windows11Only);
            Assert.All(operation.Targets, target =>
            {
                Assert.Equal(OperationTargetRoots.CurrentUser, target.Root);
                Assert.True(OperationTargetRoots.IsWellFormedCurrentUserTarget(target));
            });
        });
    }

    [Fact]
    public void NewOperationsDeclareTheExactSingleHkcuValue()
    {
        var catalog = T2bCatalog.Create();
        var hidden = catalog.Get(T2bCatalog.ShowHiddenFilesId);
        var taskView = catalog.Get(T2bCatalog.HideTaskViewButtonId);
        var search = catalog.Get(T2bCatalog.SearchFullBoxId);

        Assert.Equal((T2aCatalog.ExplorerAdvancedPath, T2bCatalog.HiddenValue),
            (hidden.Targets.Single().Path, hidden.Targets.Single().ValueName));
        Assert.Equal("1", hidden.Changes.Single().DesiredValue.Data);
        Assert.Equal((T2aCatalog.ExplorerAdvancedPath, T2bCatalog.ShowTaskViewButtonValue),
            (taskView.Targets.Single().Path, taskView.Targets.Single().ValueName));
        Assert.Equal("0", taskView.Changes.Single().DesiredValue.Data);
        Assert.Equal((T2bCatalog.SearchPath, T2bCatalog.SearchboxTaskbarModeValue),
            (search.Targets.Single().Path, search.Targets.Single().ValueName));
        Assert.Equal(RegistryValueKinds.DWord, search.Changes.Single().DesiredValue.Kind);
        Assert.Equal("Explorador de Arquivos", hidden.Category);
        Assert.Equal("Barra de tarefas", taskView.Category);
        Assert.Equal("Barra de tarefas", search.Category);
    }

    [Fact]
    public void SearchModesMapEachCataloguedValueToOneOperation()
    {
        Assert.Equal([0, 1, 2, 3], T2bCatalog.SearchModes.Select(item => item.Mode));
        Assert.Equal(4, T2bCatalog.SearchModes.Select(item => item.OperationId).Distinct().Count());
        Assert.Equal(T2bCatalog.HideTaskbarSearchId, T2bCatalog.OperationIdForSearchMode(0));
        Assert.Equal(T2bCatalog.SearchIconOnlyId, T2bCatalog.OperationIdForSearchMode(1));
        Assert.Equal(T2bCatalog.SearchFullBoxId, T2bCatalog.OperationIdForSearchMode(2));
        Assert.Equal(T2bCatalog.SearchIconAndLabelId, T2bCatalog.OperationIdForSearchMode(3));

        var catalog = T2bCatalog.Create();
        foreach (var mode in T2bCatalog.SearchModes)
        {
            var operation = catalog.Get(mode.OperationId);
            Assert.Equal(mode.Mode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                operation.Changes.Single().DesiredValue.Data);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void UnknownSearchModeIsRejected(int mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => T2bCatalog.OperationIdForSearchMode(mode));
        Assert.Null(T2bCatalog.FindSearchMode(mode));
    }

    [Fact]
    public void AdapterAllowlistMatchesTheExpandedClosedCatalogExactly()
    {
        var catalogTargets = T2bCatalog.Operations()
            .SelectMany(item => item.Targets)
            .Select(item => item.Identity)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        var adapterTargets = HkcuTargetPolicy.AllowedTargets
            .Select(item => item.Identity)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(7, adapterTargets.Count);
        Assert.Equal(catalogTargets, adapterTargets);
    }

    [Fact]
    public async Task UnknownOperationIsRejectedBeforeAnyRead()
    {
        using var scope = new T2bScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Engine.SimulateAsync("SC-TW-NAO-EXISTE"));
        Assert.Equal(0, scope.Access.Reads);
    }

    [Fact]
    public async Task HkcuTargetOutsideTheExpandedAllowlistIsBlockedBeforeRead()
    {
        var operation = T2bCatalog.Create().Get(T2bCatalog.SearchFullBoxId);
        var rogue = new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.ExplorerAdvancedPath,
            "TaskbarMn");
        var changed = operation with
        {
            Targets = [rogue],
            Changes = [new PlannedTarget(rogue, new TargetValueState(true, RegistryValueKinds.DWord, "0"))]
        };
        using var scope = new T2bScope(catalog: new OperationCatalog("t2b-rogue", [changed]));

        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.SearchFullBoxId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.TargetOutsideCatalog, simulation.Blockers);
        Assert.Equal(0, scope.Access.Reads);
    }

    [Fact]
    public void CatalogRejectsKeyOnlyAndMachineRootTargets()
    {
        var operation = T2bCatalog.Create().Get(T2bCatalog.SearchFullBoxId);
        var keyOnly = operation with
        {
            Targets = [new OperationTarget(OperationTargetRoots.CurrentUser, T2bCatalog.SearchPath, null)],
            Changes = [new PlannedTarget(
                new OperationTarget(OperationTargetRoots.CurrentUser, T2bCatalog.SearchPath, null),
                new TargetValueState(false, RegistryValueKinds.DWord, null))]
        };
        var machine = operation with
        {
            Targets = [new OperationTarget("HKLM", T2bCatalog.SearchPath, T2bCatalog.SearchboxTaskbarModeValue)],
            Changes = [new PlannedTarget(
                new OperationTarget("HKLM", T2bCatalog.SearchPath, T2bCatalog.SearchboxTaskbarModeValue),
                new TargetValueState(true, RegistryValueKinds.DWord, "0"))]
        };

        Assert.Throws<InvalidDataException>(() => new OperationCatalog("t2b-key", [keyOnly]));
        Assert.Throws<InvalidDataException>(() => new OperationCatalog("t2b-machine", [machine]));
    }

    [Fact]
    public async Task Windows10BuildBlocksEveryNewOperationBeforeWrite()
    {
        using var scope = new T2bScope(build: 19045);
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        scope.Access.SeedDWord(scope.SearchTarget(), 2);

        var hidden = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);
        var search = await scope.Engine.SimulateAsync(T2bCatalog.HideTaskbarSearchId);

        Assert.False(hidden.CanExecute);
        Assert.False(search.CanExecute);
        Assert.Contains(BlockerCode.UnsupportedWindows, hidden.Blockers);
        Assert.Contains(BlockerCode.UnsupportedWindows, search.Blockers);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task HighRiskVariantOfANewOperationIsBlockedByTheBackend()
    {
        var operation = T2bCatalog.Create().Get(T2bCatalog.ShowHiddenFilesId) with
        {
            Id = "SC-TW-EXP-96",
            Risk = RiskLevel.High
        };
        using var scope = new T2bScope(catalog: new OperationCatalog("t2b-high", [operation]));
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);

        var simulation = await scope.Engine.SimulateAsync("SC-TW-EXP-96");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.RiskBlocked, simulation.Blockers);
        Assert.Throws<InvalidOperationException>(() => scope.Engine.Authorize(simulation, "autorizo"));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public void CatalogContentHashIsStableAcrossRebuilds()
    {
        Assert.Equal(T2bCatalog.Create().ContentHash, T2bCatalog.Create().ContentHash);
        Assert.NotEqual(T2aCatalog.Create().ContentHash, T2bCatalog.Create().ContentHash);
    }

    [Fact]
    public void AuthorizedHkcuTargetsArePinnedToSevenLiteralIdentities()
    {
        var expected = new[]
        {
            new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.ExplorerAdvancedPath,
                T2aCatalog.HideFileExtValue).Identity,
            new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.ExplorerAdvancedPath,
                T2aCatalog.TaskbarAlValue).Identity,
            new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.PersonalizePath,
                T2aCatalog.AppsUseLightThemeValue).Identity,
            new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.PersonalizePath,
                T2aCatalog.SystemUsesLightThemeValue).Identity,
            new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.ExplorerAdvancedPath,
                T2bCatalog.HiddenValue).Identity,
            new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.ExplorerAdvancedPath,
                T2bCatalog.ShowTaskViewButtonValue).Identity,
            new OperationTarget(OperationTargetRoots.CurrentUser, T2bCatalog.SearchPath,
                T2bCatalog.SearchboxTaskbarModeValue).Identity
        }.OrderBy(item => item, StringComparer.Ordinal).ToList();
        var actual = T2bCatalog.Operations()
            .SelectMany(item => item.Targets)
            .Select(item => item.Identity)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, actual);
        Assert.Equal(expected, HkcuTargetPolicy.AllowedTargets.Select(item => item.Identity)
            .OrderBy(item => item, StringComparer.Ordinal));
    }
}
