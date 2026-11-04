using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyCatalogTests
{
    [Fact]
    public void SyntheticCatalogHasVersionHashAndOperations()
    {
        var catalog = T1SyntheticCatalog.Create();

        Assert.Equal(T1SyntheticCatalog.CatalogVersion, catalog.Version);
        Assert.NotEmpty(catalog.ContentHash);
        Assert.Equal(64, catalog.ContentHash.Length);
        Assert.Equal(14, catalog.All.Count);
        Assert.All(catalog.All, operation => Assert.StartsWith("SYN-", operation.Id));
        Assert.All(catalog.All, operation => Assert.All(operation.Targets,
            target => Assert.Equal("Synthetic", target.Root)));
        Assert.All(catalog.All, operation => Assert.True(operation.Protection.RequiresBackup));
    }

    [Fact]
    public async Task UnknownOperationIsRejectedBeforeAnyRead()
    {
        using var scope = new SafetyScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Engine.SimulateAsync("NAO-EXISTE"));
    }

    [Fact]
    public void TryGetIsCaseSensitiveAndRejectsUnknown()
    {
        var catalog = T1SyntheticCatalog.Create();

        Assert.True(catalog.TryGet("SYN-PREF-01", out var operation));
        Assert.NotNull(operation);
        Assert.False(catalog.TryGet("syn-pref-01", out _));
        Assert.False(catalog.TryGet("", out _));
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("NAO-EXISTE"));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        var operation = SafetyTestData.TwoTargetOperation();

        Assert.Throws<InvalidDataException>(() => new OperationCatalog("v1", [operation, operation]));
    }

    [Fact]
    public void EmptyCatalogIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new OperationCatalog("v1", []));
        Assert.Throws<InvalidDataException>(() => new OperationCatalog("", [SafetyTestData.TwoTargetOperation()]));
    }

    [Fact]
    public void TargetOutsideAllowedRootsIsRejected()
    {
        var operation = SafetyTestData.TwoTargetOperation();
        var rogue = operation.Targets[0] with { Root = "HKLM" };
        var changed = operation with
        {
            Targets = [rogue, operation.Targets[1]],
            Changes = [operation.Changes[0], operation.Changes[1]]
        };

        Assert.Throws<InvalidDataException>(() => new OperationCatalog("v1", [changed]));
    }

    [Fact]
    public void OperationWithoutBackupIsRejected()
    {
        var operation = SafetyTestData.TwoTargetOperation() with
        {
            Protection = new ProtectionPolicy(false, false, false)
        };

        Assert.Throws<InvalidDataException>(() => new OperationCatalog("v1", [operation]));
    }

    [Fact]
    public void NotValidatedEnabledOperationIsRejected()
    {
        var operation = SafetyTestData.TwoTargetOperation(risk: RiskLevel.NotValidated) with
        {
            Availability = OperationAvailability.Enabled
        };

        Assert.Throws<InvalidDataException>(() => new OperationCatalog("v1", [operation]));
    }

    [Fact]
    public void InvalidChangeShapeIsRejected()
    {
        var operation = SafetyTestData.TwoTargetOperation() with
        {
            Changes =
            [
                new PlannedTarget(SafetyTestData.TwoTargetOperation().Targets[0],
                    new TargetValueState(false, "String", null)),
                SafetyTestData.TwoTargetOperation().Changes[1]
            ]
        };

        Assert.Throws<InvalidDataException>(() => new OperationCatalog("v1", [operation]));
    }

    [Fact]
    public void DuplicateChangeTargetIsRejected()
    {
        var operation = SafetyTestData.TwoTargetOperation();
        var changed = operation with
        {
            Changes = [operation.Changes[0], operation.Changes[0]]
        };

        Assert.Throws<InvalidDataException>(() => new OperationCatalog("v1", [changed]));
    }

    [Fact]
    public void ContentHashIsDeterministicAndSensitiveToContent()
    {
        var first = T1SyntheticCatalog.Create();
        var second = T1SyntheticCatalog.Create();
        Assert.Equal(first.ContentHash, second.ContentHash);

        var changed = new OperationCatalog(T1SyntheticCatalog.CatalogVersion,
            T1SyntheticCatalog.Operations().Select(item => item.Id == "SYN-PREF-01"
                ? item with { DisplayName = "Outro nome" }
                : item));
        Assert.NotEqual(first.ContentHash, changed.ContentHash);
    }

    [Fact]
    public async Task HighRiskAndUnvalidatedOperationsArePresentButBlocked()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.High", "initial");
        scope.Seed("Preference.Unknown", "initial");

        var high = await scope.Engine.SimulateAsync("SYN-PREF-90");
        var unknown = await scope.Engine.SimulateAsync("SYN-PREF-91");

        Assert.False(high.CanExecute);
        Assert.Contains(BlockerCode.RiskBlocked, high.Blockers);
        Assert.False(unknown.CanExecute);
        Assert.Contains(BlockerCode.NotValidated, unknown.Blockers);
    }

    [Fact]
    public void SyntheticCatalogContainsNoRealExecutorIdentifiers()
    {
        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Application",
            "SafetyCatalog.cs"));
        foreach (var forbidden in new[]
                 {
                     "Microsoft.Win32.Registry", "RegistryKey", "Process.Start", "powershell", "DISM",
                     "Appx", "ServiceController", "TaskService", ".reg\""
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
    }
}
