using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2aAdapterTests
{
    private static readonly OperationTarget HideFileExt = HkcuTargetPolicy.AllowedTargets
        .Single(item => item.ValueName == HkcuTargetPolicy.HideFileExtValue);

    [Theory]
    [InlineData(OperationTargetRoots.CurrentUser, HkcuTargetPolicy.ExplorerAdvancedPath, HkcuTargetPolicy.HideFileExtValue, true)]
    [InlineData(OperationTargetRoots.CurrentUser, HkcuTargetPolicy.ExplorerAdvancedPath, "OutroValor", false)]
    [InlineData(OperationTargetRoots.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", HkcuTargetPolicy.HideFileExtValue, false)]
    [InlineData("HKLM", HkcuTargetPolicy.ExplorerAdvancedPath, HkcuTargetPolicy.HideFileExtValue, false)]
    [InlineData("Synthetic", "Preference.Test", "Value", false)]
    public void TargetPolicyAcceptsOnlyTheExactHkcuAllowlist(string root, string path, string value, bool allowed)
    {
        var target = new OperationTarget(root, path, value);

        Assert.Equal(allowed, HkcuTargetPolicy.IsAllowed(target));
    }

    [Fact]
    public void TargetPolicyRejectsKeyOnlyAndMalformedTargets()
    {
        Assert.False(HkcuTargetPolicy.IsAllowed(new OperationTarget(
            OperationTargetRoots.CurrentUser, HkcuTargetPolicy.ExplorerAdvancedPath, null)));
        Assert.False(HkcuTargetPolicy.IsAllowed(new OperationTarget(
            OperationTargetRoots.CurrentUser, @"\Software\X", HkcuTargetPolicy.HideFileExtValue)));
        Assert.False(HkcuTargetPolicy.IsAllowed(new OperationTarget(
            OperationTargetRoots.CurrentUser, @"Software\X\..\Y", HkcuTargetPolicy.HideFileExtValue)));
    }

    [Fact]
    public async Task ReaderDistinguishesPresentAndAbsentValue()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(HideFileExt, 1);
        var present = await scope.Reader.ReadAsync(HideFileExt);

        scope.Access.SeedAbsentValue(HideFileExt);
        var absent = await scope.Reader.ReadAsync(HideFileExt);

        Assert.True(present.IsKnown);
        Assert.True(present.TargetExists);
        Assert.Equal(new TargetValueState(true, RegistryValueKinds.DWord, "1"), present.Value);
        Assert.True(absent.IsKnown);
        Assert.True(absent.TargetExists);
        Assert.Equal(new TargetValueState(false, RegistryValueKinds.DWord, null), absent.Value);
    }

    [Fact]
    public async Task ReaderReportsMissingKeyAsUnrecoverableState()
    {
        using var scope = new T2aScope();
        scope.Access.SeedMissingKey(HideFileExt);

        var state = await scope.Reader.ReadAsync(HideFileExt);

        Assert.True(state.IsKnown);
        Assert.False(state.TargetExists);
        Assert.Contains("não cria chaves", state.Limitation);
    }

    [Fact]
    public async Task ReaderReportsAccessDeniedAsUnknownWithoutEscalation()
    {
        using var scope = new T2aScope();
        scope.Access.DenyRead(HideFileExt);

        var state = await scope.Reader.ReadAsync(HideFileExt);

        Assert.False(state.IsKnown);
        Assert.Contains("Acesso negado", state.Limitation);
    }

    [Fact]
    public async Task ReaderPreservesAnUnexpectedValueKind()
    {
        using var scope = new T2aScope();
        scope.Access.SeedString(HideFileExt, "");

        var state = await scope.Reader.ReadAsync(HideFileExt);

        Assert.Equal(new TargetValueState(true, RegistryValueKinds.String, ""), state.Value);
    }

    [Fact]
    public async Task ReaderRejectsTargetOutsideTheCatalog()
    {
        using var scope = new T2aScope();
        var rogue = new OperationTarget(OperationTargetRoots.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Run", HkcuTargetPolicy.HideFileExtValue);

        var state = await scope.Reader.ReadAsync(rogue);

        Assert.False(state.IsKnown);
        Assert.Equal(0, scope.Access.Reads);
    }

    [Fact]
    public async Task ExecutorAppliesOnlyAnAllowedDwordTarget()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(HideFileExt, 1);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2aCatalog.ShowFileExtensionsId, 1, ChangeKind.SetValue, HideFileExt,
            new TargetValueState(true, RegistryValueKinds.DWord, "1"),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await scope.Executor.ApplyAsync(request);

        Assert.True(outcome.Applied);
        Assert.Equal("0", scope.Access.Data(HideFileExt));
    }

    [Fact]
    public async Task ExecutorRefusesOffCatalogTargetWithNoWrite()
    {
        using var scope = new T2aScope();
        var rogue = new OperationTarget(OperationTargetRoots.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Run", "Qualquer");
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2aCatalog.ShowFileExtensionsId, 1, ChangeKind.SetValue, rogue,
            new TargetValueState(false, RegistryValueKinds.DWord, null),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await scope.Executor.ApplyAsync(request);

        Assert.False(outcome.Applied);
        Assert.Equal(0, scope.Access.Reads);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task ExecutorRefusesStateChangedBeforeWrite()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(HideFileExt, 0);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2aCatalog.ShowFileExtensionsId, 1, ChangeKind.SetValue, HideFileExt,
            new TargetValueState(true, RegistryValueKinds.DWord, "1"),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await scope.Executor.ApplyAsync(request);

        Assert.False(outcome.Applied);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task ExecutorRefusesNonDwordAndKeyOperations()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(HideFileExt, 1);
        var before = new TargetValueState(true, RegistryValueKinds.DWord, "1");
        var wrongKind = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2aCatalog.ShowFileExtensionsId, 1, ChangeKind.SetValue, HideFileExt,
            before, new TargetValueState(true, RegistryValueKinds.String, "0"));
        var deleteKey = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2aCatalog.ShowFileExtensionsId, 1, ChangeKind.DeleteKey, HideFileExt,
            before, before);

        Assert.False((await scope.Executor.ApplyAsync(wrongKind)).Applied);
        Assert.False((await scope.Executor.ApplyAsync(deleteKey)).Applied);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task ExecutorDeletesValueWhenRestoringAbsence()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(HideFileExt, 0);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2aCatalog.ShowFileExtensionsId, 1, ChangeKind.DeleteValue, HideFileExt,
            new TargetValueState(true, RegistryValueKinds.DWord, "0"),
            new TargetValueState(false, RegistryValueKinds.DWord, null));

        var outcome = await scope.Executor.ApplyAsync(request);

        Assert.True(outcome.Applied);
        Assert.False(scope.Access.State(HideFileExt).Exists);
        Assert.Equal(1, scope.Access.Deletes);
    }

    [Fact]
    public async Task ExecutorTreatsAmbiguousWriteFailureAsIndeterminate()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(HideFileExt, 1);
        scope.Access.FeatureWriteFailure(HideFileExt);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(),
            T2aCatalog.ShowFileExtensionsId, 1, ChangeKind.SetValue, HideFileExt,
            new TargetValueState(true, RegistryValueKinds.DWord, "1"),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await scope.Executor.ApplyAsync(request);

        Assert.False(outcome.Applied);
        Assert.True(outcome.Indeterminate);
    }
}
