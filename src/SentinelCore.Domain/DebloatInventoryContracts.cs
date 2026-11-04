using System.Text.RegularExpressions;

namespace SentinelCore.Domain;

/// <summary>Provenance of an inventory observation. It is never inferred from documentary catalog data.</summary>
public enum DebloatInventoryOrigin { Synthetic, NativeAppx }

/// <summary>
/// Outcome of an inventory query. A query that did not complete must never be read as an empty inventory.
/// </summary>
public enum DebloatInventoryQueryState
{
    NotExecuted,
    Completed,
    CompletedNoResults,
    Partial,
    AccessDenied,
    Interrupted,
    Failed,
    Unavailable
}

/// <summary>How much of the requested scope was actually observed. Separate from the query outcome.</summary>
public enum DebloatObservationCompleteness { NotObserved, Complete, Partial, Unavailable }

/// <summary>Installation state derived from the package status reported by Windows.</summary>
public enum DebloatNativeInstallState { Unknown, Available, NotAvailable, Degraded }

/// <summary>Result of matching one observed package against the documentary catalog.</summary>
public enum DebloatCatalogMatchKind { NotInCatalog, Exact, Ambiguous, CatalogOnly, IdentityDivergent }

public static class DebloatNativeSources
{
    public const string PackageManager = "Windows.Management.Deployment.PackageManager.FindPackagesForUser";
    public const string ProviderVersion = "t3b-native-appx-1";
}

public static class DebloatNativeLimits
{
    public const int MaximumPackages = 4096;
    public const int MaximumText = 512;
}

public static class DebloatNativeMessages
{
    public const string ReadOnly =
        "Inventário nativo estritamente somente leitura; não existem remoção, provisionamento, instalação ou elevação neste caminho.";
    public const string NotHomologated =
        "Inventário nativo não homologado; execução real indisponível e operação bloqueada no backend.";
    public const string CatalogLacksIdentity =
        "O catálogo documental não contém Package Family Name, publisher, versão ou arquitetura por entrada; a correspondência é limitada ao nome técnico exato.";
    public const string OutOfCatalog = "Aplicativo identificado — fora do catálogo de remoção.";
    public const string Unobserved = "Não verificado — inventário real indisponível.";
    public const string ObservedAbsent = "Ausência observada somente nesta consulta completa; não é garantia de que o aplicativo nunca existiu.";
    public const string IncompleteObservation = "Observação incompleta; ausência não confirmada.";
    public const string CurrentUserOnly = "Somente pacotes instalados para o usuário atual são consultados.";
}

/// <summary>
/// Metadata copied from an installed package. Nullable fields mean unavailable, never a negative fact.
/// No application content, document, credential or other user's profile is represented.
/// </summary>
public sealed record DebloatNativePackageMetadata(
    string TechnicalName,
    string PackageFamilyName,
    string PackageFullName,
    string Publisher,
    string PublisherId,
    string Version,
    string Architecture,
    string UserSecurityId,
    DebloatPackageKind Kind,
    DebloatScope Scope,
    DebloatNativeInstallState InstallState,
    bool? Provisioned,
    bool IsFramework,
    bool IsResourcePackage,
    bool IsMainPackage,
    bool IsOptional,
    bool IsBundle,
    string Source,
    IReadOnlyList<string> Limitations);

public sealed record DebloatInventoryError(string Code, string Message, bool Fatal);

/// <summary>
/// Complete query result. Packages are only those actually observed; absence is never fabricated.
/// </summary>
public sealed record DebloatInventoryQueryResult(
    DebloatInventoryOrigin Origin,
    DebloatInventoryQueryState State,
    DebloatObservationCompleteness Completeness,
    bool IsCurrentUserScoped,
    string UserSecurityId,
    DateTimeOffset QueriedAtUtc,
    string ProviderVersion,
    IReadOnlyList<DebloatNativePackageMetadata> Packages,
    IReadOnlyList<DebloatInventoryError> Errors,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Read-only, current-user-only contract. It deliberately has no remove, deploy or provisioning member.
/// </summary>
public interface IDebloatNativeInventoryReader
{
    bool ReadOnly { get; }
    string ProviderVersion { get; }
    Task<DebloatInventoryQueryResult> ReadCurrentUserAsync(CancellationToken cancellationToken = default);
}

public static partial class DebloatNativeIdentity
{
    /// <summary>
    /// Conservative identity validation: exact internal consistency and safe text.
    /// A package that fails this check is blocked, never reported as a lesser risk.
    /// </summary>
    public static bool IsValid(DebloatNativePackageMetadata package) =>
        Safe(package.TechnicalName) && Safe(package.PackageFamilyName) && Safe(package.PackageFullName) &&
        Safe(package.Publisher) && Safe(package.PublisherId) && Safe(package.Version) &&
        Safe(package.Architecture) && Safe(package.UserSecurityId) && Safe(package.Source) &&
        package.Limitations is not null && package.Limitations.All(Safe) &&
        TechnicalNamePattern().IsMatch(package.TechnicalName) &&
        PublisherIdPattern().IsMatch(package.PublisherId) &&
        package.Architecture is "x64" or "x86" or "arm" or "arm64" or "neutral" or "x86onarm64" or "unknown" &&
        System.Version.TryParse(package.Version, out var version) && version.Revision >= 0 &&
        package.PackageFamilyName == package.TechnicalName + "_" + package.PublisherId &&
        package.PackageFullName.StartsWith(package.TechnicalName + "_", StringComparison.Ordinal) &&
        UserSidPattern().IsMatch(package.UserSecurityId) &&
        Enum.IsDefined(package.Kind) && Enum.IsDefined(package.Scope) && Enum.IsDefined(package.InstallState) &&
        package.Scope == DebloatScope.CurrentUser;

    public static bool Safe(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= DebloatNativeLimits.MaximumText &&
        !value.Any(char.IsControl) && !value.Contains('*') && !value.Contains('?') &&
        !value.Contains('\\') && !value.Contains('/') &&
        !value.Contains("<!--", StringComparison.Ordinal) && !value.Contains("```", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9.-]+$", RegexOptions.CultureInvariant)]
    public static partial Regex TechnicalNamePattern();

    [GeneratedRegex("^[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    public static partial Regex PublisherIdPattern();

    [GeneratedRegex(@"^S-\d-\d+(-\d+)+$", RegexOptions.CultureInvariant)]
    public static partial Regex UserSidPattern();
}
