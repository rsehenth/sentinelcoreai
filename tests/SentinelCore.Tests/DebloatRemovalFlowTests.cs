using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class DebloatRemovalFlowTests
{
    [Theory]
    [InlineData(DebloatRemovalScenario.Success, DebloatRemovalState.VerifiedSynthetic, true)]
    [InlineData(DebloatRemovalScenario.AccessDenied, DebloatRemovalState.Failed, false)]
    [InlineData(DebloatRemovalScenario.PartialFailure, DebloatRemovalState.PartialFailure, false)]
    [InlineData(DebloatRemovalScenario.Interrupted, DebloatRemovalState.Interrupted, false)]
    [InlineData(DebloatRemovalScenario.Indeterminate, DebloatRemovalState.Indeterminate, false)]
    [InlineData(DebloatRemovalScenario.Unverifiable, DebloatRemovalState.VerificationFailed, false)]
    [InlineData(DebloatRemovalScenario.PostVerificationFailed, DebloatRemovalState.VerificationFailed, false)]
    public async Task SyntheticLifecyclePreservesDurableResultsAndReopens(DebloatRemovalScenario scenario,
        DebloatRemovalState expected, bool success)
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare(scenario);
        scope.Adapter.AfterStart = () =>
        {
            var files = Directory.GetFiles(Path.Combine(scope.Root, "memory", "debloat", plan.Simulation.Plan.TransactionId.ToString("D"), "journal"));
            Assert.Contains(files, f => f.EndsWith("-ExecutionStarted.md", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("-Authorized.md", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("-BackupVerified.md", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("-Planned.md", StringComparison.Ordinal));
        };
        var result = await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan));
        Assert.Equal(expected, result.State);
        Assert.Equal(success, result.Succeeded);
        Assert.Equal(1, scope.Adapter.ExecutionCount);
        Assert.NotNull(result.Receipt);
        Assert.Equal(plan.Simulation.Plan.PlanHash, scope.Adapter.LastRequest!.PlanHash);
        var record = result.Transaction.Transaction!;
        Assert.True(record.Authorization!.Consumed);
        Assert.Equal(plan.Snapshot.Observation.SelectedPackage, result.Receipt.Package);
        Assert.NotNull(result.Receipt.PostVerification);
        var history = await scope.Create(new MemoryStore(scope.Root).CreateDebloatJournal()).LoadHistoryAsync();
        Assert.Empty(history.Warnings);
        var item = Assert.Single(history.Items);
        Assert.Equal(plan.Snapshot.Observation.SelectedPackage, item.Receipt!.Package);
        Assert.Equal(scenario, item.Snapshot.Scenario);
        Assert.Equal(record.Succeeded, item.Transaction.Succeeded);
        var report = File.ReadAllText(item.ReportPath);
        Assert.Contains("HRESULT", report, StringComparison.Ordinal);
        Assert.Contains(DebloatLimits.RecoveryWarning, report, StringComparison.Ordinal);
        Assert.Contains("Pós-verificação", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelBeforeStartHasNoEffectOrIntent()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan), cancellationToken: cancellation.Token);
        Assert.Equal(DebloatRemovalState.CancelledBeforeStart, result.State);
        Assert.Equal(0, scope.Adapter.ExecutionCount);
        Assert.Empty((await scope.Journal.LoadTransactionsAsync()).Transactions);
    }

    [Fact]
    public async Task CancellationAfterDurableStartIntentButBeforeExecutorPreventsEffect()
    {
        using var scope = new DebloatRemovalTestScope();
        using var cancellation = new CancellationTokenSource();
        var spy = new RemovalJournalSpy(scope.Journal)
        {
            AfterAppend = e => { if (e.State == TransactionState.ExecutionStarted) cancellation.Cancel(); }
        };
        var manager = scope.Create(spy); var plan = await manager.PrepareAsync();
        var result = await manager.ExecuteAsync(plan, manager.Authorize(plan, plan.Confirmation), cancellationToken: cancellation.Token);
        Assert.Equal(0, scope.Adapter.ExecutionCount);
        Assert.Equal(DebloatRemovalState.CancelledBeforeStart, result.State);
        Assert.False(result.RequiresIntervention);
        Assert.False(Assert.Single((await scope.Journal.LoadTransactionsAsync()).Transactions).IsIndeterminate);
    }

    [Fact]
    public async Task CancellationAfterInitiationCannotStopOrFalselyCancelRemoval()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        using var cancellation = new CancellationTokenSource();
        scope.Adapter.AfterStart = cancellation.Cancel;
        var progress = new CaptureProgress();
        var result = await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan), progress: progress, cancellationToken: cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Succeeded);
        Assert.Contains(progress.Values, p => p.Phase == DebloatRemovalPhase.Started);
        Assert.Contains(progress.Values, p => p.Phase == DebloatRemovalPhase.Completed);
        Assert.All(progress.Values, p => { Assert.Null(p.ReportedPercentage); Assert.Equal(plan.Simulation.Plan.TransactionId, p.OperationId); });
        Assert.DoesNotContain(result.Transaction.Transaction!.Events, e => e.State == TransactionState.Cancelled);
    }

    [Theory]
    [InlineData(TransactionState.Planned)]
    [InlineData(TransactionState.Authorized)]
    [InlineData(TransactionState.ExecutionStarted)]
    public async Task PersistenceFailureBeforeEffectPreventsExecutor(TransactionState fail)
    {
        using var scope = new DebloatRemovalTestScope();
        var spy = new RemovalJournalSpy(scope.Journal) { FailBefore = fail };
        var manager = scope.Create(spy);
        var plan = await manager.PrepareAsync();
        var result = await manager.ExecuteAsync(plan, manager.Authorize(plan, plan.Confirmation));
        Assert.False(result.Succeeded);
        Assert.Equal(0, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public async Task MissingMetadataSnapshotPreventsEffect()
    {
        using var scope = new DebloatRemovalTestScope();
        var manager = scope.Create(new RemovalJournalSpy(scope.Journal) { FailBackup = true });
        var plan = await manager.PrepareAsync();
        var result = await manager.ExecuteAsync(plan, manager.Authorize(plan, plan.Confirmation));
        Assert.Equal(0, scope.Adapter.ExecutionCount);
        Assert.Contains(BlockerCode.BackupFailed, result.Transaction.Blockers);
    }

    [Fact]
    public async Task JournalFailureAfterEffectLeavesInterventionAndNoSuccess()
    {
        using var scope = new DebloatRemovalTestScope();
        var manager = scope.Create(new RemovalJournalSpy(scope.Journal) { FailBefore = TransactionState.ExecutionCompleted });
        var plan = await manager.PrepareAsync();
        var result = await manager.ExecuteAsync(plan, manager.Authorize(plan, plan.Confirmation));
        Assert.Equal(1, scope.Adapter.ExecutionCount);
        Assert.False(result.Succeeded);
        Assert.True(result.RequiresIntervention);
        var record = Assert.Single((await scope.Journal.LoadTransactionsAsync()).Transactions);
        Assert.True(record.IsIndeterminate);
    }

    [Theory]
    [InlineData("publisher")]
    [InlineData("updated")]
    [InlineData("revision")]
    [InlineData("dependencies")]
    [InlineData("partial")]
    public async Task ChangedObservationInvalidatesAuthorization(string change)
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare(); var token = scope.Authorize(plan);
        var o = scope.Adapter.Observation;
        var p = o.Packages[0];
        scope.Adapter.Observation = change switch
        {
            "publisher" => o with { Packages = [p with { Publisher = "CN=Changed" }] },
            "updated" => o with { Packages = [p with { Version = "2.0.0.0", PackageFullName = p.PackageFullName.Replace("1.0.0.0", "2.0.0.0", StringComparison.Ordinal) }] },
            "revision" => o with { InventoryRevision = "fixture-revision-2" },
            "dependencies" => o with { Dependencies = o.Dependencies with { Reverse = [o.SelectedPackage] } },
            "partial" => o with { Completeness = DebloatObservationCompleteness.Partial },
            _ => throw new InvalidOperationException()
        };
        var result = await scope.Manager.ExecuteAsync(plan, token);
        Assert.False(result.Succeeded); Assert.Equal(0, scope.Adapter.ExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedPlanCannotBeAuthorizedEvenWithRecomputedHash(bool recompute)
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        var forged = plan with { Snapshot = plan.Snapshot with { Observation = plan.Snapshot.Observation with { InventoryRevision = "changed" } } };
        if (recompute) forged = forged with { Hash = DebloatRemovalHashing.PlanHash(forged) };
        Assert.Throws<InvalidOperationException>(() => scope.Manager.Authorize(forged, plan.Confirmation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Manager.ExecuteAsync(forged, scope.Authorize(plan)));
        Assert.Equal(0, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public async Task ReturnedCollectionsCannotMutatePrivatelyPreparedPlan()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        var consequences = Assert.IsType<List<string>>(plan.Snapshot.Observation.Consequences);
        consequences[0] = "Tampered";
        Assert.Throws<InvalidOperationException>(() => scope.Authorize(plan));
    }

    [Fact]
    public async Task ExpiredAuthorizationNeverExecutes()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare(); var token = scope.Authorize(plan);
        scope.Time.Now += TimeSpan.FromMinutes(3);
        var result = await scope.Manager.ExecuteAsync(plan, token);
        Assert.False(result.Succeeded); Assert.Equal(0, scope.Adapter.ExecutionCount);
        Assert.Contains(BlockerCode.AuthorizationExpired, result.Transaction.Blockers);
    }

    [Fact]
    public async Task AuthorizationIsSingleUseAndCannotCrossPlans()
    {
        using var scope = new DebloatRemovalTestScope();
        var first = await scope.Prepare(); var token = scope.Authorize(first);
        var second = await scope.Prepare();
        Assert.False((await scope.Manager.ExecuteAsync(second, token)).Succeeded);
        Assert.True((await scope.Manager.ExecuteAsync(first, token)).Succeeded);
        Assert.False((await scope.Manager.ExecuteAsync(first, token)).Succeeded);
        Assert.Equal(1, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public async Task UnresolvedEffectsBlockAnotherPlanOnSameTarget()
    {
        using var scope = new DebloatRemovalTestScope();
        var first = await scope.Prepare(DebloatRemovalScenario.PartialFailure);
        await scope.Manager.ExecuteAsync(first, scope.Authorize(first));
        var second = await scope.Prepare();
        var result = await scope.Manager.ExecuteAsync(second, scope.Authorize(second));
        Assert.Contains(BlockerCode.IndeterminateState, result.Transaction.Blockers);
        Assert.Equal(1, scope.Adapter.ExecutionCount);
    }

    [Theory]
    [InlineData(DebloatRemovalScenario.Unverifiable)]
    [InlineData(DebloatRemovalScenario.PostVerificationFailed)]
    public async Task FailedPostVerificationKeepsTargetBlockedAfterReopening(DebloatRemovalScenario scenario)
    {
        using var scope = new DebloatRemovalTestScope();
        var first = await scope.Prepare(scenario);
        await scope.Manager.ExecuteAsync(first, scope.Authorize(first));
        var manager = scope.Create(new MemoryStore(scope.Root).CreateDebloatJournal());
        var second = await manager.PrepareAsync();
        var result = await manager.ExecuteAsync(second, manager.Authorize(second, second.Confirmation));
        Assert.Contains(BlockerCode.IndeterminateState, result.Transaction.Blockers);
        Assert.Equal(1, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public async Task IntentAloneReopensAsUnfinishedNeverAsRemoved()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        var p = plan.Simulation.Plan;
        await scope.Journal.AppendEventAsync(new(1, p.TransactionId, 1, TransactionState.Planned,
            scope.Time.GetUtcNow(), p.OperationId, p.OperationVersion, p.CatalogVersion, p.PlanHash,
            "Intenção órfã — fixture", null, p, null, null, null, null, null, null, []));
        var reopened = await scope.Create().LoadHistoryAsync();
        var item = Assert.Single(reopened.Items);
        Assert.False(item.Transaction.Succeeded); Assert.True(item.Transaction.IsIndeterminate);
        Assert.Null(item.Receipt);
    }

    [Fact]
    public async Task CorruptionPreservesFileAndExcludesTransaction()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan));
        var path = Directory.GetFiles(Path.Combine(scope.Root, "memory", "debloat"), "*-ExecutionCompleted.md", SearchOption.AllDirectories).Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("<!-- sha256:", "<!-- sha256:broken", StringComparison.Ordinal));
        var history = await scope.Create().LoadHistoryAsync();
        Assert.Empty(history.Items); Assert.NotEmpty(history.Warnings); Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SqliteRebuildUsesMarkdownAndDoesNotAddDatabaseOrSchema()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan));
        SqliteConnection.ClearAllPools();
        var database = Directory.GetFiles(scope.Root, "*.db", SearchOption.AllDirectories).Single();
        File.Delete(database);
        var history = await scope.Create().LoadHistoryAsync();
        Assert.Single(history.Items); Assert.Empty(history.Warnings);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version";
        Assert.Equal(12L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM DebloatTransactions";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM Transactions";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task LegacyEventsOmitNewNullFieldForIdempotentCompatibility()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare(); var p = plan.Simulation.Plan;
        var entry = new TransactionEventRecord(1, p.TransactionId, 1, TransactionState.Planned,
            scope.Time.GetUtcNow(), p.OperationId, p.OperationVersion, p.CatalogVersion, p.PlanHash,
            null, null, p, null, null, null, null, null, null, []);
        var json = JsonSerializer.Serialize(entry, AuditJson.Options);
        Assert.DoesNotContain("RemovalReceipt", json, StringComparison.Ordinal);
        var roundtrip = JsonSerializer.Deserialize<TransactionEventRecord>(json, AuditJson.Options)!;
        await scope.Journal.AppendEventAsync(roundtrip);
        await scope.Journal.AppendEventAsync(entry);
        Assert.Single((await scope.Journal.LoadTransactionsAsync()).Transactions);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("enum")]
    [InlineData("percentage")]
    [InlineData("native")]
    [InlineData("missing-error-code")]
    public async Task MalformedOrUnconfirmedExecutorResultNeverBecomesSuccess(string mutation)
    {
        using var scope = new DebloatRemovalTestScope();
        var manager = scope.Create(executor: new ChangedReceiptExecutor(scope.Adapter, mutation));
        var plan = await manager.PrepareAsync();
        var result = await manager.ExecuteAsync(plan, manager.Authorize(plan, plan.Confirmation));
        Assert.False(result.Succeeded); Assert.True(result.RequiresIntervention);
        Assert.Equal(1, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public async Task JournalRejectsReceiptForDifferentPackageWithoutReplacingOriginal()
    {
        using var scope = new DebloatRemovalTestScope(); var plan = await scope.Prepare();
        var result = await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan));
        var entry = result.Transaction.Transaction!.Events.Single(e => e.RemovalReceipt is not null);
        var altered = entry with { RemovalReceipt = entry.RemovalReceipt! with
            { Package = entry.RemovalReceipt.Package with { Publisher = "CN=Other" } } };
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(altered));
        var record = Assert.Single((await scope.Journal.LoadTransactionsAsync()).Transactions);
        Assert.True(record.Succeeded);
        Assert.Equal(plan.Snapshot.Observation.SelectedPackage,
            record.Events.Single(e => e.RemovalReceipt is not null).RemovalReceipt!.Package);
    }

    [Fact]
    public async Task UnknownSnapshotVersionIsRejectedBeforeIntentIsWritten()
    {
        using var scope = new DebloatRemovalTestScope(); var plan = await scope.Prepare();
        var p = plan.Simulation.Plan;
        var change = p.Changes.Single() with { Before = p.Changes.Single().Before with
            { Data = JsonSerializer.Serialize(plan.Snapshot with { SchemaVersion = 999 }) } };
        p = p with { Changes = [change] };
        p = p with { PlanHash = SafetyHashing.PlanHash(p.PlanId, p.TransactionId, p.OperationId, p.OperationVersion,
            p.CatalogVersion, p.Risk, p.Scope, p.Protection, p.ObservedStateHash, p.Changes) };
        var entry = new TransactionEventRecord(1, p.TransactionId, 1, TransactionState.Planned,
            scope.Time.GetUtcNow(), p.OperationId, p.OperationVersion, p.CatalogVersion, p.PlanHash,
            null, null, p, null, null, null, null, null, null, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(entry));
        Assert.Empty((await scope.Journal.LoadTransactionsAsync()).Transactions);
    }

    [Fact]
    public async Task PresentationUsesTypedPlanProgressHistoryAndExplicitLimitations()
    {
        using var scope = new DebloatRemovalTestScope(); var plan = await scope.Prepare();
        var result = await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan));
        var presentation = DebloatRemovalPresentation.From(plan,
            new(plan.Simulation.Plan.TransactionId, DebloatRemovalPhase.ProgressReported, null, "fixture"), result,
            await scope.Manager.LoadHistoryAsync());
        Assert.Contains(DebloatLimits.SyntheticWarning, presentation.Plan, StringComparison.Ordinal);
        Assert.Contains("Reversas", presentation.Dependencies, StringComparison.Ordinal);
        Assert.Contains("Compartilhadas", presentation.Dependencies, StringComparison.Ordinal);
        Assert.Contains("sem percentual", presentation.Progress, StringComparison.Ordinal);
        Assert.Contains("não pode ser cancelada", presentation.Progress, StringComparison.Ordinal);
        Assert.Contains(DebloatLimits.RecoveryWarning, presentation.Limitations, StringComparison.Ordinal);
        Assert.Contains("VerifiedSynthetic", presentation.Result, StringComparison.Ordinal);
        Assert.Contains("VerificationPassed", presentation.History, StringComparison.Ordinal);
    }

    private sealed class CaptureProgress : IProgress<DebloatRemovalProgress>
    {
        public List<DebloatRemovalProgress> Values { get; } = [];
        public void Report(DebloatRemovalProgress value) => Values.Add(value);
    }

    private sealed class ChangedReceiptExecutor(IDebloatRemovalExecutor inner, string mutation) : IDebloatRemovalExecutor
    {
        public bool SyntheticOnly => true;
        public bool Available => true;
        public async Task<DebloatRemovalReceipt> ExecuteAsync(DebloatRemovalRequest request,
            IProgress<DebloatRemovalProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var r = await inner.ExecuteAsync(request, progress, cancellationToken);
            return mutation switch
            {
                "identity" => r with { Package = r.Package with { Publisher = "CN=Other" } },
                "enum" => r with { Phase = (DebloatRemovalPhase)999 },
                "percentage" => r with { Progress = [new(request.OperationId, DebloatRemovalPhase.ProgressReported, 101, "invalid")] },
                "native" => r with { Synthetic = false },
                "missing-error-code" => r with { HResult = null },
                _ => throw new InvalidOperationException()
            };
        }
    }
}
