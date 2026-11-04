using System.Text.RegularExpressions;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public static partial class DebloatPolicy
{
    public const string FixturePublisher = DebloatFixtureIdentity.Publisher;
    public const string FixtureUser = DebloatFixtureIdentity.User;

    /// <summary>
    /// <paramref name="observed"/> distinguishes a real inventory observation from a documentary catalog entry.
    /// Catalog-only entries are never reported as absent from the computer.
    /// </summary>
    public static DebloatAssessment Assess(DebloatCatalog catalog, DebloatPackage package,
        string currentUserId, bool duplicate = false, bool observed = true)
    {
        var entry = catalog.Find(package.CatalogId);
        if (!observed)
            return new DebloatAssessment(package, entry,
                entry?.PreliminaryRisk ?? RiskLevel.NotValidated,
                DebloatPresence.NotVerified,
                entry?.PreliminaryRisk == RiskLevel.High ? DebloatEligibility.ProtectedOrBlocked
                    : DebloatEligibility.UnavailableForSimulation,
                false, false,
                ["Não verificado — inventário real indisponível; nenhuma consulta ao Windows foi realizada.",
                 "Entrada documental do catálogo não comprova presença, ausência, dependências ou segurança."],
                [DebloatLimits.SyntheticWarning, "Risco preliminar/fixture não autoriza remoção real.", DebloatLimits.RecoveryWarning]);

        var blockers = new List<string>();
        var risk = entry?.PreliminaryRisk ?? RiskLevel.NotValidated;
        if (entry is null || !string.Equals(entry.TechnicalName, package.TechnicalName, StringComparison.Ordinal))
        { blockers.Add("Identidade não corresponde ao catálogo fechado."); risk = RiskLevel.NotValidated; }
        if (!package.Synthetic || !string.Equals(package.Publisher, FixturePublisher, StringComparison.Ordinal))
        { blockers.Add("Só inventários de fixture sintética podem ser exercitados nesta fase."); risk = RiskLevel.NotValidated; }
        if (duplicate) { blockers.Add("Pacote duplicado ou múltiplas versões: seleção ambígua."); risk = RiskLevel.NotValidated; }
        if (!ValidIdentity(package)) { blockers.Add("Identidade completa inválida ou contraditória."); risk = RiskLevel.NotValidated; }
        if (package.DependencyStatus is DebloatDependencyStatus.Unknown or DebloatDependencyStatus.NotInvestigated ||
            !Enum.IsDefined(package.DependencyStatus))
        { blockers.Add("Dependências não determinadas; ausência de informação não prova segurança."); risk = RiskLevel.NotValidated; }
        if (package.DependencyStatus == DebloatDependencyStatus.KnownNone && package.Dependencies.Count != 0 ||
            package.DependencyStatus == DebloatDependencyStatus.Known && package.Dependencies.Count == 0)
        { blockers.Add("Informações de dependência contraditórias."); risk = RiskLevel.NotValidated; }
        if (package.Dependencies.Count > 0)
            blockers.Add("Subconjunto sintético inicial exige ausência conhecida de dependências; revisão adicional indisponível nesta fase.");
        if (package.Protected || package.SystemPackage || package.Kind is DebloatPackageKind.Framework or DebloatPackageKind.Resource ||
            package.Dependencies.Any(d => d.Critical || d.RequiredByOtherPackage) || entry?.PreliminaryRisk == RiskLevel.High)
        { blockers.Add("Pacote crítico, protegido, framework, recurso ou dependência compartilhada bloqueado."); risk = RiskLevel.High; }
        if (package.Kind != DebloatPackageKind.Appx || package.Scope != DebloatScope.CurrentUser ||
            package.UserId != currentUserId || package.Provisioned is not false || entry?.RemovalMethod != "Appx")
            blockers.Add("Fora do escopo: somente Appx/MSIX do usuário atual, sem provisionamento.");
        if (package.State != DebloatInstallState.Installed) blockers.Add("Pacote não está instalado no inventário consultado.");
        if (!Enum.IsDefined(package.Scope) || !Enum.IsDefined(package.Kind) || !Enum.IsDefined(package.State))
        { blockers.Add("Estado, tipo ou escopo desconhecido."); if (risk != RiskLevel.High) risk = RiskLevel.NotValidated; }
        if (entry is not null && !catalog.AllowsSynthetic(entry)) blockers.Add("Entrada apenas documental, fora do subconjunto sintético fechado.");
        if (risk is RiskLevel.High or RiskLevel.NotValidated) blockers.Add("Risco bloqueado no backend.");

        var identityDivergent = entry is null ||
            !string.Equals(entry.TechnicalName, package.TechnicalName, StringComparison.Ordinal) || !ValidIdentity(package);
        var presence = identityDivergent ? DebloatPresence.IdentityDivergent
            : package.State is DebloatInstallState.NotFound or DebloatInstallState.RemovedSynthetic
                ? DebloatPresence.AbsentInConsultedInventory
            : package.State == DebloatInstallState.Installed ? DebloatPresence.PresentInSyntheticInventory
            : DebloatPresence.Indeterminate;
        var protectedFact = package.Protected || package.SystemPackage ||
            package.Kind is DebloatPackageKind.Framework or DebloatPackageKind.Resource ||
            package.Dependencies.Any(d => d.Critical || d.RequiredByOtherPackage) || entry?.PreliminaryRisk == RiskLevel.High;
        var eligibility = blockers.Count == 0 ? DebloatEligibility.SimulationAvailable
            : protectedFact ? DebloatEligibility.ProtectedOrBlocked
            : DebloatEligibility.UnavailableForSimulation;

        return new DebloatAssessment(package, entry, risk, presence, eligibility,
            blockers.Count == 0, false,
            blockers.Distinct().ToArray(),
            [DebloatLimits.SyntheticWarning, "Risco preliminar/fixture não autoriza remoção real.", DebloatLimits.RecoveryWarning]);
    }

    public static bool ValidIdentity(DebloatPackage p) =>
        new[] { p.CatalogId, p.TechnicalName, p.PackageFamilyName, p.PackageFullName, p.Publisher,
            p.Version, p.Architecture, p.UserId, p.Source }.All(s =>
            !string.IsNullOrWhiteSpace(s) && s.Length <= DebloatLimits.MaximumText && !s.Any(char.IsControl) &&
            !s.Contains('*') && !s.Contains('?') && !s.Contains('\\') && !s.Contains('/') &&
            !s.Contains("<!--", StringComparison.Ordinal) && !s.Contains("```", StringComparison.Ordinal)) &&
        IdentityPart().IsMatch(p.TechnicalName) &&
        p.Architecture is "x64" or "x86" or "arm64" or "neutral" &&
        System.Version.TryParse(p.Version, out var version) && version.Revision >= 0 &&
        p.PackageFamilyName == p.TechnicalName + "_synthetic" &&
        p.PackageFullName == $"{p.TechnicalName}_{p.Version}_{p.Architecture}__synthetic" &&
        p.UserId == FixtureUser;

    [GeneratedRegex("^[A-Za-z0-9.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPart();
}
