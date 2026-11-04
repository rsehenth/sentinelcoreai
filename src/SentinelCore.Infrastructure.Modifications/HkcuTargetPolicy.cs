using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Modifications;

/// <summary>
/// Segunda camada de validação de alvos. O adaptador nativo não lê nem escreve qualquer caminho ou valor
/// recebido da interface: somente pares exatos, idênticos ao catálogo fechado de tweaks, são aceitos.
/// </summary>
public static class HkcuTargetPolicy
{
    public const string ExplorerAdvancedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string PersonalizePath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string SearchPath =
        @"Software\Microsoft\Windows\CurrentVersion\Search";

    public const string HideFileExtValue = "HideFileExt";
    public const string TaskbarAlValue = "TaskbarAl";
    public const string AppsUseLightThemeValue = "AppsUseLightTheme";
    public const string SystemUsesLightThemeValue = "SystemUsesLightTheme";
    public const string HiddenValue = "Hidden";
    public const string ShowTaskViewButtonValue = "ShowTaskViewButton";
    public const string SearchboxTaskbarModeValue = "SearchboxTaskbarMode";

    public static IReadOnlyList<OperationTarget> AllowedTargets { get; } =
    [
        new(OperationTargetRoots.CurrentUser, ExplorerAdvancedPath, HideFileExtValue),
        new(OperationTargetRoots.CurrentUser, ExplorerAdvancedPath, TaskbarAlValue),
        new(OperationTargetRoots.CurrentUser, PersonalizePath, AppsUseLightThemeValue),
        new(OperationTargetRoots.CurrentUser, PersonalizePath, SystemUsesLightThemeValue),
        new(OperationTargetRoots.CurrentUser, ExplorerAdvancedPath, HiddenValue),
        new(OperationTargetRoots.CurrentUser, ExplorerAdvancedPath, ShowTaskViewButtonValue),
        new(OperationTargetRoots.CurrentUser, SearchPath, SearchboxTaskbarModeValue)
    ];

    public static bool IsAllowed(OperationTarget? target) =>
        target is not null &&
        target.Root == OperationTargetRoots.CurrentUser &&
        OperationTargetRoots.IsWellFormedCurrentUserTarget(target) &&
        AllowedTargets.Any(item => string.Equals(item.Identity, target.Identity, StringComparison.Ordinal));

    public static string RejectionReason(OperationTarget? target)
    {
        if (target is null) return "Alvo ausente.";
        if (target.Root != OperationTargetRoots.CurrentUser)
            return "O adaptador real opera exclusivamente em HKCU do usuário atual.";
        if (!OperationTargetRoots.IsWellFormedCurrentUserTarget(target))
            return "Caminho ou nome de valor HKCU malformado.";
        return "Alvo fora do catálogo fechado de tweaks.";
    }
}
