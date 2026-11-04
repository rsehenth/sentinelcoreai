using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyConcurrencyTests
{
    [Fact]
    public void LockFactoryRejectsConcurrentAcquisitionAndAllowsAfterRelease()
    {
        using var scope = new SafetyScope();
        var key = scope.Key("Preference.Test");

        var handle = scope.Locks.Acquire(key);
        Assert.Throws<TargetLockedException>(() => scope.Locks.Acquire(key));
        handle.Dispose();
        using var again = scope.Locks.Acquire(key);
    }

    [Fact]
    public async Task TwoEnginesOnSameTargetExecuteOnlyOne()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var slow = new SlowExecutor(new SyntheticOperationExecutor(scope.Targets),
            TimeSpan.FromMilliseconds(250));
        scope.Executor = slow;
        scope.Rebuild();

        var storeB = new MemoryStore(scope.Root);
        var locksB = new FileExclusiveLockFactory(scope.Root);
        var engineB = new SafetyEngine(scope.Catalog, scope.Profile, scope.Targets, scope.Protection,
            storeB, slow, locksB, scope.Time, TimeSpan.FromMinutes(2));

        var simulationA = await scope.Engine.SimulateAsync("SYN-PREF-01");
        var simulationB = await engineB.SimulateAsync("SYN-PREF-01");
        var tokenA = scope.Engine.Authorize(simulationA, "a");
        var tokenB = engineB.Authorize(simulationB, "b");

        var results = await Task.WhenAll(
            scope.Engine.ExecuteAsync(simulationA.Plan, tokenA),
            engineB.ExecuteAsync(simulationB.Plan, tokenB));

        Assert.Single(results, item => item.Succeeded);
        Assert.Contains(results, item => item.Blockers.Contains(BlockerCode.TargetLocked)
            || item.Blockers.Contains(BlockerCode.StateChanged));
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task ConcurrentIdempotentAppendsProduceSingleRecord()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var execution = await scope.ExecuteAsync("SYN-PREF-01");
        var first = execution.Transaction!.Events[0];
        var directory = Path.Combine(scope.Root, "memory", "transactions",
            execution.Transaction.TransactionId.ToString("D"), "journal");
        var before = Directory.GetFiles(directory).Length;

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                try
                {
                    await scope.Store.AppendEventAsync(first);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            }));

        Assert.Contains(attempts, item => item);
        Assert.Equal(before, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public async Task ConcurrentAuthorizationConsumptionAllowsSingleWinner()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        var service = new AuthorizationService(scope.Time);
        var issued = service.Issue(simulation.Plan, "ok");
        var winners = 0;

        Parallel.For(0, 16, _ =>
        {
            if (service.Consume(issued)) Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
        Assert.Equal(BlockerCode.AuthorizationReused,
            service.Validate(issued, simulation.Plan, simulation.Plan.ObservedStateHash).Blocker);
        _ = token;
    }
}
