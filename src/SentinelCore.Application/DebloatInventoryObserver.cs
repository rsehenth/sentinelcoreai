using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Complete observation: query provenance, assessed observed packages, catalog-only entries and warnings.
/// It carries no removal authority and is never consumed by <see cref="DebloatManager"/>.
/// </summary>
public sealed record DebloatInventoryObservation(
    DebloatInventoryQueryResult Query,
    IReadOnlyList<DebloatNativeAssessment> Assessments,
    IReadOnlyList<string> Warnings,
    bool AbsenceConfirmed,
    int ObservedCount,
    int CatalogOnlyCount);

/// <summary>
/// Converts one native read-only query into a fail-closed observation.
/// A partial, denied, interrupted, failed or unavailable query never confirms absence.
/// </summary>
public sealed class DebloatInventoryObserver
{
    private readonly DebloatCatalog catalog;
    private readonly IDebloatNativeInventoryReader reader;

    public DebloatInventoryObserver(DebloatCatalog catalog, IDebloatNativeInventoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reader);
        if (!reader.ReadOnly)
            throw new InvalidOperationException("O leitor de inventário nativo deve ser estritamente somente leitura.");
        if (string.IsNullOrWhiteSpace(reader.ProviderVersion))
            throw new InvalidOperationException("Leitor de inventário sem versão de provedor.");
        this.catalog = catalog;
        this.reader = reader;
    }

    public async Task<DebloatInventoryObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var query = await reader.ReadCurrentUserAsync(cancellationToken);
        Validate(query);
        if (query.Packages.Count > DebloatNativeLimits.MaximumPackages)
            throw new InvalidOperationException("Inventário nativo excede o limite; observação recusada.");

        var complete = query.State is DebloatInventoryQueryState.Completed or DebloatInventoryQueryState.CompletedNoResults &&
            query.Completeness == DebloatObservationCompleteness.Complete &&
            query.Errors.Count == 0;

        var assessments = new List<DebloatNativeAssessment>(query.Packages.Count + catalog.All.Count);
        foreach (var package in query.Packages)
        {
            var ambiguity = query.Packages.Count(other =>
                string.Equals(other.TechnicalName, package.TechnicalName, StringComparison.Ordinal) ||
                string.Equals(other.PackageFamilyName, package.PackageFamilyName, StringComparison.Ordinal));
            assessments.Add(DebloatNativePolicy.Assess(catalog, package, ambiguity, query.State));
        }

        var observedNames = query.Packages.Select(package => package.TechnicalName)
            .ToHashSet(StringComparer.Ordinal);
        var catalogOnly = 0;
        foreach (var entry in catalog.All.Where(entry => !observedNames.Contains(entry.TechnicalName)))
        {
            var presence = complete ? DebloatPresence.AbsentInConsultedInventory
                : query.State == DebloatInventoryQueryState.Partial ||
                  query.Completeness == DebloatObservationCompleteness.Partial
                    ? DebloatPresence.Indeterminate
                    : DebloatPresence.NotVerified;
            assessments.Add(DebloatNativePolicy.Unobserved(entry, presence));
            catalogOnly++;
        }

        var warnings = new List<string> { DebloatNativeMessages.NotHomologated };
        warnings.AddRange(query.Errors.Select(error =>
            $"{DebloatInventoryText.Safe(error.Code)}: {DebloatInventoryText.Safe(error.Message)}"));
        warnings.AddRange(query.Limitations.Select(DebloatInventoryText.Safe));
        warnings.Add(DebloatPresentation.QueryState(query.State));
        if (!complete)
            warnings.Add("Consulta incompleta ou não executada: nenhuma ausência pode ser afirmada e nenhum risco é reduzido.");
        if (query.Packages.GroupBy(package => package.PackageFullName, StringComparer.Ordinal).Any(group => group.Count() > 1))
            warnings.Add("Identidades completas duplicadas observadas; operações permanecem bloqueadas.");
        if (query.State == DebloatInventoryQueryState.Partial)
            warnings.Add("Observação parcial: aplicativos não vistos permanecem indeterminados, nunca ausentes.");

        return new(query,
            assessments.OrderBy(value => value.CatalogEntry?.FriendlyName ?? value.Package!.TechnicalName,
                StringComparer.CurrentCultureIgnoreCase).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            complete, query.Packages.Count, catalogOnly);
    }

    private static void Validate(DebloatInventoryQueryResult query)
    {
        if (query.Origin != DebloatInventoryOrigin.NativeAppx)
            throw new InvalidOperationException("Origem do inventário não é nativa Appx.");
        if (!query.IsCurrentUserScoped)
            throw new InvalidOperationException("Consulta fora do usuário atual recusada.");
        if (query.Packages.Any(package => package.Scope != DebloatScope.CurrentUser))
            throw new InvalidOperationException("Somente pacotes do usuário atual são aceitos.");
        if (!string.IsNullOrEmpty(query.UserSecurityId) &&
            query.Packages.Any(package => !string.Equals(package.UserSecurityId, query.UserSecurityId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Pacotes de outro usuário ou SID inconsistente recusados.");
        if (query.State == DebloatInventoryQueryState.NotExecuted && query.Packages.Count != 0)
            throw new InvalidOperationException("Consulta não executada não pode retornar pacotes.");
        if (query.State == DebloatInventoryQueryState.CompletedNoResults && query.Packages.Count != 0)
            throw new InvalidOperationException("Consulta sem resultados não pode retornar pacotes.");
        if (query.Completeness == DebloatObservationCompleteness.Complete &&
            query.State is not (DebloatInventoryQueryState.Completed or DebloatInventoryQueryState.CompletedNoResults))
            throw new InvalidOperationException("Completude completa exige consulta concluída.");
        if (query.State == DebloatInventoryQueryState.Completed && query.Completeness != DebloatObservationCompleteness.Complete)
            throw new InvalidOperationException("Consulta concluída exige completude completa.");
        if (query.State == DebloatInventoryQueryState.CompletedNoResults &&
            query.Completeness != DebloatObservationCompleteness.Complete)
            throw new InvalidOperationException("Consulta sem resultados exige completude completa.");
        if (query.State == DebloatInventoryQueryState.NotExecuted &&
            query.Completeness != DebloatObservationCompleteness.NotObserved)
            throw new InvalidOperationException("Consulta não executada exige completude não observada.");
        if (query.State == DebloatInventoryQueryState.Partial &&
            query.Completeness != DebloatObservationCompleteness.Partial)
            throw new InvalidOperationException("Consulta parcial exige completude parcial.");
    }
}

/// <summary>
/// The native adapter compiles into its own Windows-targeted assembly. This gate documents that it is
/// not operational, not composed and not activated by any argument during T3B.
/// </summary>
public static class NativeDebloatInventoryGate
{
    public static bool Operational => false;
    public static bool AllowsActivation(IEnumerable<string>? arguments = null) => false;
}
