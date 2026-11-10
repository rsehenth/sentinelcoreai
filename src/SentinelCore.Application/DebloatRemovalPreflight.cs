using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed record DebloatNativeRemovalPreview(DebloatNativePackageMetadata Package,
    DateTimeOffset ObservedAtUtc, DebloatRemovalPreflight Preflight, bool DependenciesComplete,
    IReadOnlyList<string>? Dependencies, IReadOnlyList<string> Dependents, IReadOnlyList<string> SharedDependencies,
    IReadOnlyList<string> PotentiallyAffected, IReadOnlyList<string> Consequences, string Reinstallation);

/// <summary>Fail-closed preflight. Synthetic eligibility never changes the real T0 policy.</summary>
public static class DebloatRemovalPreflightEngine
{
    public static DebloatNativeRemovalPreview PreviewNative(DebloatInventoryObservation observation,
        DebloatNativeAssessment selected)
    {
        if (!observation.Assessments.Contains(selected) || selected.Package is not { } package)
            throw new InvalidOperationException("Selecione um pacote da observação atual.");

        var query = observation.Query;
        var scope = query.AdditionalScopes.SingleOrDefault(s => s.Scope == package.Scope);
        var currentUser = package.Scope == DebloatScope.CurrentUser;
        var scopedPackages = currentUser ? query.Packages : scope?.Packages ?? [];
        var scopeComplete = currentUser ? observation.AbsenceConfirmed : scope?.Complete == true;
        var identity = DebloatRemovalIdentityPolicy.From(package);
        var families = scopedPackages.Count(p =>
            string.Equals(p.PackageFamilyName, package.PackageFamilyName, StringComparison.OrdinalIgnoreCase));
        var exact = DebloatRemovalIdentityPolicy.HasExactIdentity(identity) &&
            scopedPackages.Count(p => DebloatRemovalIdentityPolicy.From(p) == identity) == 1 &&
            (!currentUser || DebloatRemovalIdentityPolicy.IsValid(identity) && identity.UserSecurityId == query.UserSecurityId);
        var names = scopedPackages.ToLookup(p => p.PackageFullName, StringComparer.Ordinal);
        var graphComplete = currentUser && scopeComplete && scopedPackages.All(p =>
            names[p.PackageFullName].Count() == 1 &&
            DebloatNativeIdentity.IsValid(p) &&
            p.DependencyFullNames is { } dependencies &&
            dependencies.All(name => name != p.PackageFullName && names[name].Count() == 1));
        var direct = package.DependencyFullNames;
        var others = scopedPackages.Where(p => p.PackageFullName != package.PackageFullName).ToArray();
        var owners = others.SelectMany(p => (p.DependencyFullNames ?? []).Select(dependency => (dependency, p.PackageFullName)))
            .ToLookup(edge => edge.dependency, edge => edge.PackageFullName, StringComparer.Ordinal);
        var reverse = owners[package.PackageFullName].Distinct(StringComparer.Ordinal).ToArray();
        var shared = direct?.Where(owners.Contains).ToArray() ?? [];
        var affected = Reachable(package.PackageFullName, name => names[name].SelectMany(p => p.DependencyFullNames ?? []))
            .Union(Reachable(package.PackageFullName, name => owners[name]), StringComparer.Ordinal).ToArray();

        var blockers = selected.Blockers.ToList();
        if (!scopeComplete) blockers.Add("A cobertura da consulta para o escopo selecionado é incompleta.");
        if (!exact || families != 1 || selected.MatchKind == DebloatCatalogMatchKind.Ambiguous)
            blockers.Add("Identidade exata ausente, inconsistente, duplicada ou com mais de uma versão no escopo.");
        if (selected.Category is DebloatNativeCategory.Unknown or DebloatNativeCategory.Essential
            or DebloatNativeCategory.SystemComponent or DebloatNativeCategory.Protected or DebloatNativeCategory.Dependency)
            blockers.Add("Categoria desconhecida ou protegida pela política de segurança.");
        if (!graphComplete) blockers.Add("Dependências ou efeitos indiretos não resolvidos no escopo.");
        if (reverse.Length > 0 || direct?.Count > 0)
            blockers.Add("Há efeitos em dependentes ou dependências. A política atual bloqueia remoção com efeitos indiretos.");
        if (package.Provisioned is null) blockers.Add("Estado de provisionamento indisponível para esta identidade.");
        if (currentUser && package.Provisioned == true) blockers.Add("O pacote também está provisionado. O escopo precisa de análise separada.");
        if (package.Scope == DebloatScope.Provisioned)
            blockers.Add("Desprovisionamento exige política específica para novas contas e verificação independente.");
        blockers.Add(DebloatNativeMessages.CatalogLacksIdentity);
        blockers.Add("A T0 não autoriza remoção de nenhum pacote real.");

        var consequences = new List<string>
        {
            package.Scope switch
            {
                DebloatScope.CurrentUser => "A remoção por usuário afeta a instalação do usuário atual e pode afetar dependências sem uso.",
                DebloatScope.Provisioned => "O desprovisionamento impede a instalação automática para novas contas. Instalações existentes exigem análise própria.",
                _ => "O inventário de outros usuários é agregado. A seleção não identifica uma conta alvo."
            },
            "A prévia usa a última observação. Ela não registra intenção nem concede autorização.",
            DebloatLimits.RecoveryWarning
        };
        if (selected.CatalogEntry is { } entry) consequences.AddRange([entry.Consequence, entry.DataLossWarning]);
        return new(package, query.QueriedAtUtc, new(false, false, selected.EffectiveRisk, [],
                blockers.Distinct(StringComparer.Ordinal).ToArray()), graphComplete, direct, reverse, shared, affected, consequences,
            selected.CatalogEntry is { } documented
                ? "Orientação do catálogo por nome técnico. Disponibilidade da fonte e identidade do instalador não verificadas. " + documented.ReinstallInformation
                : "Reinstalação indisponível pelo SentinelCore. Nenhuma fonte compatível foi comprovada.");
    }

    private static HashSet<string> Reachable(string start, Func<string, IEnumerable<string>> edges)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var package))
            foreach (var related in edges(package))
                if (visited.Add(related)) pending.Enqueue(related);
        return visited;
    }

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
