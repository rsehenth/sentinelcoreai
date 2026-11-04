using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Um grupo de cartões da aba Personalização, na ordem de categoria publicada.</summary>
public sealed record TweakCategoryGroup(string Category, IReadOnlyList<TweakOperationView> Operations);

/// <summary>
/// Agrupamento e estado de apresentação da T2B, independentes de WPF para serem verificáveis por testes
/// com dependências simuladas.
/// </summary>
public static class TweakPresentation
{
    public static IReadOnlyList<string> CategoryOrder { get; } =
        ["Explorador de Arquivos", "Barra de tarefas", "Aparência"];

    public static IReadOnlyList<TweakCategoryGroup> GroupByCategory(IEnumerable<TweakOperationView> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        var operations = views.ToList();
        var groups = CategoryOrder
            .Select(category => new TweakCategoryGroup(category,
                operations.Where(item => string.Equals(item.Category, category, StringComparison.Ordinal)).ToList()))
            .Where(group => group.Operations.Count > 0)
            .ToList();
        groups.AddRange(operations
            .Where(item => !CategoryOrder.Contains(item.Category, StringComparer.Ordinal))
            .GroupBy(item => item.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new TweakCategoryGroup(group.Key, group.ToList())));
        return groups;
    }
}

/// <summary>Estado observado do único valor compartilhado pelos quatro modos de pesquisa.</summary>
public sealed record TaskbarSearchModeState(
    int? ObservedMode,
    string ObservedLabel,
    IReadOnlyList<TaskbarSearchMode> Options,
    string? Limitation,
    string SourceOperationId)
{
    public bool IsSelected(int mode) => ObservedMode == mode;
}

/// <summary>
/// Controle único dos modos de pesquisa. Selecionar um modo apenas prepara o plano da operação catalogada;
/// nenhuma alteração é executada sem passar pelo Safety Engine e pela autorização específica.
/// </summary>
public sealed class TaskbarSearchModeController
{
    private readonly TweakManager manager;

    public TaskbarSearchModeController(TweakManager manager) =>
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public IReadOnlyList<TaskbarSearchMode> Options => T2bCatalog.SearchModes;

    public async Task<TaskbarSearchModeState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var simulation = await manager.SimulateAsync(T2bCatalog.SearchFullBoxId, cancellationToken);
        var before = simulation.Plan.Changes.Count == 1
            ? simulation.Plan.Changes[0].Before
            : new TargetValueState(false, RegistryValueKinds.Unknown, null);
        var mode = ParseMode(before);
        return new TaskbarSearchModeState(mode, Describe(mode, before), T2bCatalog.SearchModes,
            mode is null
                ? "O modo observado não pôde ser interpretado; o rollback continua usando o estado efetivamente lido."
                : null,
            T2bCatalog.SearchFullBoxId);
    }

    /// <summary>Prepara o plano de um modo catalogado sem executar qualquer efeito.</summary>
    public Task<SimulationResult> PrepareAsync(int mode, CancellationToken cancellationToken = default) =>
        manager.SimulateAsync(T2bCatalog.OperationIdForSearchMode(mode), cancellationToken);

    public static int? ParseMode(TargetValueState? value)
    {
        if (value is not { Exists: true }) return null;
        if (!string.Equals(value.Kind, RegistryValueKinds.DWord, StringComparison.Ordinal)) return null;
        if (!int.TryParse(value.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
            return null;
        return T2bCatalog.FindSearchMode(mode) is null ? null : mode;
    }

    public static string Describe(int? mode, TargetValueState? value)
    {
        if (mode is { } current && T2bCatalog.FindSearchMode(current) is { } option)
            return $"{current} — {option.DisplayName}";
        if (value is null ||
            string.Equals(value.Kind, RegistryValueKinds.Unknown, StringComparison.Ordinal))
            return "estado não observável";
        if (!value.Exists) return "valor ausente (o Windows aplica o padrão do sistema)";
        if (!string.Equals(value.Kind, RegistryValueKinds.DWord, StringComparison.Ordinal))
            return $"tipo {value.Kind} não interpretado";
        return $"valor {value.Data} fora dos modos catalogados";
    }
}
