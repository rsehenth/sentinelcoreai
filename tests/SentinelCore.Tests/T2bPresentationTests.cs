using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2bPresentationTests
{
    private static TweakOperationView View(string id, string category) =>
        new(id, "Nome " + id, category, "Descrição " + id, RiskLevel.Low, "Windows 11",
            "atual", "pretendido", ["Backup"], true, false, [], ["Limitação"], "Disponível", null);

    [Fact]
    public void CategoriesAreOrderedExplorerTaskbarAppearance()
    {
        var groups = TweakPresentation.GroupByCategory(
        [
            View("3", "Aparência"),
            View("1", "Explorador de Arquivos"),
            View("2", "Barra de tarefas")
        ]);

        Assert.Equal(["Explorador de Arquivos", "Barra de tarefas", "Aparência"],
            groups.Select(group => group.Category));
        Assert.All(groups, group => Assert.Single(group.Operations));
    }

    [Fact]
    public void UnknownCategoriesAreAppendedAlphabetically()
    {
        var groups = TweakPresentation.GroupByCategory(
        [
            View("z", "Zeta"),
            View("a", "Alfa"),
            View("e", "Explorador de Arquivos")
        ]);

        Assert.Equal(["Explorador de Arquivos", "Alfa", "Zeta"],
            groups.Select(group => group.Category));
    }

    [Fact]
    public void EmptyInputProducesNoGroups()
    {
        Assert.Empty(TweakPresentation.GroupByCategory([]));
    }

    [Fact]
    public async Task LoadedCatalogGroupsIntoTheThreePublishedCategories()
    {
        using var scope = new T2bScope();
        var views = await scope.Manager.LoadAsync();

        var groups = TweakPresentation.GroupByCategory(views);

        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[0].Operations.Count);
        Assert.All(groups[0].Operations, view => Assert.Equal("Explorador de Arquivos", view.Category));
        Assert.Equal(6, groups[1].Operations.Count);
        Assert.All(groups[1].Operations, view => Assert.Equal("Barra de tarefas", view.Category));
        Assert.Single(groups[2].Operations);
        Assert.Equal(T2aCatalog.EnableDarkModeId, groups[2].Operations[0].OperationId);
        Assert.Equal(9, groups.Sum(group => group.Operations.Count));
    }

    [Fact]
    public void SearchModeDescriptionDistinguishesUnobservableFromAbsent()
    {
        var unobservable = new TargetValueState(false, RegistryValueKinds.Unknown, null);
        var absent = new TargetValueState(false, RegistryValueKinds.DWord, null);

        Assert.Equal("estado não observável",
            TaskbarSearchModeController.Describe(null, unobservable));
        Assert.Contains("ausente", TaskbarSearchModeController.Describe(null, absent),
            StringComparison.OrdinalIgnoreCase);
    }
}
