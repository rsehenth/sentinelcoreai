using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyPresentationTests
{
    [Fact]
    public async Task PresentationContractExposesPlanRiskConsequencesAndRecovery()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        var presentation = SafetyPresentation.Build(simulation);

        Assert.Equal("SYN-PREF-01", presentation.OperationId);
        Assert.Equal(RiskLevel.Low, presentation.Risk);
        Assert.NotEmpty(presentation.Consequences);
        Assert.NotEmpty(presentation.Protections);
        Assert.Equal(simulation.Plan.PlanHash, presentation.PlanHash);
        Assert.True(presentation.RequiresAuthorization);
        Assert.Contains(presentation.RecoveryOptions, item => item.Contains("Recusar", StringComparison.Ordinal));
        Assert.Contains("observado", presentation.RecoverySummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PresentationDoesNotClaimSuccessBeforeExecution()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");

        var presentation = SafetyPresentation.Build(simulation);

        Assert.Contains("Nenhum efeito real", presentation.ResultSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }
}
