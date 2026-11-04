using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// A T2B preserva a separação de builds da T2A: o catálogo expandido é composto pelo mesmo gate e o build de
/// distribuição continua sem compilar a escrita real.
/// </summary>
public sealed class T2bDistributionGateTests
{
    [Fact]
    public void ExpandedCatalogListsAllNineOperationIds()
    {
        Assert.Equal(9, T2bCatalog.OperationIds.Count);
        Assert.All(T2aCatalog.OperationIds, id => Assert.Contains(id, T2bCatalog.OperationIds));
        Assert.Contains(T2bCatalog.ShowHiddenFilesId, T2bCatalog.OperationIds);
        Assert.Contains(T2bCatalog.HideTaskViewButtonId, T2bCatalog.OperationIds);
        Assert.All(T2bCatalog.SearchModes,
            mode => Assert.Contains(mode.OperationId, T2bCatalog.OperationIds));
    }

    [Fact]
    public void DistributionProfileBlocksEveryExpandedOperation()
    {
        var policy = new FakeHkcuPolicyProbe();
        var reader = new Windows11SystemProfileReader(policy, realExecutionEnabled: false,
            T2bCatalog.OperationIds, buildOverride: 22631);

        var profile = reader.Read();

        Assert.All(T2bCatalog.OperationIds,
            id => Assert.Contains(id, profile.BlockedOperationIds, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(profile.Limitations,
            item => item.Contains("desabilitado", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AppCompositionUsesTheExpandedCatalogAndGate()
    {
        var app = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));

        Assert.Contains("RealTweakBuildGate.AllowsRealExecution(e.Args)", app, StringComparison.Ordinal);
        Assert.Contains("T2bCatalog.Create()", app, StringComparison.Ordinal);
        Assert.Contains("T2bCatalog.OperationIds", app, StringComparison.Ordinal);
        Assert.DoesNotContain("T2aCatalog.Create()", app, StringComparison.Ordinal);
        Assert.DoesNotContain(RealTweakBuildGate.EnableArgument, app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledExecutorNeverTouchesANewTarget()
    {
        var executor = new DisabledOperationExecutor();
        var target = HkcuTargetPolicy.AllowedTargets
            .Single(item => item.ValueName == HkcuTargetPolicy.SearchboxTaskbarModeValue);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), T2bCatalog.HideTaskbarSearchId,
            1, ChangeKind.SetValue, target, new TargetValueState(true, RegistryValueKinds.DWord, "3"),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await executor.ApplyAsync(request);

        Assert.False(outcome.Applied);
        Assert.Contains("desabilitada", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchModesUseOneControlInsteadOfFourIndependentSwitches()
    {
        var xaml = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.xaml"));
        var code = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.Tweaks.cs"));

        Assert.Equal(1, CountOccurrences(xaml, "x:Name=\"TweakSearchModeCombo\""));
        Assert.Equal(1, CountOccurrences(xaml, "x:Name=\"TweakSearchModePrepareButton\""));
        Assert.Contains("OnTweakSearchModePrepare", xaml, StringComparison.Ordinal);
        Assert.Contains("TaskbarSearchModeController", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TweakSearchMode02", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void NewOperationIdentifiersAreUniqueAndFollowTheAuditedPrefixes()
    {
        var ids = new[]
        {
            T2bCatalog.ShowHiddenFilesId, T2bCatalog.HideTaskViewButtonId,
            T2bCatalog.HideTaskbarSearchId, T2bCatalog.SearchIconOnlyId,
            T2bCatalog.SearchFullBoxId, T2bCatalog.SearchIconAndLabelId
        };

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("SC-TW-", id, StringComparison.Ordinal));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
