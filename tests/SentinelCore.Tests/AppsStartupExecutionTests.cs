using System.Security.Cryptography;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.AppsStartup;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>Somente dados artificiais, adaptadores falsos e raiz temporária exclusiva. Nenhuma API Windows é chamada.</summary>
public sealed class AppsStartupExecutionTests
{
    private static StartupEntry Run() => new(AppsIdentity.Startup(StartupSourceKind.RegistryRun, AppsScope.CurrentUser,
        @"CurrentUser|Software\Microsoft\Windows\CurrentVersion\Run|Registry64|Fixture"), "Fixture", null,
        StartupSourceKind.RegistryRun, "fixture.exe --private=SECRET-FIXTURE", AppsScope.CurrentUser,
        StartupConfiguredState.Enabled, StartupRuntimeState.NotVerified, "Run", AppsDataOrigin.Synthetic)
        { RegistryIdentity = new("CurrentUser", RunKeyStartupProvider.RunPath, "Registry64", "Fixture", "ExpandString") };

    private static StartupChangeRequest Change(StartupEntry entry, StartupAction action) =>
        new(entry, action, entry.StateToken) { Confirmation = AppsOperationConfirmation.Startup(entry, action) };

    private static InstalledApplicationEntry Msi() => new(AppsIdentity.Application(InstalledApplicationKind.Msi,
        AppsScope.CurrentUser, "{11111111-1111-1111-1111-111111111111}"), "Fixture", "Fixture Publisher", "1.0",
        InstalledApplicationKind.Msi, "Uninstall", AppsScope.CurrentUser, null,
        "{11111111-1111-1111-1111-111111111111}", null, false, AppsDataOrigin.Synthetic);
    private static InstalledApplicationEntry Msix() => new(AppsIdentity.Application(InstalledApplicationKind.Msix,
        AppsScope.CurrentUser, "Fixture_1.0.0.0_x64__fixture"), "Fixture", "Fixture Publisher", "1.0.0.0",
        InstalledApplicationKind.Msix, "AppModel", AppsScope.CurrentUser, null, null,
        "Fixture_1.0.0.0_x64__fixture", false, AppsDataOrigin.Synthetic)
        { PackageFamilyName = "Fixture_fixture", PackageAvailable = true, PackageClassification = "Application" };
    private static UninstallRequest Uninstall(InstalledApplicationEntry entry) =>
        new(entry, entry.Kind == InstalledApplicationKind.Msi ? UninstallMethod.MsiProductCode : UninstallMethod.MsixPackageFullName, entry.StateToken)
        { Confirmation = AppsOperationConfirmation.Uninstall(entry, entry.Kind == InstalledApplicationKind.Msi ?
            UninstallMethod.MsiProductCode : UninstallMethod.MsixPackageFullName) };
    private static AppsOperationIntent Intent(Guid? id = null) => new(id ?? Guid.NewGuid(), 1, AppsOperationKind.MsiUninstall,
        AppsDataOrigin.Synthetic, AppsOperationIdentity.Target(Msi().StableId), AppsScope.CurrentUser, "Msi",
        DateTimeOffset.Parse("2026-11-15T00:00:00Z"), Msi().StateToken, "Installed", AppsOperationIdentity.Hash("fixture-confirmation"),
        null, null, "SentinelCore.AppsStartup/v1");

    [Fact]
    public async Task DurableJournalReopensWithIntentStartAndResult()
    {
        using var fixture = new Fixture();
        var intent = Intent();
        await fixture.Store.SaveAppsIntentAsync(intent, default);
        await fixture.Store.SaveAppsStartedAsync(intent.Id, intent.AtUtc, default);
        await fixture.Store.SaveAppsCompletionAsync(new(intent.Id, intent.AtUtc, AppsOperationOutcome.Succeeded, "verified"), default);
        var record = Assert.Single((await fixture.Reopen().ReadAppsOperationsAsync(default)).Records);
        Assert.Equal(intent, record.Intent);
        Assert.Equal(intent.AtUtc, record.StartedUtc);
        Assert.Equal(AppsOperationOutcome.Succeeded, record.Outcome);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "data", "sentinelcore.db"))); // no schema migration
    }

    [Fact]
    public async Task DuplicatePayloadIsIdempotentDifferentPayloadIsRejected()
    {
        using var fixture = new Fixture();
        var intent = Intent();
        await fixture.Store.SaveAppsIntentAsync(intent, default);
        await fixture.Store.SaveAppsIntentAsync(intent, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveAppsIntentAsync(intent with { Source = "Msix" }, default));
        Assert.Single((await fixture.Store.ReadAppsOperationsAsync(default)).Records);
    }

    [Fact]
    public async Task InterruptedIntentAndStartedOperationRemainIndeterminateAndBlockRetry()
    {
        foreach (var started in new[] { false, true })
        {
            using var fixture = new Fixture();
            var intent = Intent();
            await fixture.Store.SaveAppsIntentAsync(intent, default);
            if (started) await fixture.Store.SaveAppsStartedAsync(intent.Id, intent.AtUtc, default);
            Assert.Equal(AppsOperationOutcome.Indeterminate, Assert.Single((await fixture.Reopen().ReadAppsOperationsAsync(default)).Records).Outcome);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveAppsIntentAsync(Intent(), default));
        }
    }

    [Fact]
    public async Task FinalWithoutIntentAndSuccessWithoutStartAreRejected()
    {
        using var fixture = new Fixture();
        var intent = Intent();
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.SaveAppsCompletionAsync(
            new(intent.Id, intent.AtUtc, AppsOperationOutcome.Succeeded, "verified"), default));
        await fixture.Store.SaveAppsIntentAsync(intent, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveAppsCompletionAsync(
            new(intent.Id, intent.AtUtc, AppsOperationOutcome.Succeeded, "verified"), default));
    }

    [Fact]
    public async Task CorruptionAndUnknownContractsArePreservedAndBlockNewEffects()
    {
        using var fixture = new Fixture();
        var intent = Intent();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveAppsIntentAsync(intent with { ContractVersion = 2 }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveAppsIntentAsync(intent with { Kind = (AppsOperationKind)99 }, default));
        await fixture.Store.SaveAppsIntentAsync(intent, default);
        var path = Path.Combine(fixture.Root, "memory", "apps-startup", intent.Id.ToString("D"), "intent.md");
        File.AppendAllText(path, "fixture"); // checksum is over payload; replace its content to simulate corruption.
        File.WriteAllText(path, "corrupt-fixture");
        var history = await fixture.Store.ReadAppsOperationsAsync(default);
        Assert.NotEmpty(history.Warnings);
        Assert.Empty(history.Records);
        Assert.Equal("corrupt-fixture", File.ReadAllText(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveAppsIntentAsync(Intent(), default));
    }

    [Fact]
    public async Task LegacyHistoryRemainsUntouchedAndOriginsStayDistinct()
    {
        using var fixture = new Fixture();
        var legacy = Path.Combine(fixture.Root, "memory", "debloat", "fixture.md");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "legacy-fixture");
        var synthetic = Intent();
        await fixture.Store.SaveAppsIntentAsync(synthetic, default);
        await fixture.Store.SaveAppsIntentAsync(Intent() with { Origin = AppsDataOrigin.Operational, ApplicationIdentity = Msi().ProductCode }, default);
        Assert.Equal(2, (await fixture.Reopen().ReadAppsOperationsAsync(default)).Records.Count);
        Assert.Equal("legacy-fixture", File.ReadAllText(legacy));
    }

    [Fact]
    public void OperationLeaseSerializesDifferentStoreInstances()
    {
        using var fixture = new Fixture();
        using (fixture.Store.AcquireAppsOperationLease())
            Assert.Throws<IOException>(() => fixture.Reopen().AcquireAppsOperationLease());
        using var next = fixture.Reopen().AcquireAppsOperationLease();
    }

    [Fact]
    public async Task RunDisableAndReenableAfterReopeningRestoreExactUnexpandedContent()
    {
        using var fixture = new Fixture();
        var entry = Run();
        var registry = new FakeRun(entry);
        var executor = new RunStartupChangeExecutor(registry, fixture.Store);
        Assert.Equal(AppsOperationOutcome.Succeeded, (await executor.ExecuteAsync(Change(entry, StartupAction.Disable), default)).Outcome);
        Assert.False(registry.Value.Exists);
        var disabled = entry with { State = StartupConfiguredState.Disabled };
        var reopened = new RunStartupChangeExecutor(registry, fixture.Reopen());
        Assert.Equal(AppsOperationOutcome.Succeeded, (await reopened.ExecuteAsync(Change(disabled, StartupAction.Enable), default)).Outcome);
        Assert.Equal(entry.Reference, registry.Value.Content);
        Assert.Equal("ExpandString", registry.Value.ValueKind);
        var records = (await fixture.Reopen().ReadAppsOperationsAsync(default)).Records;
        Assert.Equal(2, records.Count);
        Assert.Equal(records[0].Intent.Id, records[1].Intent.ReversesOperationId);
        foreach (var file in Directory.GetFiles(Path.Combine(fixture.Root, "memory"), "*.md", SearchOption.AllDirectories))
            Assert.DoesNotContain("SECRET-FIXTURE", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunNeverOverwritesExternalCollisionOrChangedValue()
    {
        using var fixture = new Fixture();
        var entry = Run();
        var registry = new FakeRun(entry) { Value = new(true, "external.exe", "ExpandString", true) };
        var executor = new RunStartupChangeExecutor(registry, fixture.Store);
        Assert.Equal(AppsOperationOutcome.Conflict, (await executor.ExecuteAsync(Change(entry, StartupAction.Disable), default)).Outcome);
        Assert.Equal(0, registry.Writes);
        registry.Value = new(true, entry.Reference, "ExpandString", true);
        Assert.Equal(AppsOperationOutcome.Succeeded, (await executor.ExecuteAsync(Change(entry, StartupAction.Disable), default)).Outcome);
        registry.Value = new(true, "external.exe", "String", true);
        Assert.Equal(AppsOperationOutcome.Conflict, (await executor.ExecuteAsync(Change(entry with { State = StartupConfiguredState.Disabled }, StartupAction.Enable), default)).Outcome);
        Assert.Equal("external.exe", registry.Value.Content);
        Assert.Equal(1, registry.Writes);
    }

    [Fact]
    public async Task UnsupportedRunIdentitiesAndWrongConfirmationDoNotReadRegistry()
    {
        using var fixture = new Fixture();
        var entry = Run();
        var registry = new FakeRun(entry);
        var executor = new RunStartupChangeExecutor(registry, fixture.Store);
        foreach (var invalid in new[] { entry with { RegistryIdentity = entry.RegistryIdentity! with { View = "Registry32" } },
            entry with { RegistryIdentity = entry.RegistryIdentity! with { Hive = "LocalMachine" } },
            entry with { RegistryIdentity = entry.RegistryIdentity! with { ValueKind = "Binary" } },
            entry with { Source = StartupSourceKind.RegistryRunOnce }, entry with { Source = StartupSourceKind.StartupFolder },
            entry with { StableId = "forged" }, entry with { State = StartupConfiguredState.Unknown } })
            Assert.Equal(AppsOperationOutcome.Blocked, (await executor.ExecuteAsync(Change(invalid, StartupAction.Disable), default)).Outcome);
        Assert.Equal(AppsOperationOutcome.Blocked, (await executor.ExecuteAsync(Change(entry, StartupAction.Disable) with { Confirmation = "yes" }, default)).Outcome);
        Assert.Equal(0, registry.Reads);
        Assert.Equal(ChangeEligibility.ReadOnly, AppsStartupPolicy.Evaluate(entry with
            { Origin = AppsDataOrigin.Operational, Scope = AppsScope.Machine }).Eligibility);
        Assert.Equal(ChangeEligibility.ReadOnly, AppsStartupPolicy.Evaluate(Msi() with
            { Origin = AppsDataOrigin.Operational, Scope = AppsScope.Machine }).Eligibility);
    }

    [Fact]
    public async Task MissingProtectionBlocksRunBeforeAnyWrite()
    {
        using var fixture = new Fixture();
        var registry = new FakeRun(Run());
        var executor = new RunStartupChangeExecutor(registry, new MemoryStore(fixture.Root));
        Assert.Equal(AppsOperationOutcome.Blocked, (await executor.ExecuteAsync(Change(Run(), StartupAction.Disable), default)).Outcome);
        Assert.Equal(0, registry.Writes);
        Assert.Empty((await fixture.Store.ReadAppsOperationsAsync(default)).Records);
    }

    [Fact]
    public async Task DisabledRunEntryReturnsToInventoryFromProtectedHistory()
    {
        using var fixture = new Fixture();
        var registry = new FakeRun(Run());
        Assert.Equal(AppsOperationOutcome.Succeeded, (await new RunStartupChangeExecutor(registry, fixture.Store)
            .ExecuteAsync(Change(Run(), StartupAction.Disable), default)).Outcome);
        var provider = new RecoverableRunStartupProvider(new EmptyStartup(), registry, fixture.Reopen());
        var inventory = await provider.ReadAsync(default);
        var entry = Assert.Single(inventory.Entries);
        Assert.Equal(StartupConfiguredState.Disabled, entry.State);
        Assert.Equal(Run().Reference, entry.Reference);
        Assert.True(inventory.Complete);
        registry.Value = new(true, "external.exe", "String", true);
        var collision = await provider.ReadAsync(default);
        Assert.Empty(collision.Entries);
        Assert.False(collision.Complete);
    }

    [Fact]
    public async Task BrokenRestorationCiphertextCannotEnableRun()
    {
        using var fixture = new Fixture();
        var registry = new FakeRun(Run());
        Assert.Equal(AppsOperationOutcome.Succeeded, (await new RunStartupChangeExecutor(registry, fixture.Store)
            .ExecuteAsync(Change(Run(), StartupAction.Disable), default)).Outcome);
        var wrongKeyStore = new MemoryStore(fixture.Root, new FakeProtector());
        var disabled = Run() with { State = StartupConfiguredState.Disabled };
        Assert.Equal(AppsOperationOutcome.Blocked, (await new RunStartupChangeExecutor(registry, wrongKeyStore)
            .ExecuteAsync(Change(disabled, StartupAction.Enable), default)).Outcome);
        Assert.False(registry.Value.Exists);
    }

    [Fact]
    public async Task PersistenceFailureBeforeIntentPreventsRunWrite()
    {
        using var fixture = new Fixture();
        var registry = new FakeRun(Run());
        var failing = new FailingStore(fixture.Store) { FailIntent = true };
        Assert.Equal(AppsOperationOutcome.Blocked, (await new RunStartupChangeExecutor(registry, failing)
            .ExecuteAsync(Change(Run(), StartupAction.Disable), default)).Outcome);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task PersistenceFailureAfterEffectRecoversIndeterminateAndBlocksSecondExecution()
    {
        using var fixture = new Fixture();
        var registry = new FakeRun(Run());
        var failing = new FailingStore(fixture.Store) { FailCompletion = true };
        Assert.Equal(AppsOperationOutcome.Indeterminate, (await new RunStartupChangeExecutor(registry, failing)
            .ExecuteAsync(Change(Run(), StartupAction.Disable), default)).Outcome);
        Assert.Equal(AppsOperationOutcome.Indeterminate, Assert.Single((await fixture.Reopen().ReadAppsOperationsAsync(default)).Records).Outcome);
        var disabled = Run() with { State = StartupConfiguredState.Disabled };
        Assert.Equal(AppsOperationOutcome.Blocked, (await new RunStartupChangeExecutor(registry, fixture.Reopen())
            .ExecuteAsync(Change(disabled, StartupAction.Enable), default)).Outcome);
        Assert.Equal(1, registry.Writes);
    }

    [Fact]
    public async Task VerificationFailureNeverBecomesSuccess()
    {
        using var fixture = new Fixture();
        var registry = new FakeRun(Run()) { IgnoreWrite = true };
        Assert.Equal(AppsOperationOutcome.Indeterminate, (await new RunStartupChangeExecutor(registry, fixture.Store)
            .ExecuteAsync(Change(Run(), StartupAction.Disable), default)).Outcome);
        Assert.Equal(AppsOperationOutcome.Indeterminate, Assert.Single((await fixture.Store.ReadAppsOperationsAsync(default)).Records).Outcome);
    }

    [Fact]
    public async Task MsiMapsArtificialNativeResultsAndRebootRequirement()
    {
        foreach (var code in new uint[] { 0, 3010, 1602, 1618, 1603, 1641, 87 })
        {
            using var fixture = new Fixture();
            var api = new FakeMsi(Msi()) { Code = code };
            var result = await new MsiApplicationUninstallExecutor(api, fixture.Store).ExecuteAsync(Uninstall(Msi()), default);
            Assert.Equal(code is 0 or 3010 ? AppsOperationOutcome.Succeeded : code == 1618 ?
                AppsOperationOutcome.Failed : AppsOperationOutcome.Indeterminate, result.Outcome);
            Assert.Equal((int)code, result.NativeCode);
            Assert.Equal(code is 3010 or 1641, result.RestartRequired);
            Assert.Equal(1, api.Calls);
            Assert.Single((await fixture.Reopen().ReadAppsOperationsAsync(default)).Records);
        }
    }

    [Fact]
    public async Task MsiRejectsAmbiguousScopeManagedIdentityAndChangedMetadata()
    {
        foreach (var invalid in new[] { new FakeMsi(Msi()).Installation! with { Unambiguous = false },
            new FakeMsi(Msi()).Installation! with { Context = "UserManaged" },
            new FakeMsi(Msi()).Installation! with { Scope = AppsScope.Machine },
            new FakeMsi(Msi()).Installation! with { Version = "2.0" },
            new FakeMsi(Msi()).Installation! with { ProductCode = "other" },
            new FakeMsi(Msi()).Installation! with { Eligible = false } })
        {
            using var fixture = new Fixture();
            var api = new FakeMsi(Msi()) { Installation = invalid };
            Assert.Equal(AppsOperationOutcome.Blocked, (await new MsiApplicationUninstallExecutor(api, fixture.Store)
                .ExecuteAsync(Uninstall(Msi()), default)).Outcome);
            Assert.Equal(0, api.Calls);
        }
    }

    [Fact]
    public async Task MsiSuccessCodeWithoutConfirmedAbsenceIsIndeterminate()
    {
        using var fixture = new Fixture();
        var api = new FakeMsi(Msi()) { KeepInstalled = true };
        Assert.Equal(AppsOperationOutcome.Indeterminate, (await new MsiApplicationUninstallExecutor(api, fixture.Store)
            .ExecuteAsync(Uninstall(Msi()), default)).Outcome);
    }

    [Fact]
    public async Task MsixVerifiesAbsenceAndRecordsArtificialAsyncErrors()
    {
        foreach (var code in new[] { 0, unchecked((int)0x80070005) })
        {
            using var fixture = new Fixture();
            var api = new FakeMsix(Msix()) { ErrorCode = code };
            var result = await new MsixApplicationUninstallExecutor(api, fixture.Store).ExecuteAsync(Uninstall(Msix()), default);
            Assert.Equal(code == 0 ? AppsOperationOutcome.Succeeded : AppsOperationOutcome.Indeterminate, result.Outcome);
            Assert.Equal(code, result.NativeCode);
            Assert.Equal(1, api.Calls);
        }
    }

    [Fact]
    public async Task IndividualRouterExecutesOnlyTheSelectedTypedTarget()
    {
        using var fixture = new Fixture();
        var msi = new FakeMsi(Msi());
        var msix = new FakeMsix(Msix());
        var router = new IndividualApplicationUninstallExecutor(new MsiApplicationUninstallExecutor(msi, fixture.Store),
            new MsixApplicationUninstallExecutor(msix, fixture.Store));
        Assert.Equal(AppsOperationOutcome.Succeeded, (await router.ExecuteAsync(Uninstall(Msi()), default)).Outcome);
        Assert.Equal(1, msi.Calls);
        Assert.Equal(0, msix.Calls);
        Assert.Equal(AppsOperationOutcome.Succeeded, (await router.ExecuteAsync(Uninstall(Msix()), default)).Outcome);
        Assert.Equal(1, msi.Calls);
        Assert.Equal(1, msix.Calls);
        Assert.Equal(AppsOperationOutcome.Blocked, (await router.ExecuteAsync(Uninstall(Msix()) with { Method = UninstallMethod.None }, default)).Outcome);
    }

    [Fact]
    public async Task MsixRejectsFrameworkDependenciesProtectionUnknownUserAndUnavailableIdentity()
    {
        var original = new FakeMsix(Msix()).Identity!;
        foreach (var invalid in new[] { original with { Framework = true }, original with { Dependency = true },
            original with { Protected = true }, original with { CurrentUser = false }, original with { Available = false },
            original with { IdentityKnown = false }, original with { FullName = "other" }, original with { FamilyName = "other" } })
        {
            using var fixture = new Fixture();
            var api = new FakeMsix(Msix()) { Identity = invalid };
            Assert.Equal(AppsOperationOutcome.Blocked, (await new MsixApplicationUninstallExecutor(api, fixture.Store)
                .ExecuteAsync(Uninstall(Msix()), default)).Outcome);
            Assert.Equal(0, api.Calls);
        }
    }

    [Fact]
    public async Task MsixCancellationAfterStartStillWaitsForReceiptAndSerializesOtherOperations()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var api = new FakeMsix(Msix()) { Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var running = new MsixApplicationUninstallExecutor(api, fixture.Store).ExecuteAsync(Uninstall(Msix()), cancellation.Token);
        await api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var secondApi = new FakeMsi(Msi());
        Assert.Equal(AppsOperationOutcome.Blocked, (await new MsiApplicationUninstallExecutor(secondApi, fixture.Reopen())
            .ExecuteAsync(Uninstall(Msi()), default)).Outcome);
        Assert.Equal(0, secondApi.Calls);
        api.Wait.SetResult();
        Assert.Equal(AppsOperationOutcome.Succeeded, (await running).Outcome);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task OperationalExecutorsGateBeforeAnyFakeNativeRead()
    {
        using var fixture = new Fixture();
        var run = new FakeRun(Run()) { IsSynthetic = false };
        var msi = new FakeMsi(Msi()) { IsSynthetic = false };
        var msix = new FakeMsix(Msix()) { IsSynthetic = false };
        Assert.False(AppsOperationalExecutionGate.PermitsExecution);
        Assert.Equal(AppsOperationOutcome.GateClosed, (await new RunStartupChangeExecutor(run, fixture.Store)
            .ExecuteAsync(Change(Run() with { Origin = AppsDataOrigin.Operational }, StartupAction.Disable), default)).Outcome);
        Assert.Equal(AppsOperationOutcome.GateClosed, (await new MsiApplicationUninstallExecutor(msi, fixture.Store)
            .ExecuteAsync(Uninstall(Msi() with { Origin = AppsDataOrigin.Operational }), default)).Outcome);
        Assert.Equal(AppsOperationOutcome.GateClosed, (await new MsixApplicationUninstallExecutor(msix, fixture.Store)
            .ExecuteAsync(Uninstall(Msix() with { Origin = AppsDataOrigin.Operational }), default)).Outcome);
        Assert.Equal(0, run.Reads + msi.Reads + msix.Reads);
    }

    [Fact]
    public async Task ManagerPersistsSyntheticHistoryAndLoadsItAfterReopening()
    {
        using var fixture = new Fixture();
        var startup = SyntheticStartupStore.CreateDefault();
        var apps = SyntheticApplicationStore.CreateDefault();
        var manager = new AppsStartupManager(startup, apps, startup, apps, operationStore: fixture.Store);
        var entry = (await manager.LoadAsync(default)).Startup.First();
        var preview = manager.PreviewStartupChange(entry, StartupAction.Disable);
        Assert.Equal(AppsOperationOutcome.Succeeded, (await manager.ExecuteStartupChangeAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
        Assert.Equal(StartupConfiguredState.Disabled, manager.Snapshot.Startup.First().State);
        var reopened = new AppsStartupManager(startup, apps, startup, apps, operationStore: fixture.Reopen());
        await reopened.LoadAsync(default);
        Assert.Equal(AppsOperationOutcome.Succeeded, Assert.Single(reopened.History).Outcome);
        Assert.True(reopened.History[0].Synthetic);
        Assert.NotNull(reopened.History[0].OperationId);
    }

    [Fact]
    public async Task ManagerRejectsForgedPreviewAndPersistenceFailureBeforeExecutor()
    {
        using var fixture = new Fixture();
        var startup = SyntheticStartupStore.CreateDefault();
        var apps = SyntheticApplicationStore.CreateDefault();
        var manager = new AppsStartupManager(startup, apps, startup, apps, operationStore:
            new FailingStore(fixture.Store) { FailIntent = true });
        var entry = (await manager.LoadAsync(default)).Startup.First();
        var preview = manager.PreviewStartupChange(entry, StartupAction.Disable);
        Assert.Equal(AppsOperationOutcome.Blocked, (await manager.ExecuteStartupChangeAsync(
            preview with { ConfirmationPhrase = "forged" }, "forged", default)).Outcome);
        Assert.Equal(AppsOperationOutcome.Blocked, (await manager.ExecuteStartupChangeAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
        Assert.Equal(StartupConfiguredState.Enabled, (await startup.ReadAsync(default)).Entries.First().State);
    }

    [Fact]
    public async Task MsixCandidatesWithUnknownProtectionRemainReadOnly()
    {
        var source = new FakePackageSource();
        var inventory = await new MsixInventoryProvider(source).ReadAsync(default);
        var entry = Assert.Single(inventory.Entries);
        Assert.Equal("ApplicationCandidate", entry.PackageClassification);
        Assert.False(entry.IsSystemOrFramework);
        Assert.Equal(ChangeEligibility.ReadOnly, AppsStartupPolicy.Evaluate(entry).Eligibility);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sentinel-apps-execution-" + Guid.NewGuid().ToString("N"));
        private readonly FakeProtector protector = new();
        public MemoryStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Store = new(Root, protector); }
        public MemoryStore Reopen() => new(Root, protector);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class FakeProtector : IAppsSecretProtector
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public bool IsSynthetic => true;
        public byte[] Protect(byte[] plaintext)
        {
            var output = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(28), output.AsSpan(12, 16));
            return output;
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            var output = new byte[ciphertext.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), output);
            return output;
        }
    }

    private sealed class FakeRun(StartupEntry entry) : IRunStartupAccess
    {
        public bool IsSynthetic { get; init; } = true;
        public RunValueState Value { get; set; } = new(true, entry.Reference, entry.RegistryIdentity!.ValueKind, true);
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool IgnoreWrite { get; init; }
        public RunValueState Read(StartupRegistryIdentity identity) { Reads++; return Value; }
        public bool DeleteIfUnchanged(StartupRegistryIdentity identity, RunValueState expected)
        {
            if (Value != expected) return false;
            Writes++;
            if (!IgnoreWrite) Value = new(false, null, null, true);
            return true;
        }
        public bool RestoreIfAbsent(StartupRegistryIdentity identity, string content)
        {
            if (Value.Exists) return false;
            Writes++;
            Value = new(true, content, identity.ValueKind, true);
            return true;
        }
    }

    private sealed class FakeMsi(InstalledApplicationEntry entry) : IMsiUninstallApi
    {
        public bool IsSynthetic { get; init; } = true;
        public MsiInstallation? Installation { get; set; } = new(entry.ProductCode!, entry.Scope, "UserUnmanaged", entry.Name,
            entry.Version!, entry.Publisher!, true, true, true);
        public uint Code { get; init; }
        public bool KeepInstalled { get; init; }
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public MsiInstallation? Inspect(string productCode) { Reads++; return Installation; }
        public uint Uninstall(string productCode) { Calls++; if (Code is 0 or 3010 && !KeepInstalled) Installation = null; return Code; }
    }

    private sealed class FakeMsix(InstalledApplicationEntry entry) : IMsixUninstallApi
    {
        public bool IsSynthetic { get; init; } = true;
        public MsixRemovalIdentity? Identity { get; set; } = new(entry.PackageFullName!, entry.PackageFamilyName!, entry.Name,
            entry.Version!, true, true, false, false, false, true);
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public int ErrorCode { get; init; }
        public TaskCompletionSource? Wait { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MsixRemovalIdentity? Inspect(string fullName) { Reads++; return Identity; }
        public async Task<MsixRemovalReceipt> RemoveAsync(string fullName)
        {
            Calls++;
            Entered.TrySetResult();
            if (Wait is not null) await Wait.Task;
            if (ErrorCode == 0) Identity = null;
            return new(ErrorCode == 0, ErrorCode);
        }
    }

    private sealed class FailingStore(IAppsOperationStore inner) : IAppsOperationStore
    {
        public bool FailIntent { get; init; }
        public bool FailCompletion { get; init; }
        public IDisposable AcquireAppsOperationLease() => inner.AcquireAppsOperationLease();
        public Task<AppsOperationHistory> ReadAppsOperationsAsync(CancellationToken token) => inner.ReadAppsOperationsAsync(token);
        public Task SaveAppsIntentAsync(AppsOperationIntent intent, CancellationToken token) => FailIntent ?
            Task.FromException(new IOException("synthetic-intent-failure")) : inner.SaveAppsIntentAsync(intent, token);
        public Task SaveAppsStartedAsync(Guid id, DateTimeOffset at, CancellationToken token) => inner.SaveAppsStartedAsync(id, at, token);
        public Task SaveAppsCompletionAsync(AppsOperationCompletion completion, CancellationToken token) => FailCompletion ?
            Task.FromException(new IOException("synthetic-result-failure")) : inner.SaveAppsCompletionAsync(completion, token);
        public string ProtectStartupState(StartupEntry entry) => inner.ProtectStartupState(entry);
        public StartupEntry UnprotectStartupState(string ciphertext, AppsDataOrigin origin) => inner.UnprotectStartupState(ciphertext, origin);
    }

    private sealed class FakePackageSource : IMsixPackageSource
    {
        public IReadOnlyList<MsixPackageSnapshot> ReadCurrentUser() =>
            [new("Fixture_1.0.0.0_x64__fixture", "Fixture_fixture", "Fixture", "Publisher", "1.0.0.0", "Package", true,
                false, false, false) { ProtectionKnown = false }];
    }

    private sealed class EmptyStartup : IStartupInventoryProvider
    {
        public Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StartupInventoryResult([], [], true));
    }
}
