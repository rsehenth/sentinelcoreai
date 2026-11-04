namespace SentinelCore.Domain;

/// <summary>
/// Raízes de alvo aceitas pelo catálogo fechado. A T2A acrescenta <c>HKCU</c> sem abrir HKLM, HKCR,
/// HKU de terceiros ou qualquer outro escopo. O adaptador nativo repete a validação de forma independente.
/// </summary>
public static class OperationTargetRoots
{
    public const string Synthetic = "Synthetic";
    public const string CurrentUser = "HKCU";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Synthetic,
        CurrentUser
    };

    public static bool IsAllowed(string? root) => root is not null && All.Contains(root);

    /// <summary>
    /// Validação estrutural de um alvo HKCU: apenas caminhos relativos ao perfil atual, sem curingas,
    /// sem traversal e sem raiz alternativa. Não substitui a lista exata de alvos do adaptador nativo.
    /// </summary>
    public static bool IsWellFormedCurrentUserTarget(OperationTarget target)
    {
        if (!string.Equals(target.Root, CurrentUser, StringComparison.Ordinal)) return false;
        if (target.ValueName is null || string.IsNullOrWhiteSpace(target.ValueName)) return false;
        if (target.ValueName.Contains('\\') || target.ValueName.Contains('#') ||
            target.ValueName.Any(char.IsControl)) return false;

        var path = target.Path;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith('\\') || path.EndsWith('\\')) return false;
        if (path.Contains('/') || path.Contains('*') || path.Contains('?') || path.Contains('#')) return false;
        if (path.Contains("..", StringComparison.Ordinal) || path.Any(char.IsControl)) return false;
        return path.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase) &&
               path.Length > "Software\\".Length;
    }
}

public static class RegistryValueKinds
{
    public const string DWord = "DWord";
    public const string String = "String";
    public const string ExpandString = "ExpandString";
    public const string None = "None";
    public const string Unknown = "Unknown";
}
