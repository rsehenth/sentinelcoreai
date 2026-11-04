using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RepairCenterTests
{
    [Fact]
    public async Task SfcPlanAuthorizesAndExecutesOnlyTheSyntheticFixture()
    {
        using var scope = new RepairScope();
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.SfcVerifyId);

        Assert.True(plan.SimulationAllowed);
        Assert.False(plan.RealExecutionAllowed);
        Assert.Equal("sfc.exe /verifyonly", plan.Invocation.DisplayCommand);
        Assert.Equal(64, plan.PlanHash.Length);
        Assert.StartsWith("AUTORIZAR REPARO SC-RP-SFC-01 ", RepairManager.BuildConfirmationText(plan));
        Assert.Equal(token.AuthorizationId, token.AuthorizationId);

        var result = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);

        Assert.True(result.Persisted);
        var record = Assert.IsType<RepairRecord>(result.Record);
        Assert.True(record.IsSynthetic);
        Assert.Equal(RepairExecutionMode.Simulation, record.ExecutionMode);
        Assert.Equal(RepairState.Completed, record.State);
        Assert.Equal(RepairResultClassification.NoIssuesReported, record.Result);
        Assert.Equal(RepairConfidence.High, record.Confidence);
        Assert.True(record.VerifiedSuccess);
        Assert.Contains(record.Output, line => line.Text.Contains("FIXTURE SINTÉTICA", StringComparison.Ordinal));
        Assert.Equal(token.AuthorizationId, record.AuthorizationId);
        Assert.Equal(1, scope.Executor.Calls);
    }

    [Fact]
    public async Task WrongConfirmationAndTamperedPlansAreRejectedBeforeExecution()
    {
        using var scope = new RepairScope();
        var plan = await scope.Center.PrepareAsync(RepairCatalog.SfcVerifyId, null);

        Assert.Throws<InvalidOperationException>(() => scope.Center.Authorize(plan, "AUTORIZAR REPARO errado"));
        var token = scope.Center.Authorize(plan, RepairManager.BuildConfirmationText(plan));

        var tamperedInvocation = plan with
        {
            Invocation = plan.Invocation with { Arguments = ["/scannow"] }
        };
        var tampered = await scope.Center.ExecuteAsync(tamperedInvocation, token,
            RepairFixtureScenario.SuccessNoIssues);
        var badHash = await scope.Center.ExecuteAsync(plan with { PlanHash = new string('0', 64) }, token,
            RepairFixtureScenario.SuccessNoIssues);

        Assert.Null(tampered.Record);
        Assert.Contains(RepairBlocker.PlanMismatch, tampered.Blockers);
        Assert.Null(badHash.Record);
        Assert.Contains(RepairBlocker.PlanMismatch, badHash.Blockers);
        Assert.Equal(0, scope.Executor.Calls);
    }

    [Fact]
    public async Task ReusingTheSameAuthorizationCannotExecuteTwice()
    {
        using var scope = new RepairScope();
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.SfcVerifyId);
        var first = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);

        var second = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);

        Assert.NotNull(first.Record);
        Assert.Null(second.Record);
        Assert.Contains(RepairBlocker.AuthorizationReused, second.Blockers);
        Assert.Equal(1, scope.Executor.Calls);
    }

    [Fact]
    public async Task ExpiredAuthorizationIsRejectedBeforeIntentOrExecution()
    {
        using var scope = new RepairScope();
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.SfcVerifyId);
        scope.Time.Now = scope.Time.Now.AddMinutes(3);

        var result = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);
        var history = await scope.Center.LoadHistoryAsync();

        Assert.Null(result.Record);
        Assert.Contains(RepairBlocker.AuthorizationExpired, result.Blockers);
        Assert.Equal(0, scope.Executor.Calls);
        Assert.Empty(history.UnfinishedIntents);
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task ChangedVolumeAfterPreparationBlocksExecution()
    {
        using var scope = new RepairScope();
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.ChkdskScanId, "C:");
        scope.Volumes.Volumes.RemoveAll(volume => volume.Id == "C:");

        var result = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);

        Assert.Null(result.Record);
        Assert.True(result.Blockers.Contains(RepairBlocker.PlanMismatch) ||
            result.Blockers.Contains(RepairBlocker.VolumeUnavailable));
        Assert.Equal(0, scope.Executor.Calls);
    }

    [Theory]
    [InlineData(RepairFixtureScenario.Cancelled, RepairState.Cancelled, RepairResultClassification.Cancelled)]
    [InlineData(RepairFixtureScenario.Interrupted, RepairState.Interrupted, RepairResultClassification.Interrupted)]
    [InlineData(RepairFixtureScenario.IncompleteOutput, RepairState.Completed, RepairResultClassification.OutputIncomplete)]
    [InlineData(RepairFixtureScenario.RepairSourceUnavailable, RepairState.Completed, RepairResultClassification.RepairSourceUnavailable)]
    [InlineData(RepairFixtureScenario.AccessDenied, RepairState.Completed, RepairResultClassification.AccessDenied)]
    public async Task SyntheticScenariosYieldExplicitStatesAndClassifications(
        RepairFixtureScenario scenario, RepairState state, RepairResultClassification classification)
    {
        using var scope = new RepairScope();
        var operation = scenario == RepairFixtureScenario.RepairSourceUnavailable
            ? RepairCatalog.DismRestoreHealthId
            : RepairCatalog.SfcRepairId;
        var (plan, token) = await scope.PrepareAsync(operation);
        var result = await scope.Center.ExecuteAsync(plan, token, scenario);

        var record = Assert.IsType<RepairRecord>(result.Record);
        Assert.Equal(state, record.State);
        Assert.Equal(classification, record.Result);
        Assert.True(record.IsSynthetic);
        Assert.True(record.RequiresIntervention || record.State == RepairState.Completed);
        Assert.NotEmpty(record.Steps);
        Assert.True(RepairStateMachine.Validate(record.Steps, out _));
        Assert.NotEmpty(record.AdditionalActions);
        Assert.Contains(record.AdditionalActions,
            action => action.Contains("Nenhuma ação é executada automaticamente", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChkdskGenericFailureDoesNotBecomeAVolumeUnavailableResult()
    {
        using var scope = new RepairScope();
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.ChkdskCheckId, "C:");

        var result = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.RepairFailed);

        var record = Assert.IsType<RepairRecord>(result.Record);
        Assert.Equal(RepairResultClassification.RepairFailed, record.Result);
        Assert.False(record.Evidence.AccessDenied);
    }

    [Fact]
    public async Task ChkdskPlanUsesTheValidatedVolumeAndNoFreeArgument()
    {
        using var scope = new RepairScope();
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.ChkdskScanId, "C:");
        var result = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.CorruptionFound);

        Assert.Equal(["C:", "/scan"], plan.Invocation.Arguments);
        Assert.Equal("chkdsk.exe C: /scan", plan.Invocation.DisplayCommand);
        var record = Assert.IsType<RepairRecord>(result.Record);
        Assert.Equal("C:", record.Volume?.Id);
        Assert.Equal(RepairResultClassification.CorruptionDetected, record.Result);
        Assert.True(record.Evidence.CorruptionDetected);
    }

    [Fact]
    public async Task IntentFailurePreventsAnyExecutorCall()
    {
        using var root = new RepairScope();
        var failing = new FailingRepairStore(root.Store) { FailIntent = true };
        using var scope = new RepairScope(failing);
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.SfcVerifyId);

        var result = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);

        Assert.Null(result.Record);
        Assert.Contains(RepairBlocker.PersistenceFailed, result.Blockers);
        Assert.Equal(0, scope.Executor.Calls);
    }

    [Fact]
    public async Task RecordFailureKeepsResultInMemoryAndIntentForRecovery()
    {
        using var root = new RepairScope();
        var failing = new FailingRepairStore(root.Store) { FailRecordOnce = true };
        using var scope = new RepairScope(failing);
        var (plan, token) = await scope.PrepareAsync(RepairCatalog.SfcVerifyId);

        var failed = await scope.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);
        var history = await scope.Center.LoadHistoryAsync();

        Assert.NotNull(failed.Record);
        Assert.False(failed.Persisted);
        Assert.Contains(RepairBlocker.PersistenceFailed, failed.Blockers);
        Assert.Single(history.UnfinishedIntents);
        Assert.Empty(history.Records);

        var saved = await scope.Center.SaveAsync(failed.Record!);
        var after = await scope.Center.LoadHistoryAsync();
        Assert.Equal(saved.Id, Assert.Single(after.Records).Id);
        Assert.Empty(after.UnfinishedIntents);
    }

    [Fact]
    public async Task ManagerLoadsFourCategoriesVolumeListAndDocumentedOperations()
    {
        using var scope = new RepairScope();
        var view = await scope.Manager.LoadAsync();

        Assert.Equal(8, view.Operations.Count);
        Assert.Equal(4, view.Volumes.Count);
        Assert.Equal(9, view.DocumentedOperations.Count);
        Assert.False(view.RealExecutionEnabled);
        Assert.Equal(RepairExecutionMode.Simulation, view.Mode);
        Assert.Contains(RepairGuidance.RealExecutionUnavailable, view.ModeText);
        var groups = RepairPresentation.GroupByCategory(view.Operations);
        Assert.Equal(4, groups.Count);
        Assert.Contains(groups, group => group.Category == RepairCategories.Integrity);
        Assert.Contains(groups, group => group.Category == RepairCategories.Image);
        Assert.Contains(groups, group => group.Category == RepairCategories.Volumes);
        Assert.Contains(groups, group => group.Category == RepairCategories.Recovery);
        var chkdsk = Assert.Single(view.Operations,
            item => item.OperationId == RepairCatalog.ChkdskScanId);
        Assert.True(chkdsk.SimulationAllowed);
    }

    [Fact]
    public void RealExecutionModeIsRejectedByTheOrchestrator()
    {
        using var scope = new RepairScope();
        var preflight = new RepairPreflightEngine(scope.Catalog, scope.Profile, scope.Volumes,
            scope.Tools, scope.Time);

        Assert.Throws<InvalidOperationException>(() => new RepairCenter(scope.Catalog, preflight,
            scope.Executor, scope.Store, scope.Time, mode: RepairExecutionMode.Real));
    }

    [Fact]
    public void StateMachineRejectsInvalidTransitionsAndGaps()
    {
        var valid = new[]
        {
            new RepairStep(1, DateTimeOffset.UtcNow, RepairState.Planned, "planned", null),
            new RepairStep(2, DateTimeOffset.UtcNow, RepairState.PreflightPassed, "preflight", null),
            new RepairStep(3, DateTimeOffset.UtcNow, RepairState.Authorized, "authorized", null),
            new RepairStep(4, DateTimeOffset.UtcNow, RepairState.Running, "running", null),
            new RepairStep(5, DateTimeOffset.UtcNow, RepairState.Completed, "completed", 0)
        };
        var invalid = new[]
        {
            new RepairStep(1, DateTimeOffset.UtcNow, RepairState.Planned, "planned", null),
            new RepairStep(2, DateTimeOffset.UtcNow, RepairState.Completed, "completed", 0)
        };
        var gap = new[]
        {
            new RepairStep(1, DateTimeOffset.UtcNow, RepairState.Planned, "planned", null),
            new RepairStep(3, DateTimeOffset.UtcNow, RepairState.PreflightPassed, "preflight", null)
        };

        Assert.True(RepairStateMachine.Validate(valid, out _));
        Assert.False(RepairStateMachine.Validate(invalid, out _));
        Assert.False(RepairStateMachine.Validate(gap, out _));
    }
}
