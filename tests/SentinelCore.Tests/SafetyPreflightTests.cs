using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyPreflightTests
{
    [Fact]
    public async Task LowRiskPassesAndReadsObservedState()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        Assert.True(simulation.CanExecute);
        Assert.Empty(simulation.Blockers);
        Assert.False(simulation.HasEffects);
        Assert.Equal("A", simulation.Plan.Changes.Single().Before.Data);
        Assert.Equal("B", simulation.Plan.Changes.Single().After.Data);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task MediumRiskPassesWhenRestorePointIsAvailable()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Medium", "off");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-04");

        Assert.True(simulation.CanExecute);
        Assert.Contains(simulation.Protections, item => item.Contains("restauração", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MediumRiskIsBlockedWhenRestorePointIsUnavailable()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Medium", "off");
        scope.Protection.SetRestorePointAvailable(false, "Proteção do Sistema desativada no ambiente sintético.");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-04");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.ProtectionUnavailable, simulation.Blockers);
    }

    [Fact]
    public async Task WindowsTenIsBlocked()
    {
        using var scope = new SafetyScope(windows11: false);
        scope.Seed("Preference.Test", "A");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.UnsupportedWindows, simulation.Blockers);
    }

    [Fact]
    public async Task BuildBelowMinimumIsBlocked()
    {
        using var scope = new SafetyScope(build: 22631);
        scope.Seed("Preference.NewBuild", "old");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-96");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.UnsupportedWindows, simulation.Blockers);
    }

    [Fact]
    public async Task BuildAboveMaximumIsBlocked()
    {
        var descriptor = SafetyTestData.TwoTargetOperation() with
        {
            Compatibility = new OperationCompatibility("22000", "22000", true)
        };
        using var scope = new SafetyScope(catalog: new OperationCatalog("v-max", [descriptor]));
        scope.Seed("Preference.MultiA", "A");
        scope.Seed("Preference.MultiB", "B");

        var simulation = await scope.Engine.SimulateAsync(descriptor.Id);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.UnsupportedWindows, simulation.Blockers);
    }

    [Fact]
    public async Task MachineScopeIsBlockedWithoutElevation()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Machine", "off");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-92");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.ElevationRequired, simulation.Blockers);
    }

    [Fact]
    public async Task DisabledOperationIsBlocked()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Disabled", "off");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-93");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.OperationBlocked, simulation.Blockers);
    }

    [Fact]
    public async Task PolicyBlocksOperationById()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Profile.Update(scope.Profile.Read() with { BlockedOperationIds = ["SYN-PREF-01"] });
        scope.Rebuild();

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.PolicyConflict, simulation.Blockers);
    }

    [Fact]
    public async Task PolicyBlocksOperationByCategory()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Profile.Update(scope.Profile.Read() with { BlockedCategories = ["Preference"] });
        scope.Rebuild();

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.PolicyConflict, simulation.Blockers);
    }

    [Fact]
    public async Task MissingDependencyIsBlocked()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Dependent", "off");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-05");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.DependencyMissing, simulation.Blockers);
    }

    [Fact]
    public async Task SatisfiedDependencyPasses()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Dependent", "off");
        scope.Profile.Update(scope.Profile.Read() with { AppliedOperations = ["SYN-PREF-01"] });
        scope.Rebuild();

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-05");

        Assert.True(simulation.CanExecute);
    }

    [Fact]
    public async Task UnknownStateIsNotTreatedAsNonexistent()
    {
        using var scope = new SafetyScope();
        scope.Reader = new DelegatingTargetReader(target => Task.FromResult(
            ObservedTargetState.Unknown(target.Identity, scope.Time.Now, "acesso negado")));
        scope.Rebuild();

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.AccessDenied, simulation.Blockers);
        Assert.Equal("Unknown", simulation.Plan.Changes.Single().Before.Kind);
    }

    [Fact]
    public async Task ReadFailureIsReportedAsAccessDeniedWithoutThrowing()
    {
        using var scope = new SafetyScope();
        scope.Reader = new DelegatingTargetReader(_ => throw new IOException("acesso negado"));
        scope.Rebuild();

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.AccessDenied, simulation.Blockers);
        Assert.Contains(simulation.Limitations, item => item.Contains("acesso negado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonSerializableValueIsBlocked()
    {
        using var scope = new SafetyScope();
        scope.SeedKind("Preference.NonSerializable", "None", "opaque");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-95");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.Unrecoverable, simulation.Blockers);
    }

    [Fact]
    public async Task FullTargetBackupRequirementIsBlockedUntilImplemented()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.FullTarget", "off");

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-94");

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.ProtectionUnavailable, simulation.Blockers);
    }

    [Fact]
    public async Task PreflightDoesNotWriteToTarget()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var before = scope.Targets.Snapshot();

        await scope.Engine.SimulateAsync("SYN-PREF-01");

        var after = scope.Targets.Snapshot();
        Assert.Equal(before.Keys.OrderBy(item => item), after.Keys.OrderBy(item => item));
        Assert.Equal("A", after[scope.Key("Preference.Test")].Value!.Data);
    }

    [Fact]
    public void ObservedStateHashNormalizesAbsentRepresentation()
    {
        var key = new OperationTarget("Synthetic", "Preference.Test", "Value").Identity;
        var noValue = new ObservedTargetState(key, true, false, null, DateTimeOffset.UnixEpoch, null);
        var absentValue = new ObservedTargetState(key, true, false,
            new TargetValueState(false, "Unknown", null), DateTimeOffset.UnixEpoch, null);
        var existingKey = new ObservedTargetState(key, true, true,
            new TargetValueState(false, "String", null), DateTimeOffset.UnixEpoch, null);

        Assert.Equal(SafetyHashing.ObservedStateHash([noValue]),
            SafetyHashing.ObservedStateHash([absentValue]));
        Assert.NotEqual(SafetyHashing.ObservedStateHash([noValue]),
            SafetyHashing.ObservedStateHash([existingKey]));
    }

    [Fact]
    public async Task PlanHashCoversRiskScopeProtectionAndObservedState()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var plan = (await scope.Engine.SimulateAsync("SYN-PREF-01")).Plan;
        string Hash(OperationPlan value, RiskLevel risk, OperationScope scopeValue, ProtectionPolicy protection) =>
            SafetyHashing.PlanHash(value.PlanId, value.TransactionId, value.OperationId,
                value.OperationVersion, value.CatalogVersion, risk, scopeValue, protection,
                value.ObservedStateHash, value.Changes);

        var baseline = Hash(plan, plan.Risk, plan.Scope, plan.Protection);

        Assert.NotEqual(baseline, Hash(plan, RiskLevel.Medium, plan.Scope, plan.Protection));
        Assert.NotEqual(baseline, Hash(plan, plan.Risk, OperationScope.Machine, plan.Protection));
        Assert.NotEqual(baseline, Hash(plan, plan.Risk, plan.Scope,
            new ProtectionPolicy(true, false, true)));
        Assert.NotEqual(baseline, Hash(plan with { ObservedStateHash = "outro" }, plan.Risk, plan.Scope,
            plan.Protection));
    }

    [Fact]
    public void ObservedStateHashDistinguishesAbsentEmptyAndValue()
    {
        var key = new OperationTarget("Synthetic", "Preference.Test", "Value").Identity;
        var absent = SafetyHashing.ObservedStateHash(
            [new ObservedTargetState(key, true, true, new TargetValueState(false, "String", null),
                DateTimeOffset.UnixEpoch, null)]);
        var empty = SafetyHashing.ObservedStateHash(
            [new ObservedTargetState(key, true, true, new TargetValueState(true, "String", ""),
                DateTimeOffset.UnixEpoch, null)]);
        var value = SafetyHashing.ObservedStateHash(
            [new ObservedTargetState(key, true, true, new TargetValueState(true, "String", "A"),
                DateTimeOffset.UnixEpoch, null)]);
        var unknown = SafetyHashing.ObservedStateHash(
            [ObservedTargetState.Unknown(key, DateTimeOffset.UnixEpoch, "x")]);

        Assert.NotEqual(absent, empty);
        Assert.NotEqual(empty, value);
        Assert.NotEqual(value, unknown);
    }

    [Fact]
    public void ObservedStateHashIgnoresObservationTimestamp()
    {
        var target = new OperationTarget("Synthetic", "Preference.Test", "Value");
        var first = new ObservedTargetState(target.Identity, true, false, null,
            DateTimeOffset.UnixEpoch, null);
        var second = first with { ObservedAtUtc = DateTimeOffset.UnixEpoch.AddHours(5) };

        Assert.Equal(SafetyHashing.ObservedStateHash([first]), SafetyHashing.ObservedStateHash([second]));
    }
}
