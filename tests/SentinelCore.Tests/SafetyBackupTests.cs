using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyBackupTests
{
    private static async Task<List<ObservedTargetState>> ObserveAsync(SafetyScope scope, OperationPlan plan)
    {
        var states = new List<ObservedTargetState>();
        foreach (var change in plan.Changes)
            states.Add(await scope.Targets.ReadAsync(change.Target));
        return states;
    }

    private static async Task<(SafetyScope Scope, OperationPlan Plan, List<ObservedTargetState> Observed)>
        PrepareAsync(string operationId = "SYN-PREF-01", string path = "Preference.Test", string value = "A")
    {
        var scope = new SafetyScope();
        scope.Seed(path, value);
        var simulation = await scope.Engine.SimulateAsync(operationId);
        Assert.True(simulation.CanExecute);
        return (scope, simulation.Plan, await ObserveAsync(scope, simulation.Plan));
    }

    [Fact]
    public async Task ExistingValueIsPreservedWithKindAndData()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var backup = new BackupService(scope.Store, scope.Time).Create(plan, observed, 1);

            Assert.True(backup.IsComplete);
            var entry = Assert.Single(backup.Entries);
            Assert.True(entry.TargetExists);
            Assert.True(entry.Value!.Exists);
            Assert.Equal("String", entry.Value.Kind);
            Assert.Equal("A", entry.Value.Data);
            Assert.Equal(SafetyHashing.EntryChecksum(entry), entry.Checksum);
            Assert.Equal(SafetyHashing.BackupContentHash(backup.Entries), backup.ContentHash);
        }
    }

    [Fact]
    public async Task AbsentTargetIsDistinctFromValueAbsent()
    {
        var (absentScope, absentPlan, absentObserved) = await PrepareAsync(value: "ignored");
        absentScope.SeedAbsent("Preference.Test");
        absentObserved = await ObserveAsync(absentScope, absentPlan);
        using (absentScope)
        {
            var absent = new BackupService(absentScope.Store, absentScope.Time)
                .Create(absentPlan, absentObserved, 1);
            var entry = Assert.Single(absent.Entries);
            Assert.False(entry.TargetExists);
            Assert.NotNull(entry.Value);
            Assert.False(entry.Value!.Exists);
        }

        var (withoutValueScope, plan, _) = await PrepareAsync();
        withoutValueScope.SeedTargetWithoutValue("Preference.Test");
        var withoutValue = await ObserveAsync(withoutValueScope, plan);
        using (withoutValueScope)
        {
            var backup = new BackupService(withoutValueScope.Store, withoutValueScope.Time)
                .Create(plan, withoutValue, 1);
            var entry = Assert.Single(backup.Entries);
            Assert.True(entry.TargetExists);
            Assert.NotNull(entry.Value);
            Assert.False(entry.Value!.Exists);
            Assert.True(backup.IsComplete);
        }
    }

    [Fact]
    public async Task EmptyContentDiffersFromMissingValue()
    {
        var (emptyScope, emptyPlan, _) = await PrepareAsync();
        emptyScope.SeedEmpty("Preference.Test");
        var emptyObserved = await ObserveAsync(emptyScope, emptyPlan);
        var emptyBackup = new BackupService(emptyScope.Store, emptyScope.Time)
            .Create(emptyPlan, emptyObserved, 1);

        var (absentScope, absentPlan, _) = await PrepareAsync();
        absentScope.SeedTargetWithoutValue("Preference.Test");
        var absentObserved = await ObserveAsync(absentScope, absentPlan);
        var absentBackup = new BackupService(absentScope.Store, absentScope.Time)
            .Create(absentPlan, absentObserved, 1);

        using (emptyScope)
        using (absentScope)
        {
            Assert.True(emptyBackup.Entries[0].Value!.Exists);
            Assert.Equal("", emptyBackup.Entries[0].Value!.Data);
            Assert.False(absentBackup.Entries[0].Value!.Exists);
            Assert.NotEqual(emptyBackup.ContentHash, absentBackup.ContentHash);
        }
    }

    [Fact]
    public async Task DifferentKindsArePreservedWithoutConversion()
    {
        var (scope, plan, _) = await PrepareAsync();
        scope.SeedKind("Preference.Test", "Integer", "42");
        var observed = await ObserveAsync(scope, plan);
        using (scope)
        {
            var backup = new BackupService(scope.Store, scope.Time).Create(plan, observed, 1);
            Assert.Equal("Integer", backup.Entries[0].Value!.Kind);
            Assert.Equal("42", backup.Entries[0].Value!.Data);
        }
    }

    [Fact]
    public async Task IncompleteBackupIsNotDeclaredRecoverable()
    {
        var (scope, plan, _) = await PrepareAsync();
        using (scope)
        {
            var unknown = new List<ObservedTargetState>
            {
                ObservedTargetState.Unknown(plan.Changes[0].TargetIdentity, scope.Time.Now, "indisponível")
            };
            var backup = new BackupService(scope.Store, scope.Time).Create(plan, unknown, 1);

            Assert.False(backup.IsComplete);
            Assert.NotEmpty(backup.Limitations);
            var validation = BackupService.ValidateBackup(backup, plan);
            Assert.False(validation.Valid);
            Assert.Equal(BackupIntegrity.Incomplete, validation.Integrity);
        }
    }

    [Fact]
    public async Task PersistedBackupRoundTripsAndIsImmutable()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var service = new BackupService(scope.Store, scope.Time);
            var persisted = await service.CreateAndPersistAsync(plan, observed, 1);
            var path = Path.Combine(scope.Root, "memory", "transactions", $"{plan.TransactionId:D}",
                "backup-01.md");

            Assert.True(File.Exists(path));
            var reread = await scope.Store.ReadBackupAsync(path);
            Assert.Equal(persisted.ContentHash, reread.ContentHash);
            Assert.True((await service.ReadAndValidateAsync(path, plan)).Valid);

            var again = await scope.Store.WriteBackupAsync(persisted);
            Assert.Equal(path, again);
        }
    }

    [Fact]
    public async Task ConflictingBackupContentForSameSequenceFails()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var service = new BackupService(scope.Store, scope.Time);
            var first = service.Create(plan, observed, 1);
            await scope.Store.WriteBackupAsync(first);
            scope.Seed("Preference.Test", "Z");
            var second = service.Create(plan, await ObserveAsync(scope, plan), 1);

            await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.WriteBackupAsync(second));
        }
    }

    [Fact]
    public async Task CorruptBackupIsDetected()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var service = new BackupService(scope.Store, scope.Time);
            await service.CreateAndPersistAsync(plan, observed, 1);
            var path = Path.Combine(scope.Root, "memory", "transactions", $"{plan.TransactionId:D}",
                "backup-01.md");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"A\"", "\"TAMPERED\""));

            await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.ReadBackupAsync(path));
            var validation = await service.ReadAndValidateAsync(path, plan);
            Assert.False(validation.Valid);
            Assert.Equal(BackupIntegrity.Corrupt, validation.Integrity);
        }
    }

    [Fact]
    public async Task HashDivergenceIsDetected()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var backup = new BackupService(scope.Store, scope.Time).Create(plan, observed, 1);
            var tampered = backup with { ContentHash = new string('0', 64) };

            var validation = BackupService.ValidateBackup(tampered, plan);

            Assert.False(validation.Valid);
            Assert.Equal(BackupIntegrity.Corrupt, validation.Integrity);
        }
    }

    [Fact]
    public async Task EntryChecksumDivergenceIsDetected()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var backup = new BackupService(scope.Store, scope.Time).Create(plan, observed, 1);
            var entry = backup.Entries[0] with { Checksum = new string('0', 64) };
            var tampered = backup with
            {
                Entries = [entry],
                ContentHash = SafetyHashing.BackupContentHash([entry])
            };

            var validation = BackupService.ValidateBackup(tampered, plan);

            Assert.False(validation.Valid);
            Assert.Equal(BackupIntegrity.Corrupt, validation.Integrity);
        }
    }

    [Fact]
    public async Task IdentityMismatchIsDetected()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var backup = new BackupService(scope.Store, scope.Time).Create(plan, observed, 1);
            var other = plan with { PlanId = Guid.NewGuid(), PlanHash = new string('A', 64) };

            var validation = BackupService.ValidateBackup(backup, other);

            Assert.False(validation.Valid);
            Assert.Equal(BackupIntegrity.IdentityMismatch, validation.Integrity);
        }
    }

    [Fact]
    public async Task TargetExistenceMismatchIsDetected()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var backup = new BackupService(scope.Store, scope.Time).Create(plan, observed, 1);
            var original = backup.Entries[0];
            var entry = original with { TargetExists = false };
            entry = entry with { Checksum = SafetyHashing.EntryChecksum(entry) };
            var tampered = backup with
            {
                Entries = [entry],
                ContentHash = SafetyHashing.BackupContentHash([entry])
            };

            var validation = BackupService.ValidateBackup(tampered, plan);

            Assert.False(validation.Valid);
            Assert.Equal(BackupIntegrity.IdentityMismatch, validation.Integrity);
        }
    }

    [Fact]
    public async Task MissingEntriesAreIncompleteNotCorrupt()
    {
        var descriptor = SafetyTestData.TwoTargetOperation();
        using var scope = new SafetyScope(catalog: new OperationCatalog("multi-v1", [descriptor]));
        scope.Seed("Preference.MultiA", "A");
        scope.Seed("Preference.MultiB", "B");
        var simulation = await scope.Engine.SimulateAsync(descriptor.Id);
        Assert.True(simulation.CanExecute);
        var observed = await ObserveAsync(scope, simulation.Plan);
        var backup = new BackupService(scope.Store, scope.Time).Create(simulation.Plan, observed, 1);
        var entry = backup.Entries[0];
        var partial = backup with
        {
            Entries = [entry],
            ContentHash = SafetyHashing.BackupContentHash([entry])
        };

        var validation = BackupService.ValidateBackup(partial, simulation.Plan);

        Assert.False(validation.Valid);
        Assert.Equal(BackupIntegrity.Incomplete, validation.Integrity);
    }

    [Fact]
    public async Task BackupPathOutsideDataRootIsRejected()
    {
        using var scope = new SafetyScope();
        var outside = Path.Combine(Path.GetTempPath(), "sentinel-rogue-" + Guid.NewGuid().ToString("N") + ".md");

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.ReadBackupAsync(outside));
    }

    [Fact]
    public async Task ContentHashIsDeterministic()
    {
        var (scope, plan, observed) = await PrepareAsync();
        using (scope)
        {
            var service = new BackupService(scope.Store, scope.Time);
            var first = service.Create(plan, observed, 1);
            var second = service.Create(plan, observed, 1);

            Assert.Equal(first.ContentHash, second.ContentHash);
            Assert.NotEqual(first.BackupId, second.BackupId);
        }
    }
}
