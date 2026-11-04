using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Fail-closed preflight. Synthetic eligibility never changes the real T0 policy.</summary>
public static class DebloatRemovalPreflightEngine
{
    public static DebloatRemovalPreflight Evaluate(DebloatCatalog catalog, DebloatRemovalObservation o,
        bool mediumReviewConfirmed = false)
    {
        var blockers = new List<string>();
        void Require(bool condition, string text) { if (!condition) blockers.Add(text); }
        Require(o.IsWindows11 && o.WindowsBuild >= 22000, "Windows 11 build 22000 ou superior obrigatório.");
        Require(o.ExecutorAvailable, "Executor indisponível.");
        Require(DebloatNativeIdentity.Safe(o.CurrentUserSecurityId) &&
            DebloatNativeIdentity.UserSidPattern().IsMatch(o.CurrentUserSecurityId), "SID do usuário inválido.");
        Require(DebloatRemovalIdentityPolicy.IsValid(o.SelectedPackage), "PackageFullName ou identidade exata inválida.");
        Require(o.SelectedPackage.UserSecurityId == o.CurrentUserSecurityId, "Pacote de outro usuário bloqueado.");
        var entry = catalog.Find(o.CatalogId);
        Require(entry is not null && entry.TechnicalName == o.SelectedPackage.TechnicalName &&
            entry.RemovalMethod == "Appx", "Catálogo desconhecido ou nome técnico divergente.");
        Require(o.CatalogVersion == DebloatCatalog.Version && o.CatalogHash == DebloatRemovalHashing.Hash(catalog.All),
            "Identidade/versão/hash do catálogo divergente.");
        Require(DebloatNativeIdentity.Safe(o.InventoryRevision), "Revisão do inventário desconhecida.");
        Require(o.QueryState == DebloatInventoryQueryState.Completed &&
            o.Completeness == DebloatObservationCompleteness.Complete, "Observação incompleta ou consulta não concluída.");
        Require(o.Protected is false && o.SystemPackage is false, "Pacote protegido, de sistema ou proteção desconhecida.");
        Require(o.Packages is { Count: > 0 and <= DebloatNativeLimits.MaximumPackages }, "Inventário vazio ou fora do limite.");
        var candidates = o.Packages?.Where(p => p.TechnicalName == o.SelectedPackage.TechnicalName ||
            p.PackageFamilyName == o.SelectedPackage.PackageFamilyName).ToArray() ?? [];
        Require(candidates.Length == 1, "Identidade ausente, duplicada ou múltiplas versões.");
        if (candidates.Length == 1)
        {
            var p = candidates[0];
            Require(DebloatRemovalIdentityPolicy.From(p) == o.SelectedPackage, "Versão, publisher, arquitetura ou SID divergente.");
            Require(DebloatNativeIdentity.IsValid(p), "Metadados de identidade inválidos.");
            Require(p.Scope == DebloatScope.CurrentUser && p.UserSecurityId == o.CurrentUserSecurityId &&
                p.Provisioned is false, "Somente usuário atual; provisionado, outro escopo ou desconhecido bloqueados.");
            Require(p.Kind == DebloatPackageKind.Appx && p.IsMainPackage && !p.IsFramework &&
                !p.IsResourcePackage && !p.IsOptional && !p.IsBundle, "Framework, recurso, opcional, bundle ou tipo desconhecido bloqueado.");
            Require(p.InstallState == DebloatNativeInstallState.Available, "Instalação indisponível ou desconhecida.");
        }
        var d = o.Dependencies;
        Require(d is not null && d.Completeness == DebloatObservationCompleteness.Complete && d.EffectsKnown &&
            d.UnknownConsequences is { Count: 0 }, "Dependências ou efeitos indiretos desconhecidos.");
        if (d is not null)
        {
            var lists = new[] { d.Direct, d.Reverse, d.Shared, d.PotentiallyAffected };
            Require(lists.All(l => l is not null && l.Count <= 512 && l.All(p =>
                DebloatRemovalIdentityPolicy.IsValid(p) && p.UserSecurityId == o.CurrentUserSecurityId)),
                "Identidade/escopo de dependência inválida.");
            Require(d.Reverse is { Count: 0 } && d.Shared is { Count: 0 }, "Dependência reversa ou compartilhada bloqueada.");
            Require(d.Direct is { Count: 0 } && d.PotentiallyAffected is { Count: 0 },
                "Efeitos indiretos representados; política atual não permite executá-los.");
            Require(d.ApplicationsMayClose is not null && d.ApplicationsMayClose.Count <= 512 &&
                d.ApplicationsMayClose.All(DebloatNativeIdentity.Safe), "Aplicativos que poderão ser encerrados não determinados.");
        }
        Require(o.ReinstallationDocumented && SafeText(o.ReinstallInformation), "Reinstalação documentada indisponível.");
        Require(o.DataLossAcknowledged && o.Consequences is { Count: > 0 and <= 32 } &&
            o.Consequences.All(SafeText), "Consequências/perda de dados não revisadas.");
        Require(entry is not null && o.ReinstallInformation == entry.ReinstallInformation &&
            o.Consequences.Contains(entry.Consequence, StringComparer.Ordinal) &&
            o.Consequences.Contains(entry.DataLossWarning, StringComparer.Ordinal), "Consequências/reinstalação divergem do catálogo.");
        Require(entry?.PreliminaryRisk != RiskLevel.Medium || mediumReviewConfirmed, "Revisão média adicional obrigatória.");
        Require(o.Synthetic && entry is not null && catalog.AllowsSynthetic(entry),
            "Somente subconjunto sintético fechado; inventário nativo não pode executar nesta fase.");
        var real = new List<string>(blockers)
        {
            DebloatRemovalMessages.NativeBlocked,
            "Catálogo T0: 153 NÃO VALIDADOS / 9 ALTO; nenhuma política individual autoriza remoção real.",
            DebloatNativeMessages.CatalogLacksIdentity
        };
        if (!DebloatRemovalBuildGate.AllowsRealExecution()) real.Add("Build gate de remoção incondicionalmente fechado.");
        return new(blockers.Count == 0, false, entry?.RealRisk ?? RiskLevel.NotValidated,
            blockers.Distinct().ToArray(), real.Distinct().ToArray());
    }

    private static bool SafeText(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= 1024 && !text.Any(char.IsControl) && !text.Contains("<!--", StringComparison.Ordinal) &&
        !text.Contains("```", StringComparison.Ordinal);
}
