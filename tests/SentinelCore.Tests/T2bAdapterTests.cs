using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2bAdapterTests
{
    private static OperationTarget Hidden => HkcuTargetPolicy.AllowedTargets
        .Single(item => item.ValueName == HkcuTargetPolicy.HiddenValue);
    private static OperationTarget TaskView => HkcuTargetPolicy.AllowedTargets
        .Single(item => item.ValueName == HkcuTargetPolicy.ShowTaskViewButtonValue);
    private static OperationTarget Search => HkcuTargetPolicy.AllowedTargets
        .Single(item => item.ValueName == HkcuTargetPolicy.SearchboxTaskbarModeValue);

    [Theory]
    [InlineData(OperationTargetRoots.CurrentUser, HkcuTargetPolicy.ExplorerAdvancedPath, HkcuTargetPolicy.HiddenValue, true)]
    [InlineData(OperationTargetRoots.CurrentUser, HkcuTargetPolicy.ExplorerAdvancedPath, HkcuTargetPolicy.ShowTaskViewButtonValue, true)]
    [InlineData(OperationTargetRoots.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Search", HkcuTargetPolicy.SearchboxTaskbarModeValue, true)]
    [InlineData(OperationTargetRoots.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Search", "OutroValor", false)]
    [InlineData(OperationTargetRoots.CurrentUser, HkcuTargetPolicy.SearchPath, HkcuTargetPolicy.HiddenValue, false)]
    [InlineData("HKLM", HkcuTargetPolicy.SearchPath, HkcuTargetPolicy.SearchboxTaskbarModeValue, false)]
    public void TargetPolicyAcceptsOnlyTheExactExpandedAllowlist(string root, string path, string value,
        bool allowed)
    {
        Assert.Equal(allowed, HkcuTargetPolicy.IsAllowed(new OperationTarget(root, path, value)));
    }

    [Fact]
    public void TargetPolicyKeepsSevenExactTargetsWithoutDuplicates()
    {
        Assert.Equal(7, HkcuTargetPolicy.AllowedTargets.Count);
        Assert.Equal(7, HkcuTargetPolicy.AllowedTargets.Select(item => item.Identity).Distinct().Count());
        Assert.All(HkcuTargetPolicy.AllowedTargets, target =>
            Assert.True(OperationTargetRoots.IsWellFormedCurrentUserTarget(target)));
    }

    [Fact]
    public async Task ReaderReadsHiddenFilesAndTaskViewValues()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(Hidden, 2);
        scope.Access.SeedDWord(TaskView, 1);

        var hidden = await scope.Reader.ReadAsync(Hidden);
        var taskView = await scope.Reader.ReadAsync(TaskView);

        Assert.Equal(new TargetValueState(true, RegistryValueKinds.DWord, "2"), hidden.Value);
        Assert.Equal(new TargetValueState(true, RegistryValueKinds.DWord, "1"), taskView.Value);
        Assert.True(hidden.IsKnown);
    }

    [Fact]
    public async Task ReaderDistinguishesAbsentSearchValueFromMissingKey()
    {
        using var scope = new T2bScope();
        scope.Access.SeedAbsentValue(Search);
        var absent = await scope.Reader.ReadAsync(Search);
        scope.Access.SeedMissingKey(Search);
        var missing = await scope.Reader.ReadAsync(Search);

        Assert.True(absent.IsKnown);
        Assert.True(absent.TargetExists);
        Assert.Equal(new TargetValueState(false, RegistryValueKinds.DWord, null), absent.Value);
        Assert.True(missing.IsKnown);
        Assert.False(missing.TargetExists);
        Assert.Contains("não cria chaves", missing.Limitation);
    }

    [Fact]
    public async Task ReaderReportsDeniedAccessWithoutEscalation()
    {
        using var scope = new T2bScope();
        scope.Access.DenyRead(Search);

        var state = await scope.Reader.ReadAsync(Search);

        Assert.False(state.IsKnown);
        Assert.Contains("Acesso negado", state.Limitation);
    }

    [Fact]
    public async Task ReaderRejectsAnOffCatalogTargetWithNoRead()
    {
        using var scope = new T2bScope();
        var rogue = new OperationTarget(OperationTargetRoots.CurrentUser, T2bCatalog.SearchPath, "OutroValor");

        var state = await scope.Reader.ReadAsync(rogue);

        Assert.False(state.IsKnown);
        Assert.Equal(0, scope.Access.Reads);
    }

    [Fact]
    public async Task ExecutorAppliesDwordOnlyToAllowedNewTargets()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(Search, 3);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2bCatalog.SearchFullBoxId, 1, ChangeKind.SetValue, Search,
            new TargetValueState(true, RegistryValueKinds.DWord, "3"),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await scope.Executor.ApplyAsync(request);

        Assert.True(outcome.Applied);
        Assert.Equal("0", scope.Access.Data(Search));
    }

    [Fact]
    public async Task ExecutorRefusesOffCatalogTargetWithNoReadOrWrite()
    {
        using var scope = new T2bScope();
        var rogue = new OperationTarget(OperationTargetRoots.CurrentUser, T2bCatalog.SearchPath, "OutroValor");
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2bCatalog.SearchFullBoxId, 1, ChangeKind.SetValue, rogue,
            new TargetValueState(false, RegistryValueKinds.DWord, null),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await scope.Executor.ApplyAsync(request);

        Assert.False(outcome.Applied);
        Assert.Equal(0, scope.Access.Reads);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task ExecutorRefusesStateChangedAndNonDwordValues()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(Search, 2);
        var before = new TargetValueState(true, RegistryValueKinds.DWord, "3");
        var changed = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2bCatalog.SearchFullBoxId, 1, ChangeKind.SetValue, Search,
            before, new TargetValueState(true, RegistryValueKinds.DWord, "0"));
        var wrongKind = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2bCatalog.SearchFullBoxId, 1, ChangeKind.SetValue, Search,
            new TargetValueState(true, RegistryValueKinds.DWord, "2"),
            new TargetValueState(true, RegistryValueKinds.String, "0"));

        Assert.False((await scope.Executor.ApplyAsync(changed)).Applied);
        Assert.False((await scope.Executor.ApplyAsync(wrongKind)).Applied);
        Assert.Equal(0, scope.Access.Writes);
        Assert.Equal("2", scope.Access.Data(Search));
    }

    [Fact]
    public async Task ExecutorRefusesKeyOperationsOnTheNewTargets()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(Hidden, 2);
        var before = new TargetValueState(true, RegistryValueKinds.DWord, "2");
        var deleteKey = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2bCatalog.ShowHiddenFilesId, 1, ChangeKind.DeleteKey, Hidden, before, before);

        var outcome = await scope.Executor.ApplyAsync(deleteKey);

        Assert.False(outcome.Applied);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(0, true, true)]
    public void AppearancePolicyBlocksEnabledOrUninterpretableValues(int? value, bool unreadable, bool blocked)
    {
        var read = new HkcuPolicyRead(value, unreadable, null);

        Assert.Equal(blocked, HkcuPolicyDecision.BlocksAppearance(read));
    }

    [Fact]
    public void PolicyReadDistinguishesAbsentFromIndeterminate()
    {
        var absent = HkcuPolicyRead.Absent();
        var indeterminate = HkcuPolicyRead.Indeterminate("valor não interpretado");

        Assert.False(absent.IsUnreadable);
        Assert.Null(absent.Value);
        Assert.True(indeterminate.IsUnreadable);
        Assert.NotNull(indeterminate.Limitation);
        Assert.True(HkcuPolicyDecision.BlocksAppearance(indeterminate));
    }
}
