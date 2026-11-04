using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2aCatalogTests
{
    [Fact]
    public void CatalogContainsExactlyTheThreeAuthorizedOperations()
    {
        var catalog = T2aCatalog.Create();

        Assert.Equal(T2aCatalog.CatalogVersion, catalog.Version);
        Assert.Equal(T2aCatalog.OperationIds.OrderBy(item => item),
            catalog.All.Select(item => item.Id).OrderBy(item => item));
        Assert.Equal(3, catalog.All.Count);
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
    public void DarkModePreservesBothValuesSeparately()
    {
        var operation = T2aCatalog.Create().Get(T2aCatalog.EnableDarkModeId);

        Assert.Equal(2, operation.Targets.Count);
        Assert.Equal(2, operation.Changes.Count);
        Assert.All(operation.Changes, change =>
            Assert.Equal(T2aCatalog.PersonalizePath, change.Target.Path));
        Assert.Contains(operation.Changes, change => change.Target.ValueName == T2aCatalog.AppsUseLightThemeValue);
        Assert.Contains(operation.Changes, change => change.Target.ValueName == T2aCatalog.SystemUsesLightThemeValue);
        Assert.All(operation.Changes, change =>
        {
            Assert.Equal(RegistryValueKinds.DWord, change.DesiredValue.Kind);
            Assert.Equal("0", change.DesiredValue.Data);
        });
    }

    [Fact]
    public void AdapterAllowlistCoversCatalogTargetsExactly()
    {
        var catalogTargets = T2aCatalog.Create().All
            .SelectMany(item => item.Targets)
            .Select(item => item.Identity)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        var adapterTargets = HkcuTargetPolicy.AllowedTargets
            .Select(item => item.Identity)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(catalogTargets, adapterTargets);
    }

    [Fact]
    public async Task UnknownOperationIsRejectedBeforeAnyRead()
    {
        using var scope = new T2aScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Engine.SimulateAsync("SC-TW-NAO-EXISTE"));
        Assert.Equal(0, scope.Access.Reads);
    }

    [Fact]
    public async Task HkcuTargetOutsideTheFixedPathsIsBlockedByPreflight()
    {
        var operation = T2aCatalog.Create().Get(T2aCatalog.ShowFileExtensionsId);
        var rogue = new OperationTarget(OperationTargetRoots.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Run", "Qualquer");
        var changed = operation with
        {
            Targets = [rogue],
            Changes = [new PlannedTarget(rogue, new TargetValueState(true, RegistryValueKinds.DWord, "0"))]
        };
        using var scope = new T2aScope(catalog: new OperationCatalog("t2a-rogue", [changed]));

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.TargetOutsideCatalog, simulation.Blockers);
        Assert.Equal(0, scope.Access.Reads);
    }

    [Fact]
    public void HkcuTargetWithTraversalOrAbsolutePathIsRejected()
    {
        var operation = T2aCatalog.Create().Get(T2aCatalog.ShowFileExtensionsId);
        foreach (var path in new[] { @"..\Software\X", @"\Software\X", @"Software\X\..\Y", @"Software/X" })
        {
            var rogue = new OperationTarget(OperationTargetRoots.CurrentUser, path, "Valor");
            var changed = operation with
            {
                Targets = [rogue],
                Changes = [new PlannedTarget(rogue, new TargetValueState(true, RegistryValueKinds.DWord, "0"))]
            };
            Assert.Throws<InvalidDataException>(() => new OperationCatalog("t2a-rogue", [changed]));
        }
    }

    [Fact]
    public async Task HighRiskHkcuOperationIsBlockedByTheBackend()
    {
        var operation = T2aCatalog.Create().Get(T2aCatalog.ShowFileExtensionsId) with
        {
            Id = "SC-TW-EXP-99",
            Risk = RiskLevel.High
        };
        using var scope = new T2aScope(catalog: new OperationCatalog("t2a-high", [operation]));
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 1);

        var simulation = await scope.Engine.SimulateAsync("SC-TW-EXP-99");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.RiskBlocked, simulation.Blockers);
        Assert.Throws<InvalidOperationException>(() => scope.Engine.Authorize(simulation, "autorizo"));
    }

    [Fact]
    public async Task Windows10BuildIsBlockedBeforeAnyWrite()
    {
        using var scope = new T2aScope(build: 19045);
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 1);

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.UnsupportedWindows, simulation.Blockers);
        Assert.Equal(0, scope.Access.Writes);
    }
}
