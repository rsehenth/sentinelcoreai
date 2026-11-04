using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2bSearchModeTests
{
    [Fact]
    public async Task ControllerObservesTheSingleSharedSearchValue()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 3);

        var state = await scope.SearchModes.LoadAsync();

        Assert.Equal(3, state.ObservedMode);
        Assert.Equal("3 — Ícone e texto", state.ObservedLabel);
        Assert.Null(state.Limitation);
        Assert.Equal(T2bCatalog.SearchFullBoxId, state.SourceOperationId);
        Assert.Equal(4, state.Options.Count);
    }

    [Fact]
    public async Task AbsentSearchValueIsReportedWithoutInventingAMode()
    {
        using var scope = new T2bScope();
        scope.Access.SeedAbsentValue(scope.SearchTarget());

        var state = await scope.SearchModes.LoadAsync();

        Assert.Null(state.ObservedMode);
        Assert.Contains("ausente", state.ObservedLabel, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(state.Limitation);
        Assert.All(state.Options, option => Assert.False(state.IsSelected(option.Mode)));
    }

    [Fact]
    public async Task UnexpectedKindAndOutOfRangeValuesAreNotConverted()
    {
        using var scope = new T2bScope();
        scope.Access.SeedString(scope.SearchTarget(), "2");
        var text = await scope.SearchModes.LoadAsync();

        scope.Access.SeedDWord(scope.SearchTarget(), 7);
        var outOfRange = await scope.SearchModes.LoadAsync();

        Assert.Null(text.ObservedMode);
        Assert.Contains("não interpretado", text.ObservedLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Null(outOfRange.ObservedMode);
        Assert.Contains("fora dos modos", outOfRange.ObservedLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, T2bCatalog.HideTaskbarSearchId, "0")]
    [InlineData(1, T2bCatalog.SearchIconOnlyId, "1")]
    [InlineData(2, T2bCatalog.SearchFullBoxId, "2")]
    [InlineData(3, T2bCatalog.SearchIconAndLabelId, "3")]
    public async Task PrepareEachModeReturnsTheCorrespondingPlanWithoutWriting(int mode, string operationId,
        string desired)
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 5);

        var simulation = await scope.SearchModes.PrepareAsync(mode);

        Assert.Equal(operationId, simulation.Operation.Id);
        Assert.Equal(desired, simulation.Plan.Changes.Single().After.Data);
        Assert.False(simulation.HasEffects);
        Assert.Equal(0, scope.Access.Writes);
        Assert.Equal("5", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
    }

    [Fact]
    public async Task PreparingEveryModeIsSideEffectFreeAndExclusive()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 2);
        var state = await scope.SearchModes.LoadAsync();

        foreach (var option in state.Options) await scope.SearchModes.PrepareAsync(option.Mode);

        Assert.Equal(0, scope.Access.Writes);
        Assert.Equal(1, state.Options.Count(option => state.IsSelected(option.Mode)));
        Assert.True(state.IsSelected(2));
        Assert.Equal(4, state.Options.Select(option => option.Mode).Distinct().Count());
        Assert.Equal(4, state.Options.Select(option => option.OperationId).Distinct().Count());
    }

    [Fact]
    public async Task PreparingAnUnknownModeThrowsBeforeAnyRead()
    {
        using var scope = new T2bScope();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scope.SearchModes.PrepareAsync(9));
        Assert.Equal(0, scope.Access.Reads);
    }

    [Theory]
    [InlineData(true, "DWord", "0", 0)]
    [InlineData(true, "String", "2", null)]
    [InlineData(false, "DWord", null, null)]
    public void ParseModeOnlyAcceptsCataloguedDwordValues(bool exists, string kind, string? data, int? expected)
    {
        var value = new TargetValueState(exists, kind, data);

        Assert.Equal(expected, TaskbarSearchModeController.ParseMode(value));
    }
}
