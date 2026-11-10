using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Repairs;

namespace SentinelCore.RepairBroker;

internal sealed class NativeRepairVolumes : IRepairVolumeProvider
{
    private readonly WindowsRepairVolumeProvider volumes = new();

    public async Task<IReadOnlyList<RepairVolume>> GetVolumesAsync(CancellationToken cancellationToken = default)
    {
        var available = await volumes.GetVolumesAsync(cancellationToken).ConfigureAwait(false);
        return available.Where(volume => volume.IsReady && volume.IsLocalFixed)
            .Select(volume => volume with { VolumeIdentity = BrokerWindows.VolumeIdentity(volume.RootPath) })
            .Where(volume => volume.VolumeIdentity is not null).ToArray();
    }
}

internal static class NativeRepairPreflight
{
    internal static RepairPreflightEngine Create(RepairCatalog catalog, NativeRepairVolumes volumes) =>
        new(catalog, new WindowsRepairSystemProfileReader(), volumes, new WindowsRepairToolLocator());

    internal static bool IsBound(RepairCatalog catalog, RepairIntent intent, RepairAuthorizationToken token,
        RepairOperationDescriptor operation, DateTimeOffset now)
    {
        var plan = intent.Plan;
        if (plan is not
            {
                Invocation.Arguments: not null,
                Preflight: { Profile: not null, Tool: not null, ObservedStateHash.Length: 64 },
                PlanHash.Length: 64
            })
            return false;
        if (intent.FormatVersion != 1 || intent.ExecutionMode != RepairExecutionMode.Real || intent.IsSynthetic ||
            intent.Id == Guid.Empty || intent.PlanId == Guid.Empty ||
            intent.Id != plan.ExecutionId || intent.PlanId != plan.PlanId ||
            intent.OperationId != plan.OperationId || intent.OperationVersion != plan.OperationVersion ||
            intent.CatalogVersion != plan.CatalogVersion || intent.CatalogContentHash != plan.CatalogContentHash ||
            intent.Kind != operation.Kind || intent.Risk != operation.Risk || intent.DisplayName != operation.DisplayName ||
            plan.DisplayName != operation.DisplayName || plan.Category != operation.Category ||
            intent.Volume?.Id != plan.Volume?.Id || intent.Volume?.VolumeIdentity != plan.Volume?.VolumeIdentity ||
            plan.Preflight.Volume?.Id != plan.Volume?.Id ||
            plan.Preflight.Volume?.VolumeIdentity != plan.Volume?.VolumeIdentity ||
            intent.InvocationDisplay != plan.Invocation.DisplayCommand || intent.AuthorizationId != token.AuthorizationId ||
            intent.ConfirmationId != token.UserConfirmationId || !token.Consumed || !plan.RealExecutionAllowed ||
            operation.IsDocumentedOnly || operation.Availability != RepairAvailability.Available ||
            operation.Risk is not (RiskLevel.Low or RiskLevel.Medium) ||
            intent.CreatedAtUtc.Offset != TimeSpan.Zero || plan.CreatedAtUtc.Offset != TimeSpan.Zero ||
            token.IssuedAtUtc.Offset != TimeSpan.Zero || token.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            token.IssuedAtUtc < plan.CreatedAtUtc || intent.CreatedAtUtc < token.IssuedAtUtc ||
            intent.CreatedAtUtc > now || now - plan.CreatedAtUtc >= RepairLimits.AuthorizationLifetime ||
            token.ExpiresAtUtc - token.IssuedAtUtc > RepairLimits.AuthorizationLifetime ||
            !RepairPlanValidation.MatchesCatalog(catalog, operation, plan, out _))
            return false;
        var observedHash = RepairHashing.PreflightHash(plan.Preflight.Profile, plan.Preflight.Tool,
            plan.Preflight.Volume, catalog.Version, catalog.ContentHash, operation.Id, operation.Version);
        return observedHash == plan.Preflight.ObservedStateHash &&
            RepairAuthorizationService.ValidateBinding(token, plan, observedHash, now).Valid;
    }

    internal static bool MatchesObservation(RepairCatalog catalog, RepairOperationDescriptor operation,
        RepairPlan plan, RepairPreflightResult current, bool callerElevated)
    {
        if (!current.SimulationAllowed || !current.Profile.Is64Bit ||
            operation.RequiresElevation && !current.Profile.IsElevated ||
            plan.Preflight.Profile.IsElevated != callerElevated ||
            current.Volume?.Id != plan.Volume?.Id ||
            current.Volume?.VolumeIdentity != plan.Volume?.VolumeIdentity ||
            operation.RequiresVolume && current.Volume?.VolumeIdentity is null ||
            !current.Tool.Available || current.Tool.Path is null ||
            !RepairInvocationBuilder.Matches(operation, plan.Invocation, current.Volume, current.Tool, out _))
            return false;
        // The expected elevation change is authenticated separately. All other observed values are rehashed.
        var profile = current.Profile with { IsElevated = callerElevated };
        var hash = RepairHashing.PreflightHash(profile, current.Tool, current.Volume, catalog.Version,
            catalog.ContentHash, operation.Id, operation.Version);
        return string.Equals(hash, plan.Preflight.ObservedStateHash, StringComparison.Ordinal);
    }
}
