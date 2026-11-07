using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// U3: jornadas integradas entre módulos sobre UM armazenamento temporário real (MemoryStore), sempre com dados
/// artificiais. Nenhum teste consulta o computador, inicia coletor, ferramenta, elevação ou executor real.
/// </summary>
public sealed class U3JourneyTests
{
    private static readonly DateTimeOffset At = CorrelationSamples.Now.AddHours(-2);

    private static async Task<(MonitoringSession Session, MonitoringIncident? Incident)> RunMonitoring(
        MemoryStore store, bool cancel, bool recordIncident)
    {
        var blocking = new BlockingMonitoringSource();
        var runner = new MonitoringSessionRunner([blocking], store, TimeProvider.System, MonitoringSamples.FastLimits());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(1)),
            cancellationToken: cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        MonitoringIncident? incident = null;
        if (recordIncident)
            Assert.True(runner.TryRecordIncident(new MonitoringIncidentRequest(
                MonitoringIncidentCategory.BlackScreen, "tela preta artificial", DateTimeOffset.UtcNow.AddMinutes(-1)), out incident));
        if (cancel) cancellation.Cancel(); else blocking.Release.TrySetResult();
        return (await running, incident);
    }

    [Fact]
    public async Task FreshStoreOpensEveryHistoryEmptyAndNeverClaimsACompleteRecoveryHistory()
    {
        using var scope = new SafetyScope();
        var store = new MemoryStore(Path.Combine(scope.Root, "first-use"));

        Assert.Empty((await store.LoadAsync()).Audits);
        Assert.Empty((await store.LoadSnapshotsAsync()).Snapshots);
        Assert.Empty((await store.LoadInvestigationsAsync()).Investigations);
        Assert.Empty((await store.LoadSessionsAsync()).Sessions);
        Assert.Empty((await store.LoadNetworkSessionsAsync()).Sessions);
        Assert.Empty((await store.LoadGraphicsInvestigationsAsync()).Investigations);
        Assert.Empty((await store.LoadCorrelationInvestigationsAsync()).Investigations);
        Assert.Empty((await store.LoadMonitoringSessionsAsync()).Sessions);
        Assert.Empty((await store.LoadTransactionsAsync()).Transactions);

        var snapshot = await new RecoveryCenter(store).LoadAsync();
        Assert.Empty(snapshot.Operations);
        Assert.Equal(4, snapshot.Sources.Count);
        Assert.DoesNotContain(snapshot.Operations, item => item.Outcome == RecoveryOperationOutcome.Completed);
    }

    [Fact]
    public async Task DiagnosticHistoryPersistsAndReopensWithoutInventingMissingEvidence()
    {
        using var scope = new SafetyScope();
        var audit = CorrelationSamples.AuditWithBoot(At.AddHours(-3), At);
        var hardware = CorrelationSamples.Snapshot(At);
        await scope.Store.SaveAsync(audit);
        await scope.Store.SaveSnapshotAsync(hardware);

        var reopened = new MemoryStore(scope.Root);
        var audits = await reopened.LoadAsync();
        var snapshots = await reopened.LoadSnapshotsAsync();

        Assert.Equal(audit.Id, Assert.Single(audits.Audits).Id);
        Assert.Empty(audits.Warnings);
        Assert.Equal(hardware.Id, Assert.Single(snapshots.Snapshots).Id);
        Assert.Empty((await reopened.LoadInvestigationsAsync()).Investigations);
        Assert.Empty((await reopened.LoadSessionsAsync()).Sessions);
        Assert.Empty((await reopened.LoadNetworkSessionsAsync()).Sessions);
        Assert.Empty((await reopened.LoadGraphicsInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task InvestigationJourneyCorrelatesPersistedSourcesWithoutAssertingCausality()
    {
        using var scope = new SafetyScope();
        var store = scope.Store;
        var events = await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 9101, at: At)],
            reports: [GraphicsSamples.LiveKernel("wer-lk-117", "117", at: At.AddSeconds(60))]);
        var graphics = await CorrelationSamples.GraphicsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 9101, at: At)]);
        await store.SaveAsync(CorrelationSamples.AuditWithBoot(At.AddHours(-3), At));
        await store.SaveSnapshotAsync(CorrelationSamples.Snapshot(At));
        await store.SaveInvestigationAsync(events);
        await store.SaveSessionAsync(CorrelationSamples.Performance(fromUtc: At));
        await store.SaveNetworkSessionAsync(CorrelationSamples.Network(fromUtc: At.AddHours(-1)));
        await store.SaveGraphicsInvestigationAsync(graphics);

        var reader = new CorrelationEvidenceReader(store, store, store, store, store, store, monitoring: store);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.All | CorrelationSources.Monitoring));
        await store.SaveCorrelationInvestigationAsync(investigation);

        Assert.NotEmpty(investigation.Evidence);
        Assert.NotEmpty(investigation.Relations);
        Assert.All(investigation.Relations, relation => Assert.Equal(CausalityStatus.NotAssessed, relation.Causality));
        Assert.Contains(CorrelationGuidance.CausalityLimitation, investigation.Limitations);
        Assert.DoesNotContain(investigation.Sources, source =>
            source.Source == CorrelationSource.Events && source.RecordsUsed == 0);
        var reloaded = Assert.Single((await new MemoryStore(scope.Root).LoadCorrelationInvestigationsAsync()).Investigations);
        Assert.Equal(investigation.Id, reloaded.Id);
        Assert.Equal(investigation.Relations.Count, reloaded.Relations.Count);
        Assert.All(reloaded.Relations, relation => Assert.Equal(CausalityStatus.NotAssessed, relation.Causality));
    }

    [Fact]
    public async Task CorrelationWithMissingSourcesStatesTheGapInsteadOfCompleteCoverage()
    {
        using var scope = new SafetyScope();
        var reader = new CorrelationEvidenceReader(scope.Store, scope.Store, scope.Store, scope.Store,
            scope.Store, scope.Store, monitoring: scope.Store);

        var investigation = await new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock())
            .RunAsync(new CorrelationRequest(EventWindow.Last24Hours));

        Assert.Empty(investigation.Evidence);
        var relation = Assert.Single(investigation.Relations);
        Assert.Equal(CorrelationRelationKind.InsufficientEvidence, relation.Kind);
        Assert.Equal(CausalityStatus.NotAssessed, relation.Causality);
        Assert.Contains("NÃO AVALIÁVEL", relation.Statement, StringComparison.Ordinal);
        Assert.NotEqual(CorrelationCoverageStatus.Complete, investigation.Coverage.Status);
        Assert.NotEqual(CorrelationStatus.Completed, investigation.Status);
    }

    [Fact]
    public async Task MonitoringJourneyPersistsIncidentCancellationAndForwardsToCorrelationWithoutEvidenceInvention()
    {
        using var scope = new SafetyScope();
        var (completed, incident) = await RunMonitoring(scope.Store, cancel: false, recordIncident: true);

        // Fechamento antes do registro final (o chamador é quem o grava): o histórico só recupera os checkpoints.
        var closedEarly = await new MemoryStore(scope.Root).LoadMonitoringSessionsAsync();
        var recovered = Assert.Single(closedEarly.Sessions);
        Assert.Equal(MonitoringExecutionStatus.Interrupted, recovered.Status);
        Assert.Contains(closedEarly.Warnings, warning => warning.Contains("sem registro final", StringComparison.Ordinal));

        await scope.Store.SaveMonitoringSessionAsync(completed);
        var (cancelled, _) = await RunMonitoring(scope.Store, cancel: true, recordIncident: false);
        await scope.Store.SaveMonitoringSessionAsync(cancelled);

        var reopened = await new MemoryStore(scope.Root).LoadMonitoringSessionsAsync();
        Assert.True(reopened.Warnings.Count == 0, string.Join(" || ", reopened.Warnings));
        var saved = Assert.Single(reopened.Sessions, session => session.Id == completed.Id);
        Assert.Equal(MonitoringExecutionStatus.Completed, saved.Status);
        Assert.Equal(incident!.Id, Assert.Single(saved.Incidents).Id);
        var stopped = Assert.Single(reopened.Sessions, session => session.Id == cancelled.Id);
        Assert.Equal(MonitoringExecutionStatus.Cancelled, stopped.Status);
        Assert.NotEqual(MonitoringCoverageStatus.Complete, stopped.CoverageStatus);

        var reader = new CorrelationEvidenceReader(monitoring: scope.Store);
        var investigation = await new EvidenceCorrelationRunner(reader)
            .RunAsync(new CorrelationRequest(EventWindow.Last24Hours, Sources: CorrelationSources.Monitoring));
        Assert.DoesNotContain(investigation.Evidence, item =>
            item.Id.Contains(incident.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(CorrelationCoverageStatus.Complete, investigation.Coverage.Status);
    }

    [Fact]
    public async Task MonitoringSessionRefusedWithoutAuthorizationLeavesTheStoreUntouched()
    {
        using var scope = new SafetyScope();
        var runner = new MonitoringSessionRunner([new BlockingMonitoringSource()], scope.Store,
            TimeProvider.System, MonitoringSamples.FastLimits());

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, authorized: false)));

        Assert.Empty((await scope.Store.LoadMonitoringSessionsAsync()).Sessions);
    }

    [Fact]
    public async Task TweakJourneyThroughTheDistributionCompositionNeverReachesAWriteAndFiveGatesStayClosed()
    {
        using var scope = new SafetyScope { Executor = new DisabledOperationExecutor() };
        scope.Rebuild();
        scope.Seed("Preference.Test", "A");
        var tweaks = new TweakManager(scope.Catalog, scope.Engine, scope.Recovery, scope.Store, TweakExecutionMode.RealDisabled);

        Assert.False(tweaks.RealExecutionEnabled);
        var simulation = await tweaks.SimulateAsync("SYN-PREF-01");
        TransactionExecutionResult? result = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            var token = tweaks.Authorize(simulation, TweakManager.BuildConfirmationText(simulation.Plan));
            result = await tweaks.ExecuteAsync(simulation.Plan, token);
        });

        Assert.True(error is not null || result is { Succeeded: false }, "A execução não pode ser concluída com sucesso.");
        Assert.DoesNotContain(await tweaks.LoadHistoryAsync(), record => record.Succeeded);
        Assert.DoesNotContain((await new RecoveryCenter(scope.Store).LoadAsync()).Operations,
            item => item.Outcome == RecoveryOperationOutcome.Completed);
        Assert.False(RealTweakBuildGate.AllowsRealExecution([RealTweakBuildGate.EnableArgument]));
        Assert.False(RealRepairBuildGate.AllowsRealExecution([]));
        Assert.False(RealDebloatBuildGate.AllowsRealExecution());
        Assert.False(NativeDebloatInventoryGate.AllowsActivation());
        Assert.False(DebloatRemovalBuildGate.AllowsRealExecution());
    }

    [Fact]
    public async Task RepairAndRecoveryJourneyKeepsSuccessFailureAndInterruptionDistinct()
    {
        using var root = new RepairScope();
        using var success = new RepairScope(root.Store);
        await success.ExecuteAsync(RepairCatalog.SfcVerifyId);
        using var failure = new RepairScope(root.Store);
        await failure.ExecuteAsync(RepairCatalog.SfcRepairId, scenario: RepairFixtureScenario.RepairFailed);
        using var interrupted = new RepairScope(root.Store);
        await interrupted.ExecuteAsync(RepairCatalog.DismCheckHealthId, scenario: RepairFixtureScenario.Interrupted);
        var failingStore = new FailingRepairStore(root.Store) { FailRecordOnce = true };
        using var orphan = new RepairScope(failingStore);
        var (plan, token) = await orphan.PrepareAsync(RepairCatalog.SfcVerifyId);
        var lost = await orphan.Center.ExecuteAsync(plan, token, RepairFixtureScenario.SuccessNoIssues);
        Assert.False(lost.Persisted);

        var snapshot = await new RecoveryCenter(root.Store).LoadAsync();
        var summary = RecoveryCenter.Summarize(snapshot);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(4, snapshot.Operations.Count);
        Assert.Equal(1, snapshot.Operations.Count(item => item.Outcome == RecoveryOperationOutcome.Completed));
        Assert.Equal(1, summary.Completed);
        var orphanSource = Assert.Single(snapshot.Sources, source => source.Module == RecoveryModule.WindowsRepair);
        Assert.Equal(lost.Record!.Id, Assert.Single(orphanSource.UnfinishedRepairs).Id);
        var unfinished = Assert.Single(snapshot.Operations, item => item.Repair is null);
        Assert.NotEqual(RecoveryOperationOutcome.Completed, unfinished.Outcome);
        Assert.True(unfinished.RequiresIntervention);
        Assert.All(snapshot.Operations, item => Assert.NotEqual(RecoveryCapability.PotentiallyAvailable, item.Capability));
        Assert.All(snapshot.Operations.Where(item => item.Outcome != RecoveryOperationOutcome.Completed),
            item => Assert.True(item.RequiresIntervention, item.OperationId + " " + item.Outcome));
    }

    [Fact]
    public async Task BusyStoreIsUnavailableNotEmptyAndRecoversAfterTheLockIsReleased()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        await scope.ExecuteAsync("SYN-PREF-01");
        var center = new RecoveryCenter(scope.Store);
        var before = await center.LoadAsync();
        Assert.Single(before.Operations);

        IReadOnlyList<RecoveryHistorySource> blocked;
        using (new FileStream(Path.Combine(scope.Root, "data", "store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            blocked = await scope.Store.ReadRecoveryHistoryAsync();
        Assert.All(blocked, source => Assert.Equal(RecoveryEvidenceStatus.Unavailable, source.Status));
        Assert.All(blocked, source => Assert.Empty(source.Transactions));

        var after = await center.LoadAsync();
        Assert.Equal(before.Operations.Select(item => item.Id), after.Operations.Select(item => item.Id));
    }

    [Fact]
    public async Task CorruptedAndCancelledReadsPreserveWarningsAndNeverFabricateSuccess()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var good = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var journal = Path.Combine(scope.Root, "memory", "transactions", $"{good.TransactionId:D}", "journal");
        var damaged = Path.Combine(scope.Root, "memory", "transactions", Guid.NewGuid().ToString("D"), "journal");
        Directory.CreateDirectory(damaged);
        File.WriteAllText(Path.Combine(damaged, "0001-Planned.md"), "U3 CORRUPTED SYNTHETIC JOURNAL");
        var files = Directory.GetFiles(scope.Root, "*", SearchOption.AllDirectories).Length;

        var snapshot = await new RecoveryCenter(new MemoryStore(scope.Root)).LoadAsync();

        Assert.True(Directory.Exists(journal));
        Assert.NotEmpty(snapshot.Warnings);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(good.TransactionId, Assert.Single(snapshot.Operations).Id);
        Assert.Equal(files, Directory.GetFiles(scope.Root, "*", SearchOption.AllDirectories).Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RecoveryCenter(scope.Store).LoadAsync(cancelled.Token));
        Assert.Equal(files, Directory.GetFiles(scope.Root, "*", SearchOption.AllDirectories).Length);
    }
}
