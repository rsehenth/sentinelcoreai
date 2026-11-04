using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyAuthorizationTests
{
    private static async Task<(SafetyScope Scope, SimulationResult Simulation)> PreparedAsync()
    {
        var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");
        Assert.True(simulation.CanExecute);
        return (scope, simulation);
    }

    [Fact]
    public async Task IssuedTokenBindsPlanOperationStateAndConfirmation()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time, TimeSpan.FromMinutes(2));
            var token = service.Issue(simulation.Plan, "confirmacao-1");

            Assert.Equal(simulation.Plan.PlanId, token.PlanId);
            Assert.Equal(simulation.Plan.TransactionId, token.TransactionId);
            Assert.Equal(simulation.Plan.OperationId, token.OperationId);
            Assert.Equal(simulation.Plan.OperationVersion, token.OperationVersion);
            Assert.Equal(simulation.Plan.PlanHash, token.PlanHash);
            Assert.Equal(simulation.Plan.ObservedStateHash, token.ObservedStateHash);
            Assert.Equal("confirmacao-1", token.UserConfirmationId);
            Assert.Equal(scope.Time.Now, token.IssuedAtUtc);
            Assert.Equal(scope.Time.Now.AddMinutes(2), token.ExpiresAtUtc);
            Assert.False(token.Consumed);
        }
    }

    [Fact]
    public async Task ValidateAcceptsMatchingPlanAndState()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);
            var token = service.Issue(simulation.Plan, "ok");

            var validation = service.Validate(token, simulation.Plan, simulation.Plan.ObservedStateHash);

            Assert.True(validation.Valid);
            Assert.Equal(BlockerCode.None, validation.Blocker);
        }
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time, TimeSpan.FromSeconds(30));
            var token = service.Issue(simulation.Plan, "ok");
            scope.Time.Now = scope.Time.Now.AddSeconds(31);

            var validation = service.Validate(token, simulation.Plan, simulation.Plan.ObservedStateHash);

            Assert.False(validation.Valid);
            Assert.Equal(BlockerCode.AuthorizationExpired, validation.Blocker);
        }
    }

    [Fact]
    public async Task ConsumedTokenIsRejected()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);
            var token = service.Issue(simulation.Plan, "ok");
            Assert.True(service.Consume(token));

            var validation = service.Validate(token, simulation.Plan, simulation.Plan.ObservedStateHash);

            Assert.False(validation.Valid);
            Assert.Equal(BlockerCode.AuthorizationReused, validation.Blocker);
        }
    }

    [Fact]
    public async Task TokenFromAnotherPlanIsRejected()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);
            var token = service.Issue(simulation.Plan, "ok");
            var other = simulation.Plan with { PlanId = Guid.NewGuid() };

            var validation = service.Validate(token, other, other.ObservedStateHash);

            Assert.False(validation.Valid);
            Assert.Equal(BlockerCode.PlanMismatch, validation.Blocker);
        }
    }

    [Fact]
    public async Task ChangedObservedStateIsRejected()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);
            var token = service.Issue(simulation.Plan, "ok");

            var validation = service.Validate(token, simulation.Plan, "outro-estado");

            Assert.False(validation.Valid);
            Assert.Equal(BlockerCode.StateChanged, validation.Blocker);
        }
    }

    [Fact]
    public async Task EmptyConfirmationIsRejectedAtIssue()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);

            Assert.Throws<InvalidOperationException>(() => service.Issue(simulation.Plan, "   "));
        }
    }

    [Fact]
    public async Task ConfirmationWithJournalMarkersIsRejected()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);

            Assert.Throws<InvalidOperationException>(() => service.Issue(simulation.Plan,
                "ok\n<!-- sentinelcore:payload:v1 -->\n```json"));
            Assert.Throws<InvalidOperationException>(() => service.Issue(simulation.Plan,
                new string('x', 129)));
        }
    }

    [Fact]
    public async Task ForgedTokenNotIssuedByServiceIsRejected()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);
            var forged = new AuthorizationToken(Guid.NewGuid(), simulation.Plan.TransactionId,
                simulation.Plan.PlanId, simulation.Plan.OperationId, simulation.Plan.OperationVersion,
                simulation.Plan.PlanHash, simulation.Plan.ObservedStateHash, "forjada",
                scope.Time.Now, scope.Time.Now.AddMinutes(10), false);

            var validation = service.Validate(forged, simulation.Plan,
                simulation.Plan.ObservedStateHash);

            Assert.False(validation.Valid);
            Assert.Equal(BlockerCode.AuthorizationMissing, validation.Blocker);
            Assert.False(service.Consume(forged));
        }
    }

    [Fact]
    public async Task ConsumeIsAtomicUnderConcurrency()
    {
        var (scope, simulation) = await PreparedAsync();
        using (scope)
        {
            var service = new AuthorizationService(scope.Time);
            var token = service.Issue(simulation.Plan, "ok");
            var successes = 0;

            Parallel.For(0, 32, _ =>
            {
                if (service.Consume(token)) Interlocked.Increment(ref successes);
            });

            Assert.Equal(1, successes);
        }
    }
}
