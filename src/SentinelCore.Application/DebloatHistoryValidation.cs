using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Shared historical metadata validation; never observes installed packages or authorizes effects.</summary>
internal static class DebloatHistoryValidation
{
    internal static DebloatTargetSnapshot ReadDebloat(TransactionRecord record)
    {
        if (!record.OperationId.StartsWith("DBLT-", StringComparison.Ordinal) || record.Plan is null)
            throw new InvalidDataException("Registro sem plano Debloat.");
        var state = JsonSerializer.Deserialize<DebloatTargetSnapshot>(record.Plan.Changes.Single().Before.Data ?? "");
        if (state?.Package is not { } package || state.CatalogEntry is not { } entry ||
            package.Dependencies is null || package.Limitations is null || !package.Synthetic ||
            !Enum.IsDefined(package.State) || !Enum.IsDefined(package.Kind) || !Enum.IsDefined(package.Scope) ||
            !Enum.IsDefined(package.DependencyStatus) || !Enum.IsDefined(state.Scenario) ||
            !Enum.IsDefined(entry.PreliminaryRisk) || !Enum.IsDefined(entry.RealRisk) ||
            !DebloatPolicy.ValidIdentity(package) || package.CatalogId != entry.Id ||
            package.TechnicalName != entry.TechnicalName || record.OperationId != "DBLT-" + entry.Id ||
            package.Dependencies.Any(d => d is null || string.IsNullOrWhiteSpace(d.PackageFamilyName)))
            throw new InvalidDataException("Metadados históricos incompatíveis ou incompletos.");
        return state;
    }

    internal static DebloatRemovalSnapshot ReadRemoval(TransactionRecord record, DebloatCatalog catalog)
    {
        var state = JsonSerializer.Deserialize<DebloatRemovalSnapshot>(record.Plan!.Changes.Single().Before.Data!);
        if (state is null || state.SchemaVersion != 1 || state.Removed ||
            !Enum.IsDefined(state.Scenario) || !state.Observation.Synthetic ||
            !DebloatRemovalPreflightEngine.Evaluate(catalog, state.Observation, state.MediumReviewConfirmed).CanSimulate ||
            state.CatalogEntry != catalog.Find(state.Observation.CatalogId) ||
            record.OperationId != "DBLT-RMV-" + state.CatalogEntry.Id)
            throw new InvalidDataException("Snapshot T3C incompatível.");
        return state;
    }
}
