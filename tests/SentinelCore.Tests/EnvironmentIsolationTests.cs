using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EnvironmentIsolationTests
{
    [Fact]
    public void ProfileUsesOnlyInjectedProvidersAndPropagatesTheirFailure()
    {
        var windowsCalls = 0; var buildCalls = 0; var privilegeCalls = 0;
        var profile = new Windows11SystemProfileReader(new FakeHkcuPolicyProbe(), false, [],
            () => { windowsCalls++; return true; },
            () => { buildCalls++; return 22631; },
            () => { privilegeCalls++; return true; });
        var result = profile.Read();
        Assert.True(result.IsWindows11);
        Assert.Equal(22631, result.Build);
        Assert.Equal(1, windowsCalls); Assert.Equal(1, buildCalls); Assert.Equal(1, privilegeCalls);

        var failing = new Windows11SystemProfileReader(new FakeHkcuPolicyProbe(), false, [],
            () => true, () => 22631, () => throw new InvalidOperationException("fixture"));
        Assert.Throws<InvalidOperationException>(() => failing.Read());
        Assert.Throws<ArgumentNullException>(() => new Windows11SystemProfileReader(
            new FakeHkcuPolicyProbe(), false, [], () => true, () => 22631, null!));
    }

    [Fact]
    public void LimiterUsesExplicitArtificialProfileAndRejectsMissingProfile()
    {
        const string profile = @"D:\SyntheticProfile";
        var result = RepairOutputLimiter.Limit([profile + @"\private.txt"], profile);
        Assert.DoesNotContain(profile, result.Lines[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", result.Lines[0].Text, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => RepairOutputLimiter.Limit(["fixture"], null!));
    }

    [Fact]
    public async Task RepairCenterUsesInjectedProfileAndDoesNotReadItOnEarlyExit()
    {
        var calls = 0;
        using var scope = new RepairScope(profilePath: () => { calls++; return @"D:\SyntheticProfile"; });
        var blocked = await scope.Center.ExecuteAsync(null!, null!, RepairFixtureScenario.SuccessNoIssues);
        Assert.Null(blocked.Record); Assert.Equal(0, calls);
        var result = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        Assert.NotNull(result.Record); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RepairCenterDoesNotFallbackWhenInjectedProfileFails()
    {
        var calls = 0;
        using var scope = new RepairScope(profilePath: () => { calls++; throw new InvalidOperationException("fixture"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ExecuteAsync(RepairCatalog.SfcVerifyId));
        Assert.Equal(1, calls);
    }
}
