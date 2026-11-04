using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Expansão da T2B sobre o catálogo fechado da T2A. O catálogo publicado continua sendo um só: a versão
/// <c>t2b-hkcu-1</c> contém as três operações da T2A e as seis novas operações HKCU, sem catálogo paralelo.
/// Nenhuma operação aceita caminho, valor ou tipo fora desta definição versionada.
/// </summary>
public static class T2bCatalog
{
    public const string CatalogVersion = "t2b-hkcu-1";

    public const string ShowHiddenFilesId = "SC-TW-EXP-06";
    public const string HideTaskViewButtonId = "SC-TW-TSK-06";
    public const string HideTaskbarSearchId = "SC-TW-TSK-02";
    public const string SearchIconOnlyId = "SC-TW-TSK-03";
    public const string SearchIconAndLabelId = "SC-TW-TSK-04";
    public const string SearchFullBoxId = "SC-TW-TSK-05";

    public const string SearchPath = @"Software\Microsoft\Windows\CurrentVersion\Search";
    public const string HiddenValue = "Hidden";
    public const string ShowTaskViewButtonValue = "ShowTaskViewButton";
    public const string SearchboxTaskbarModeValue = "SearchboxTaskbarMode";

    public const string ReuseAttribution =
        "Win11Debloat (MIT) 5d88bfe9ef2be5a8a643166a6400eb78a30c46ec — definição de Registro " +
        "auditada na T0; nenhum script upstream executado.";

    public static IReadOnlyList<TaskbarSearchMode> SearchModes { get; } =
    [
        new(0, HideTaskbarSearchId, "Ocultar pesquisa",
            "Remove o elemento de pesquisa da barra de tarefas; atalhos e o menu Iniciar continuam disponíveis."),
        new(1, SearchIconOnlyId, "Somente ícone",
            "Mostra apenas o ícone de pesquisa na barra de tarefas."),
        new(2, SearchFullBoxId, "Caixa completa",
            "Mostra a caixa de pesquisa expandida na barra de tarefas."),
        new(3, SearchIconAndLabelId, "Ícone e texto",
            "Mostra o ícone com o rótulo 'Pesquisar' na barra de tarefas.")
    ];

    public static IReadOnlyList<string> OperationIds { get; } =
    [
        .. T2aCatalog.OperationIds,
        ShowHiddenFilesId,
        HideTaskViewButtonId,
        HideTaskbarSearchId,
        SearchIconOnlyId,
        SearchIconAndLabelId,
        SearchFullBoxId
    ];

    public static OperationCatalog Create() => new(CatalogVersion, Operations());

    public static IReadOnlyList<OperationDescriptor> Operations() =>
    [
        .. T2aCatalog.Operations(),
        ShowHiddenFiles(),
        HideTaskViewButton(),
        SearchMode(HideTaskbarSearchId, 0, "Ocultar pesquisa da barra de tarefas",
            "O elemento de pesquisa é removido da barra; a pesquisa continua acessível por atalho e pelo menu Iniciar."),
        SearchMode(SearchIconOnlyId, 1, "Mostrar somente o ícone de pesquisa",
            "Somente o ícone de pesquisa aparece na barra de tarefas."),
        SearchMode(SearchFullBoxId, 2, "Mostrar a caixa de pesquisa completa",
            "A caixa de pesquisa expandida aparece na barra de tarefas."),
        SearchMode(SearchIconAndLabelId, 3, "Mostrar ícone e texto de pesquisa",
            "O ícone com o rótulo 'Pesquisar' aparece na barra de tarefas.")
    ];

    public static string OperationIdForSearchMode(int mode) =>
        SearchModes.FirstOrDefault(item => item.Mode == mode)?.OperationId
        ?? throw new ArgumentOutOfRangeException(nameof(mode), mode,
            "Modo de pesquisa não catalogado; valores aceitos: 0, 1, 2 e 3.");

    public static TaskbarSearchMode? FindSearchMode(int mode) =>
        SearchModes.FirstOrDefault(item => item.Mode == mode);

    private static OperationDescriptor ShowHiddenFiles()
    {
        var target = new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.ExplorerAdvancedPath,
            HiddenValue);
        return new OperationDescriptor(
            ShowHiddenFilesId,
            1,
            "Explorador de Arquivos",
            "Mostrar arquivos e pastas ocultos",
            OperationScope.CurrentUser,
            ChangeKind.SetValue,
            RiskLevel.Low,
            OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true),
            [target],
            [new PlannedTarget(target, DWord(1))],
            new ProtectionPolicy(true, false, false),
            [],
            ["Itens ocultos passam a ser exibidos no Explorador de Arquivos quando o Windows honra a preferência."],
            ["Não altera a opção de mostrar arquivos protegidos do sistema operacional.",
             "Itens ocultos passam a aparecer; a alteração visual pode exigir atualizar o Explorador."],
            true,
            false,
            ["A escrita no Registro não prova, sozinha, que o Explorador já atualizou a exibição.",
             "O rollback restaura o valor observado, inclusive a ausência; não se presume que era 2."],
            ReuseAttribution);
    }

    private static OperationDescriptor HideTaskViewButton()
    {
        var target = new OperationTarget(OperationTargetRoots.CurrentUser, T2aCatalog.ExplorerAdvancedPath,
            ShowTaskViewButtonValue);
        return new OperationDescriptor(
            HideTaskViewButtonId,
            1,
            "Barra de tarefas",
            "Ocultar o botão Visualizar Tarefas",
            OperationScope.CurrentUser,
            ChangeKind.SetValue,
            RiskLevel.Low,
            OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true),
            [target],
            [new PlannedTarget(target, DWord(0))],
            new ProtectionPolicy(true, false, false),
            [],
            ["O botão Visualizar Tarefas é removido da barra; o atalho Win+Tab continua funcionando."],
            ["Não desabilita áreas de trabalho virtuais nem atalhos de teclado.",
             "Políticas ou componentes de terceiros podem manter o botão visível."],
            true,
            false,
            ["A alteração visual pode exigir reabrir ou atualizar o Explorador; o aplicativo não reinicia o Explorer."],
            ReuseAttribution);
    }

    private static OperationDescriptor SearchMode(string id, int mode, string displayName,
        string consequence)
    {
        var target = new OperationTarget(OperationTargetRoots.CurrentUser, SearchPath,
            SearchboxTaskbarModeValue);
        return new OperationDescriptor(
            id,
            1,
            "Barra de tarefas",
            displayName,
            OperationScope.CurrentUser,
            ChangeKind.SetValue,
            RiskLevel.Low,
            OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true),
            [target],
            [new PlannedTarget(target, DWord(mode))],
            new ProtectionPolicy(true, false, false),
            [],
            [consequence],
            ["Os quatro modos compartilham o mesmo valor; aplicar um modo substitui o modo observado.",
             "Não desabilita a pesquisa por atalho, menu Iniciar ou política de pesquisa."],
            true,
            false,
            ["A barra de tarefas pode exigir reabrir ou atualizar o Explorer; o aplicativo não reinicia processos.",
             "O rollback restaura o modo observado (0 a 3) e não supõe que o estado anterior era a caixa completa."],
            ReuseAttribution);
    }

    private static TargetValueState DWord(int value) =>
        new(true, RegistryValueKinds.DWord, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Modo de pesquisa catalogado da barra de tarefas.</summary>
public sealed record TaskbarSearchMode(int Mode, string OperationId, string DisplayName, string Description);

/// <summary>
/// Segunda camada do Safety Engine para todos os alvos HKCU autorizados. Deriva do catálogo publicado da T2B
/// para impedir que um catálogo adulterado apresente um alvo fora da lista fechada. O adaptador nativo mantém
/// a mesma lista de forma independente.
/// </summary>
internal static class HkcuCatalogGuard
{
    public static IReadOnlyList<OperationTarget> AllowedTargets { get; } =
        T2bCatalog.Operations()
            .SelectMany(operation => operation.Targets)
            .Where(target => string.Equals(target.Root, OperationTargetRoots.CurrentUser, StringComparison.Ordinal))
            .DistinctBy(target => target.Identity, StringComparer.Ordinal)
            .ToList();

    public static bool IsAllowed(OperationTarget target) =>
        AllowedTargets.Any(item => string.Equals(item.Identity, target.Identity, StringComparison.Ordinal));
}
