using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RecoveryCenterPersistenceTests
{
    [Fact]
    public async Task UninitializedRootHasFourExplicitSourcesAndCreatesNoArtifacts()
    {
        using var scope = new SafetyScope();
        var untouchedRoot = Path.Combine(scope.Root, "uninitialized");
        var sources = await new MemoryStore(untouchedRoot).ReadRecoveryHistoryAsync();

        Assert.Equal(4, sources.Count);
        Assert.All(sources, source =>
        {
            Assert.Empty(source.Transactions); Assert.Empty(source.Repairs);
            Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
            Assert.Contains(source.Warnings, warning => warning.Contains("SQLite", StringComparison.Ordinal));
        });
        Assert.False(Directory.Exists(untouchedRoot));
    }

    [Fact]
    public async Task HealthyTweakHistoryIsReadWithoutChangingAnyArtifact()
    {
        using var scope = new SafetyScope();
        var transaction = await Tweak(scope);
        var before = Snapshot(scope.Root);

        var sources = await new MemoryStore(scope.Root).ReadRecoveryHistoryAsync();

        var tweaks = Source(sources, RecoveryModule.Tweaks);
        Assert.Equal(RecoveryEvidenceStatus.Complete, tweaks.Status);
        Assert.Empty(tweaks.Warnings);
        Assert.Equal(transaction.TransactionId, Assert.Single(tweaks.Transactions).TransactionId);
        Assert.True(tweaks.Transactions[0].Succeeded);
        Unchanged(before, scope.Root);
    }

    [Fact]
    public async Task AllFourDurableHistoriesRemainDistinctAfterReopening()
    {
        using var tweaks = new SafetyScope();
        var tweak = await Tweak(tweaks);
        using var debloat = DebloatScope();
        var debloatTransaction = await debloat.ExecuteAsync("DBLT-TEST-T4A");
        using var removal = new DebloatRemovalTestScope();
        var plan = await removal.Prepare();
        var removalResult = await removal.Manager.ExecuteAsync(plan, removal.Authorize(plan));
        using var repair = new RepairScope();
        var repairResult = await repair.ExecuteAsync(RepairCatalog.SfcVerifyId);
        CopyHistory(debloat.Root, tweaks.Root); CopyHistory(removal.Root, tweaks.Root); CopyHistory(repair.Root, tweaks.Root);
        await tweaks.Store.LoadTransactionsAsync();
        await tweaks.Store.CreateDebloatJournal().LoadTransactionsAsync();
        await tweaks.Store.LoadRepairsAsync();
        var before = Snapshot(tweaks.Root);

        var sources = await new MemoryStore(tweaks.Root).ReadRecoveryHistoryAsync();

        Assert.Equal(tweak.TransactionId, Assert.Single(Source(sources, RecoveryModule.Tweaks).Transactions).TransactionId);
        Assert.Equal(debloatTransaction.Transaction!.TransactionId,
            Assert.Single(Source(sources, RecoveryModule.Debloat).Transactions).TransactionId);
        Assert.Equal(removalResult.Transaction.Transaction!.TransactionId,
            Assert.Single(Source(sources, RecoveryModule.ControlledRemoval).Transactions).TransactionId);
        Assert.Equal(repairResult.Record!.Id, Assert.Single(Source(sources, RecoveryModule.WindowsRepair).Repairs).Id);
        Assert.All(sources, source => Assert.DoesNotContain(source.Warnings, w => w.Contains("SQLite", StringComparison.Ordinal)));
        Unchanged(before, tweaks.Root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptSqliteDoesNotHideMarkdownOrRebuildTheIndex(bool corrupt)
    {
        using var scope = new SafetyScope();
        var transaction = await Tweak(scope);
        var database = Path.Combine(scope.Root, "data", "sentinelcore.db");
        if (corrupt) File.WriteAllText(database, "índice corrompido de fixture"); else File.Delete(database);
        var before = Snapshot(scope.Root);

        var sources = await scope.Store.ReadRecoveryHistoryAsync();

        var tweaks = Source(sources, RecoveryModule.Tweaks);
        Assert.Equal(transaction.TransactionId, Assert.Single(tweaks.Transactions).TransactionId);
        Assert.Equal(RecoveryEvidenceStatus.Partial, tweaks.Status);
        Assert.Contains(tweaks.Warnings, w => w.Contains("SQLite", StringComparison.Ordinal));
        Unchanged(before, scope.Root);
        Assert.Empty(Directory.GetFiles(Path.Combine(scope.Root, "data"), "*.corrupt-*"));
    }

    [Theory]
    [InlineData("DELETE FROM Transactions")]
    [InlineData("UPDATE Transactions SET State='Planned'")]
    [InlineData("UPDATE TransactionEvents SET PayloadHash='invalid'")]
    [InlineData("PRAGMA user_version=13")]
    public async Task IndexDivergenceIsVisibleAndNeverSilentlyReconciled(string sql)
    {
        using var scope = new SafetyScope();
        await Tweak(scope); ExecuteSql(scope.Root, sql);
        var before = Snapshot(scope.Root);

        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);

        Assert.Single(source.Transactions);
        Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
        Assert.Contains(source.Warnings, w => w.Contains("SQLite", StringComparison.Ordinal));
        Unchanged(before, scope.Root);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("unknown-version")]
    [InlineData("unknown-state")]
    [InlineData("duplicate")]
    [InlineData("gap")]
    public async Task InvalidJournalsArePreservedExcludedAndWarned(string fault)
    {
        using var scope = new SafetyScope();
        var transaction = await Tweak(scope);
        var journal = Journal(scope.Root, "transactions", transaction.TransactionId);
        var first = Path.Combine(journal, "0001-Planned.md");
        switch (fault)
        {
            case "corrupt": File.WriteAllText(first, File.ReadAllText(first).Replace("\"FormatVersion\": 1", "\"FormatVersion\": 2")); break;
            case "unknown-version": RewritePayload(first, payload => payload["FormatVersion"] = 99); break;
            case "unknown-state": RewritePayload(first, payload => payload["State"] = 999); break;
            case "duplicate": File.Copy(first, Path.Combine(journal, "00001-Planned.md")); break;
            case "gap": File.Delete(Directory.GetFiles(journal, "0002-*.md").Single()); break;
        }
        var before = Snapshot(scope.Root);

        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);

        Assert.Empty(source.Transactions);
        Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
        Assert.NotEmpty(source.Warnings);
        Unchanged(before, scope.Root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptBackupPreservesOperationWithRecoveryEvidenceWarning(bool corrupt)
    {
        using var scope = new SafetyScope();
        var transaction = await Tweak(scope);
        if (corrupt) File.WriteAllText(transaction.BackupPath!, "backup inválido fixture");
        else File.Delete(transaction.BackupPath!);
        var before = Snapshot(scope.Root);

        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);

        var copy = Assert.Single(source.Transactions);
        Assert.NotNull(copy.Backup);
        Assert.Contains(copy.Limitations, w => w.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(source.Warnings, w => w.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
        Unchanged(before, scope.Root);
    }

    [Theory]
    [InlineData("risk")]
    [InlineData("scope")]
    [InlineData("kind")]
    [InlineData("target")]
    public async Task UnknownPlanEnumsAndRootsAreRejectedEvenWithRecomputedHash(string fault)
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var simulation = await scope.SimulateAsync("SYN-PREF-01"); var plan = simulation.Plan;
        await scope.Store.AppendEventAsync(new(1, plan.TransactionId, 1, TransactionState.Planned,
            scope.Time.Now, plan.OperationId, plan.OperationVersion, plan.CatalogVersion, plan.PlanHash,
            "fixture", null, plan, null, null, null, null, null, null, []));
        plan = fault switch
        {
            "risk" => plan with { Risk = (RiskLevel)999 },
            "scope" => plan with { Scope = (OperationScope)999 },
            "kind" => plan with { Changes = [plan.Changes[0] with { Kind = (ChangeKind)999 }] },
            _ => plan with { Changes = [plan.Changes[0] with
            {
                Target = plan.Changes[0].Target with { Root = "HKLM" },
                TargetIdentity = (plan.Changes[0].Target with { Root = "HKLM" }).Identity
            }] }
        };
        plan = plan with { PlanHash = SafetyHashing.PlanHash(plan.PlanId, plan.TransactionId,
            plan.OperationId, plan.OperationVersion, plan.CatalogVersion, plan.Risk, plan.Scope,
            plan.Protection, plan.ObservedStateHash, plan.Changes) };
        var first = Path.Combine(Journal(scope.Root, "transactions", plan.TransactionId), "0001-Planned.md");
        RewritePayload(first, payload =>
        {
            payload["Plan"] = JsonSerializer.SerializeToNode(plan, AuditJson.Options);
            payload["PlanHash"] = plan.PlanHash;
        });
        var before = Snapshot(scope.Root);

        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);

        Assert.Empty(source.Transactions); Assert.NotEmpty(source.Warnings);
        Unchanged(before, scope.Root);
    }

    [Fact]
    public async Task RecoveryWithUnknownOutcomeCannotBePresentedAsValid()
    {
        using var scope = new SafetyScope(); var transaction = await Tweak(scope);
        await scope.Recovery.RollbackAsync(transaction.TransactionId, new RecoveryOptions("confirmacao-teste"));
        var journal = Journal(scope.Root, "transactions", transaction.TransactionId);
        var final = Directory.GetFiles(journal, "*-RecoveryCompleted.md").Single();
        RewritePayload(final, payload => payload["Recovery"]!["Outcome"] = 999);
        var before = Snapshot(scope.Root);
        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);
        Assert.Empty(source.Transactions); Assert.NotEmpty(source.Warnings);
        Unchanged(before, scope.Root);
    }

    [Theory]
    [InlineData("partial-marked-full")]
    [InlineData("failure-marked-full")]
    [InlineData("completed-not-full")]
    [InlineData("unverified-step")]
    [InlineData("missing-steps")]
    [InlineData("foreign-target")]
    [InlineData("conflict")]
    [InlineData("error")]
    [InlineData("failed-event-marked-full")]
    public async Task HashValidContradictoryRecoveryNeverResolvesTheOperation(string fault)
    {
        using var scope = new SafetyScope(); var transaction = await Tweak(scope);
        var recovered = await scope.Recovery.RollbackAsync(transaction.TransactionId, new RecoveryOptions("confirmacao-teste"));
        Assert.True(recovered.FullyRecovered);
        var journal = Journal(scope.Root, "transactions", transaction.TransactionId);
        var final = Directory.GetFiles(journal, "*-RecoveryCompleted.md").Single();
        RewritePayload(final, payload =>
        {
            var recovery = payload["Recovery"]!;
            switch (fault)
            {
                case "partial-marked-full": recovery["Outcome"] = "PartiallyCompleted"; break;
                case "failure-marked-full": recovery["Outcome"] = "Failed"; break;
                case "completed-not-full": recovery["FullyRecovered"] = false; break;
                case "unverified-step": recovery["Steps"]![0]!["Verified"] = false; break;
                case "missing-steps": recovery["Steps"] = new JsonArray(); break;
                case "foreign-target": recovery["Steps"]![0]!["TargetIdentity"] = "Synthetic\\Foreign#Value"; break;
                case "conflict": recovery["Conflicts"] = JsonSerializer.SerializeToNode(new[]
                {
                    new RecoveryConflict(transaction.Plan!.Changes[0].TargetIdentity,
                        RecoveryConflictKind.StateChangedAfterTransaction, null, null, null, "fixture conflitante")
                }, AuditJson.Options); break;
                case "error": recovery["Error"] = "falha de verificação registrada"; break;
                case "failed-event-marked-full": payload["State"] = "RecoveryFailed"; break;
            }
        });
        if (fault == "failed-event-marked-full")
        {
            var renamed = final.Replace("RecoveryCompleted.md", "RecoveryFailed.md", StringComparison.Ordinal);
            File.Move(final, renamed); final = renamed;
        }
        var before = Snapshot(scope.Root);
        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);
        Assert.Empty(source.Transactions); Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
        Assert.Contains(source.Warnings, warning => warning.Contains("recuperação", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(final)); Unchanged(before, scope.Root);
    }

    [Theory]
    [InlineData("passed-result-false")]
    [InlineData("missing-steps")]
    [InlineData("unverified-step")]
    [InlineData("foreign-target")]
    [InlineData("failed-event-passed-result")]
    [InlineData("missing-post-hash")]
    public async Task HashValidContradictoryVerificationNeverAppearsAsSuccess(string fault)
    {
        using var scope = new SafetyScope(); var transaction = await Tweak(scope);
        var journal = Journal(scope.Root, "transactions", transaction.TransactionId);
        var final = Directory.GetFiles(journal, "*-VerificationPassed.md").Single();
        RewritePayload(final, payload =>
        {
            var verification = payload["Verification"]!;
            switch (fault)
            {
                case "passed-result-false": verification["Passed"] = false; break;
                case "missing-steps": verification["Steps"] = new JsonArray(); break;
                case "unverified-step": verification["Steps"]![0]!["Verified"] = false; break;
                case "foreign-target": verification["Steps"]![0]!["TargetIdentity"] = "Synthetic\\Foreign#Value"; break;
                case "failed-event-passed-result": payload["State"] = "VerificationFailed"; break;
                case "missing-post-hash": verification["PostStateHash"] = ""; break;
            }
        });
        if (fault == "failed-event-passed-result")
        {
            var renamed = final.Replace("VerificationPassed.md", "VerificationFailed.md", StringComparison.Ordinal);
            File.Move(final, renamed); final = renamed;
        }
        var before = Snapshot(scope.Root);
        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);
        Assert.Empty(source.Transactions); Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
        Assert.Contains(source.Warnings, warning => warning.Contains("verificação", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(final)); Unchanged(before, scope.Root);
    }

    [Fact]
    public async Task SameRecoveryIdentityWithDifferentContentIsRejectedAndPreserved()
    {
        using var scope = new SafetyScope(); var transaction = await Tweak(scope);
        var first = await scope.Recovery.RollbackAsync(transaction.TransactionId, new RecoveryOptions("confirmacao-teste"));
        var second = await scope.Recovery.RollbackAsync(transaction.TransactionId, new RecoveryOptions("confirmacao-teste"));
        Assert.True(first.FullyRecovered); Assert.True(second.FullyRecovered);
        var journal = Journal(scope.Root, "transactions", transaction.TransactionId);
        var final = Directory.GetFiles(journal, "*-RecoveryCompleted.md").Order(StringComparer.Ordinal).Last();
        RewritePayload(final, payload => payload["Recovery"]!["RecoveryId"] = first.RecoveryId.ToString("D"));
        var before = Snapshot(scope.Root);
        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);
        Assert.Empty(source.Transactions);
        Assert.Contains(source.Warnings, warning => warning.Contains("conteúdo divergente", StringComparison.Ordinal));
        Unchanged(before, scope.Root);
    }

    [Fact]
    public async Task CompleteAndAlreadyRestoredRecoveriesRemainValidWithTheirOwnEvidence()
    {
        using var scope = new SafetyScope(); var transaction = await Tweak(scope);
        await scope.Recovery.RollbackAsync(transaction.TransactionId, new RecoveryOptions("confirmacao-teste"));
        await scope.Recovery.RollbackAsync(transaction.TransactionId, new RecoveryOptions("confirmacao-teste"));
        var before = Snapshot(scope.Root);
        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);
        var copy = Assert.Single(source.Transactions);
        Assert.Empty(source.Warnings); Assert.Equal(RecoveryEvidenceStatus.Complete, source.Status);
        Assert.False(copy.IsIndeterminate);
        Assert.Equal(2, copy.Events.Count(entry => entry.State == TransactionState.RecoveryCompleted));
        Unchanged(before, scope.Root);
    }

    [Fact]
    public async Task MissingJournalDirectoryIsNotAnEmptySuccessfulHistory()
    {
        using var scope = new SafetyScope();
        var transaction = await Tweak(scope);
        Directory.Delete(Journal(scope.Root, "transactions", transaction.TransactionId), true);
        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);
        Assert.Empty(source.Transactions);
        Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
        Assert.Contains(source.Warnings, w => w.Contains("sem diretório de journal", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndStaleReportsAreVisibleAndNeverRegenerated(bool stale)
    {
        using var scope = new SafetyScope();
        var transaction = await Tweak(scope);
        var report = scope.Store.GetTransactionReportPath(transaction.TransactionId);
        if (stale) File.WriteAllText(report, "projeção antiga"); else File.Delete(report);
        var before = Snapshot(scope.Root);

        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.Tweaks);

        Assert.Single(source.Transactions);
        Assert.Contains(source.Warnings, w => w.Contains("Projeção/relatório", StringComparison.Ordinal));
        Unchanged(before, scope.Root);
    }

    [Fact]
    public async Task InaccessibleRootAndBusyStoreAreUnavailableRatherThanZero()
    {
        using var scope = new SafetyScope();
        await Tweak(scope);
        using var locked = new FileStream(Path.Combine(scope.Root, "data", "store.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var sources = await scope.Store.ReadRecoveryHistoryAsync();
        Assert.All(sources, source =>
        {
            Assert.Equal(RecoveryEvidenceStatus.Unavailable, source.Status);
            Assert.Contains(source.Warnings, w => w.Contains("Não equivale a histórico vazio", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task RepairWithoutFinalRecordIsAnUnfinishedIntentAndCorruptionIsPreserved()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.DismCheckHealthId);
        var record = result.Record!;
        var recordPath = Path.Combine(scope.Root, "memory", "repairs", $"{record.Id:D}", "record.md");
        File.Delete(recordPath);
        var before = Snapshot(scope.Root);
        var source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.WindowsRepair);
        Assert.Empty(source.Repairs);
        Assert.Equal(record.Id, Assert.Single(source.UnfinishedRepairs).Id);
        Unchanged(before, scope.Root);
        File.WriteAllText(recordPath, "registro corrompido");
        before = Snapshot(scope.Root);
        source = Source(await scope.Store.ReadRecoveryHistoryAsync(), RecoveryModule.WindowsRepair);
        Assert.Empty(source.Repairs); Assert.Empty(source.UnfinishedRepairs);
        Assert.Equal(RecoveryEvidenceStatus.Partial, source.Status);
        Assert.NotEmpty(source.Warnings);
        Unchanged(before, scope.Root);
    }

    [Theory]
    [InlineData(DebloatRemovalScenario.Unverifiable)]
    [InlineData(DebloatRemovalScenario.PostVerificationFailed)]
    public async Task RemovalPendingEffectRemainsBlockedAfterReadOnlyReopeningAndSqliteRebuild(DebloatRemovalScenario scenario)
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare(scenario);
        var result = await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan));
        var database = Path.Combine(scope.Root, "data", "sentinelcore.db"); File.Delete(database);
        var source = Source(await new MemoryStore(scope.Root).ReadRecoveryHistoryAsync(), RecoveryModule.ControlledRemoval);
        var transaction = Assert.Single(source.Transactions);
        Assert.Equal(result.Transaction.Transaction!.TransactionId, transaction.TransactionId);
        Assert.True(transaction.IsIndeterminate);
        Assert.Contains(transaction.Events, e => e.RemovalReceipt is { EffectMayHaveOccurred: true });
        Assert.False(File.Exists(database));
        await new MemoryStore(scope.Root).CreateDebloatJournal().LoadTransactionsAsync();
        source = Source(await new MemoryStore(scope.Root).ReadRecoveryHistoryAsync(), RecoveryModule.ControlledRemoval);
        Assert.True(Assert.Single(source.Transactions).IsIndeterminate);
        var manager = scope.Create(new MemoryStore(scope.Root).CreateDebloatJournal());
        var next = await manager.PrepareAsync(DebloatRemovalScenario.Success);
        var blocked = await manager.ExecuteAsync(next, manager.Authorize(next, next.Confirmation));
        Assert.Contains(BlockerCode.IndeterminateState, blocked.Transaction.Blockers);
        Assert.Equal(1, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public async Task CancellationIsPropagatedWithoutFilesystemSideEffects()
    {
        using var scope = new SafetyScope();
        var untouchedRoot = Path.Combine(scope.Root, "cancelled");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MemoryStore(untouchedRoot).ReadRecoveryHistoryAsync(cancellation.Token));
        Assert.False(Directory.Exists(untouchedRoot));
    }

    [Fact]
    public void ReportPathsUseOnlyClosedModulePartitions()
    {
        using var scope = new SafetyScope(); var id = Guid.NewGuid();
        Assert.Equal(scope.Store.GetTransactionReportPath(id), scope.Store.GetRecoveryReportPath(RecoveryModule.Tweaks, id));
        Assert.Equal(scope.Store.CreateDebloatJournal().GetTransactionReportPath(id), scope.Store.GetRecoveryReportPath(RecoveryModule.ControlledRemoval, id));
        Assert.Equal(scope.Store.GetRepairReportPath(id), scope.Store.GetRecoveryReportPath(RecoveryModule.WindowsRepair, id));
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.Store.GetRecoveryReportPath((RecoveryModule)999, id));
    }

    private static RecoveryHistorySource Source(IReadOnlyList<RecoveryHistorySource> sources, RecoveryModule module) =>
        Assert.Single(sources, source => source.Module == module);

    private static async Task<TransactionRecord> Tweak(SafetyScope scope)
    {
        scope.Seed("Preference.Test", "A");
        return (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
    }

    private static SafetyScope DebloatScope()
    {
        var operation = T1SyntheticCatalog.Create().Get("SYN-PREF-01") with { Id = "DBLT-TEST-T4A" };
        var scope = new SafetyScope(catalog: new OperationCatalog("debloat-test-t4a", [operation]));
        scope.Journal = scope.Store.CreateDebloatJournal(); scope.Rebuild(); scope.Seed("Preference.Test", "A");
        return scope;
    }

    private static string Journal(string root, string partition, Guid id) =>
        Path.Combine(root, "memory", partition, $"{id:D}", "journal");

    private static Dictionary<string, (byte[] Bytes, DateTime Updated)> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path,
            path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));

    private static void Unchanged(Dictionary<string, (byte[] Bytes, DateTime Updated)> before, string root)
    {
        var after = Snapshot(root); Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, original) in before)
        {
            Assert.Equal(original.Bytes, after[path].Bytes);
            Assert.Equal(original.Updated, after[path].Updated);
        }
    }

    private static void CopyHistory(string from, string to)
    {
        foreach (var partition in new[] { "memory", "reports" })
            foreach (var file in Directory.GetFiles(Path.Combine(from, partition), "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
            }
    }

    private static void ExecuteSql(string root, string sql)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "data", "sentinelcore.db"), Pooling = false }.ToString());
        database.Open(); using var command = database.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static void RewritePayload(string path, Action<JsonNode> edit)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        const string marker = "\n<!-- sentinelcore:payload:v1 -->\n```json\n";
        const string checksum = "\n```\n<!-- sha256:";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = text.LastIndexOf(checksum, StringComparison.Ordinal);
        var payload = JsonNode.Parse(text[start..end])!; edit(payload);
        var json = payload.ToJsonString();
        File.WriteAllText(path, text[..start] + json + checksum +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))) + " -->\n");
    }
}
