using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Exact, conservative correspondence between one observation and the documentary catalog.</summary>
public sealed record DebloatCatalogMatch(
    DebloatCatalogEntry? Entry,
    DebloatCatalogMatchKind Kind,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<DebloatCatalogEntry> Candidates);

public static class DebloatCatalogMatcher
{
    /// <summary>
    /// Only an exact technical-name match is accepted. The documentary catalog has no PFN, publisher,
    /// version or architecture column, so those axes cannot corroborate an identity and are declared as a limitation.
    /// Partial names never match and an ambiguous observation never selects an entry.
    /// </summary>
    public static DebloatCatalogMatch Match(DebloatCatalog catalog, DebloatNativePackageMetadata package,
        int duplicateIdentities)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(package);
        if (duplicateIdentities > 1)
            return new(null, DebloatCatalogMatchKind.Ambiguous,
                ["Mais de uma identidade ou versão observada para o mesmo nome técnico/família; seleção ambígua."], []);
        var candidates = catalog.All
            .Where(entry => string.Equals(entry.TechnicalName, package.TechnicalName, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
            return new(null, DebloatCatalogMatchKind.NotInCatalog,
                ["Nome técnico não corresponde a nenhuma entrada do catálogo documental."], []);
        if (candidates.Length > 1)
            return new(null, DebloatCatalogMatchKind.Ambiguous,
                ["Múltiplas entradas documentais com o mesmo nome técnico."], candidates);
        return new(candidates[0], DebloatCatalogMatchKind.Exact,
            ["Correspondência exata somente pelo nome técnico; o catálogo documental não possui PFN, publisher, versão ou arquitetura."],
            candidates);
    }
}

/// <summary>
/// Assessment of one observed native package or one catalog-only entry. This type is intentionally
/// distinct from <see cref="DebloatAssessment"/> so it cannot be fed to the synthetic removal manager.
/// </summary>
public sealed record DebloatNativeAssessment(
    DebloatNativePackageMetadata? Package,
    DebloatCatalogEntry? CatalogEntry,
    DebloatCatalogMatchKind MatchKind,
    RiskLevel EffectiveRisk,
    DebloatPresence Presence,
    DebloatEligibility Eligibility,
    bool CanSimulate,
    bool CanExecuteReal,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Limitations)
{
    public DebloatNativeCategory Category => DebloatNativePolicy.Classify(Package, EffectiveRisk);
}

public static class DebloatNativePolicy
{
    /// <summary>
    /// Real T0 classification is never lowered: high stays high and "not validated" stays "not validated".
    /// </summary>
    public static RiskLevel RiskFor(DebloatCatalogEntry? entry) => entry?.RealRisk switch
    {
        RiskLevel.High => RiskLevel.High,
        RiskLevel.NotValidated => RiskLevel.NotValidated,
        _ => entry?.PreliminaryRisk ?? RiskLevel.NotValidated
    };

    public static DebloatNativeAssessment Assess(DebloatCatalog catalog, DebloatNativePackageMetadata package,
        int duplicateIdentities, DebloatInventoryQueryState queryState)
    {
        ArgumentNullException.ThrowIfNull(package);
        var match = DebloatCatalogMatcher.Match(catalog, package, duplicateIdentities);
        var entry = match.Entry;
        var identityValid = DebloatNativeIdentity.HasConsistentMetadata(package) &&
            (package.Scope != DebloatScope.CurrentUser || DebloatNativeIdentity.IsValid(package)) &&
            (!package.IsBundle && package.Architecture is not ("x86onarm64" or "unknown")
                ? DebloatRemovalIdentityPolicy.HasExactIdentity(DebloatRemovalIdentityPolicy.From(package)) : true);
        var matchKind = identityValid ? match.Kind : DebloatCatalogMatchKind.IdentityDivergent;
        var risk = RiskFor(entry);
        if (entry is null && catalog.All.Any(candidate => candidate.RealRisk == RiskLevel.High &&
            string.Equals(candidate.TechnicalName, package.TechnicalName, StringComparison.OrdinalIgnoreCase)))
            risk = RiskLevel.High;
        if (!identityValid && risk is RiskLevel.Low or RiskLevel.Medium) risk = RiskLevel.NotValidated;
        var protectedFact = !identityValid || matchKind == DebloatCatalogMatchKind.Ambiguous ||
            package.IsFramework || package.IsResourcePackage || package.IsOptional || package.IsBundle ||
            package.Kind is DebloatPackageKind.Framework or DebloatPackageKind.Resource ||
            risk == RiskLevel.High || package.Signature == DebloatNativeSignature.System;

        var blockers = new List<string> { DebloatNativeMessages.NotHomologated };
        if (!identityValid) blockers.Add("Identidade nativa inválida ou internamente inconsistente.");
        if (matchKind == DebloatCatalogMatchKind.Ambiguous) blockers.AddRange(match.Reasons);
        if (matchKind == DebloatCatalogMatchKind.NotInCatalog) blockers.Add(DebloatNativeMessages.OutOfCatalog);
        if (!package.IsMainPackage) blockers.Add("Pacote não principal bloqueado (framework, recurso, opcional ou bundle).");
        if (package.IsOptional) blockers.Add("Pacote opcional exige política própria; bloqueado nesta fase.");
        if (package.IsBundle) blockers.Add("Bundle observado; a identidade do aplicativo principal não foi determinada.");
        if (package.Kind != DebloatPackageKind.Appx) blockers.Add("Somente pacotes Appx/MSIX principais são considerados.");
        if (package.Scope == DebloatScope.OtherUser) blockers.Add("Instalação de outro usuário é somente informativa.");
        if (package.Scope != DebloatScope.Provisioned && package.InstallState != DebloatNativeInstallState.Available)
            blockers.Add("Pacote não está totalmente disponível no estado observado.");
        if (package.Signature is null or DebloatNativeSignature.Unsigned)
            blockers.Add("Origem da assinatura indisponível ou pacote sem assinatura.");
        if (package.Signature == DebloatNativeSignature.System) blockers.Add("Pacote assinado como componente do Windows.");
        if (entry is not null && entry.RemovalMethod != "Appx") blockers.Add("Entrada do catálogo não é Appx; fora do escopo.");
        if (risk is RiskLevel.High or RiskLevel.NotValidated)
            blockers.Add("Risco real bloqueado no backend; classificação conservadora da T0 preservada.");
        if (queryState != DebloatInventoryQueryState.Completed)
            blockers.Add("Consulta de inventário não concluída; nenhuma inferência de ausência é permitida.");

        var limitations = new List<string>((package.Limitations ?? [])
            .Where(DebloatNativeIdentity.Safe).Take(32))
        {
            DebloatNativeMessages.ReadOnly,
            DebloatNativeMessages.CatalogLacksIdentity
        };
        if (matchKind == DebloatCatalogMatchKind.NotInCatalog) limitations.Add(DebloatNativeMessages.OutOfCatalog);
        if (!identityValid) limitations.Add("Metadados insuficientes ou contraditórios não são completados por suposição.");

        return new(package, entry, matchKind, risk, matchKind switch
            {
                DebloatCatalogMatchKind.IdentityDivergent => DebloatPresence.IdentityDivergent,
                DebloatCatalogMatchKind.Ambiguous => DebloatPresence.Indeterminate,
                _ => DebloatPresence.PresentInNativeInventory
            },
            protectedFact ? DebloatEligibility.ProtectedOrBlocked : DebloatEligibility.UnavailableForSimulation,
            false, false, blockers.Distinct(StringComparer.Ordinal).ToArray(), limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static DebloatNativeCategory Classify(DebloatNativePackageMetadata? package, RiskLevel risk)
    {
        if (package is null) return DebloatNativeCategory.Unknown;
        if (package.Signature == DebloatNativeSignature.System && package.PackageFamilyName is
            "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy" or "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy")
            return DebloatNativeCategory.Essential;
        if (package.IsFramework || package.IsResourcePackage) return DebloatNativeCategory.Dependency;
        if (package.Signature == DebloatNativeSignature.System) return DebloatNativeCategory.SystemComponent;
        if (risk == RiskLevel.High || package.IsBundle) return DebloatNativeCategory.Protected;
        if (package.IsOptional) return DebloatNativeCategory.Optional;
        if (package.Signature is DebloatNativeSignature.Store or DebloatNativeSignature.Enterprise or DebloatNativeSignature.Developer &&
            DebloatNativeIdentity.Safe(package.Publisher) && DebloatNativeIdentity.PublisherIdPattern().IsMatch(package.PublisherId) &&
            package.PublisherId is not ("8wekyb3d8bbwe" or "cw5n1h2txyewy") &&
            !package.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            return DebloatNativeCategory.ThirdParty;
        return DebloatNativeCategory.Unknown;
    }

    /// <summary>Catalog-only entry. Absence requires a complete query; incomplete observation is indeterminate.</summary>
    public static DebloatNativeAssessment Unobserved(DebloatCatalogEntry entry, DebloatPresence presence) =>
        new(null, entry, DebloatCatalogMatchKind.CatalogOnly, RiskFor(entry), presence,
            RiskFor(entry) == RiskLevel.High
                ? DebloatEligibility.ProtectedOrBlocked
                : DebloatEligibility.UnavailableForSimulation,
            false, false,
            [DebloatNativeMessages.NotHomologated],
            [DebloatNativeMessages.CatalogLacksIdentity, presence switch
            {
                DebloatPresence.AbsentInConsultedInventory => DebloatNativeMessages.ObservedAbsent,
                DebloatPresence.Indeterminate => DebloatNativeMessages.IncompleteObservation,
                _ => DebloatNativeMessages.Unobserved
            }]);
}

/// <summary>Bounds and sanitizes provider-controlled text before it reaches the UI.</summary>
internal static class DebloatInventoryText
{
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var cleaned = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return cleaned.Length <= DebloatNativeLimits.MaximumText
            ? cleaned
            : cleaned[..DebloatNativeLimits.MaximumText];
    }
}
