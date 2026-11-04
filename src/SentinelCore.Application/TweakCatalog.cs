using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Catálogo fechado da T2A. Contém somente as três operações reais autorizadas, todas em HKCU do usuário
/// atual, sem elevação. Nenhum caminho, valor ou operação é aceito fora desta definição versionada.
/// </summary>
public static class T2aCatalog
{
    public const string CatalogVersion = "t2a-hkcu-1";

    public const string ShowFileExtensionsId = "SC-TW-EXP-05";
    public const string AlignTaskbarLeftId = "SC-TW-TSK-01";
    public const string EnableDarkModeId = "SC-TW-APP-04";

    public const string ExplorerAdvancedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string PersonalizePath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public const string HideFileExtValue = "HideFileExt";
    public const string TaskbarAlValue = "TaskbarAl";
    public const string AppsUseLightThemeValue = "AppsUseLightTheme";
    public const string SystemUsesLightThemeValue = "SystemUsesLightTheme";

    private const string ReuseAttribution =
        "Win11Debloat (MIT) 5d88bfe9ef2be5a8a643166a6400eb78a30c46ec — definição de Registro " +
        "auditada na T0; nenhum script upstream executado.";

    public static IReadOnlyList<string> OperationIds { get; } =
        [ShowFileExtensionsId, AlignTaskbarLeftId, EnableDarkModeId];

    public static OperationCatalog Create() => new(CatalogVersion, Operations());

    public static IReadOnlyList<OperationDescriptor> Operations() =>
    [
        ShowFileExtensions(),
        AlignTaskbarLeft(),
        EnableDarkMode()
    ];

    private static OperationDescriptor ShowFileExtensions()
    {
        var target = new OperationTarget(OperationTargetRoots.CurrentUser, ExplorerAdvancedPath,
            HideFileExtValue);
        return new OperationDescriptor(
            ShowFileExtensionsId,
            1,
            "Explorador de Arquivos",
            "Mostrar extensões de arquivos conhecidos",
            OperationScope.CurrentUser,
            ChangeKind.SetValue,
            RiskLevel.Low,
            OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true),
            [target],
            [new PlannedTarget(target, DarkOrOff())],
            new ProtectionPolicy(true, false, false),
            [],
            ["As extensões de arquivos conhecidos passam a ser exibidas no Explorador de Arquivos."],
            ["A alteração visual pode exigir reabrir ou atualizar o Explorador; o aplicativo não reinicia processos."],
            true,
            false,
            ["A escrita no Registro não prova, sozinha, que a interface do Explorador já foi atualizada."],
            ReuseAttribution);
    }

    private static OperationDescriptor AlignTaskbarLeft()
    {
        var target = new OperationTarget(OperationTargetRoots.CurrentUser, ExplorerAdvancedPath,
            TaskbarAlValue);
        return new OperationDescriptor(
            AlignTaskbarLeftId,
            1,
            "Barra de tarefas",
            "Alinhar os ícones da barra de tarefas à esquerda",
            OperationScope.CurrentUser,
            ChangeKind.SetValue,
            RiskLevel.Low,
            OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true),
            [target],
            [new PlannedTarget(target, DarkOrOff())],
            new ProtectionPolicy(true, false, false),
            [],
            ["Os ícones da barra de tarefas são alinhados à esquerda quando o Windows honra a preferência."],
            ["A barra de tarefas pode precisar de reinício do Explorer ou de nova sessão; o aplicativo não reinicia o Explorer."],
            true,
            false,
            ["Políticas corporativas ou componentes de terceiros podem impedir ou reverter o efeito visual."],
            ReuseAttribution);
    }

    private static OperationDescriptor EnableDarkMode()
    {
        var apps = new OperationTarget(OperationTargetRoots.CurrentUser, PersonalizePath,
            AppsUseLightThemeValue);
        var system = new OperationTarget(OperationTargetRoots.CurrentUser, PersonalizePath,
            SystemUsesLightThemeValue);
        return new OperationDescriptor(
            EnableDarkModeId,
            1,
            "Aparência",
            "Ativar o modo escuro para aplicativos e sistema",
            OperationScope.CurrentUser,
            ChangeKind.SetValue,
            RiskLevel.Low,
            OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true),
            [apps, system],
            [new PlannedTarget(apps, DarkOrOff()), new PlannedTarget(system, DarkOrOff())],
            new ProtectionPolicy(true, false, false),
            [],
            ["Aplicativos e sistema usam o tema escuro quando cada aplicativo respeita a preferência."],
            ["As duas escritas não formam uma transação atômica do Windows; falha parcial permanece explícita.",
             "Aplicativos de terceiros podem manter tema próprio ou sobrescrever a preferência."],
            false,
            false,
            ["Não substitui arquivos de tema, não altera políticas de aparência e não força aplicativos de terceiros.",
             "Cada valor é recuperado individualmente pelo estado observado."],
            ReuseAttribution);
    }

    private static TargetValueState DarkOrOff() =>
        new(true, RegistryValueKinds.DWord, "0");
}
