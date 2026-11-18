using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.AppsStartup;
using Xunit;

namespace SentinelCore.Tests;

public sealed class AppsStartupInventoryTests
{
    [Fact]
    public async Task RegistryIdentityPreservesViewsAndKinds()
    {
        var result = await new RunKeyStartupProvider(new RegistryFixture()).ReadAsync(default);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Entries.Count);
        Assert.NotEqual(result.Entries[0].StableId, result.Entries[1].StableId);
        Assert.Equal("ExpandString", result.Entries[0].RegistryIdentity!.ValueKind);
        Assert.Equal("%FIXTURE%\\app.exe", result.Entries[0].Reference);
        Assert.Equal(StartupConfiguredState.Unknown, result.Entries[0].State);
    }

    [Fact]
    public async Task StartupFoldersAreScopedAndOperationallyReadOnly()
    {
        var result = await new StartupFolderProvider(new FolderFixture()).ReadAsync(default);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Entries.Count);
        Assert.NotEqual(result.Entries[0].StableId, result.Entries[1].StableId);
        Assert.All(result.Entries, entry => Assert.Equal(ChangeEligibility.ReadOnly, AppsStartupPolicy.Evaluate(entry).Eligibility));
    }

    [Fact]
    public async Task MsixClassificationRejectsIncompleteAndDuplicates()
    {
        var common = new MsixPackageSnapshot("Fixture_1_x64__x", "Fixture_x", "Fixture", "Publisher", "1.0", "Package", true, false, false, false);
        var values = new[] { common, common, common with { FullName = "Fixture_2_x64__x", Version = "2.0", Framework = true },
            common with { FullName = "Dependency_1_x64__x", Dependency = true }, common with { FullName = "Protected_1_x64__x", Protected = true },
            common with { FullName = null } };
        var result = await new MsixInventoryProvider(new MsixFixture(values)).ReadAsync(default);
        Assert.False(result.Complete);
        Assert.Equal(4, result.Entries.Count);
        Assert.Equal(ChangeEligibility.Eligible, AppsStartupPolicy.Evaluate(result.Entries[0]).Eligibility);
        Assert.All(result.Entries.Skip(1), e => Assert.Equal(ChangeEligibility.Unavailable, AppsStartupPolicy.Evaluate(e).Eligibility));
        Assert.Equal("Fixture_x", result.Entries[0].PackageFamilyName);
    }

    [Fact]
    public async Task MsixEmptyAndAccessFailureDiffer()
    {
        var empty = await new MsixInventoryProvider(new MsixFixture([])).ReadAsync(default);
        var failed = await new MsixInventoryProvider(new MsixFixture(null)).ReadAsync(default);
        Assert.True(empty.Complete);
        Assert.False(failed.Complete);
        Assert.Empty(failed.Entries);
    }

    private sealed class RegistryFixture : IAppsRegistrySource, IRunRegistrySource
    {
        public (IReadOnlyList<RunRegistryValue> Values, IReadOnlyList<string> Warnings) ReadRunValues() =>
            ([new(AppsScope.Machine, "LocalMachine", RunKeyStartupProvider.RunPath, "Registry64", "Fixture", "ExpandString", "%FIXTURE%\\app.exe", StartupConfiguredState.Unknown),
              new(AppsScope.Machine, "LocalMachine", RunKeyStartupProvider.RunPath, "Registry32", "Fixture", "String", "fixture.exe", StartupConfiguredState.Enabled)], []);
        public IReadOnlyDictionary<string, object?>? ReadValues(AppsScope scope, string path) => throw new InvalidOperationException();
        public IReadOnlyList<RegistryKeySnapshot>? ReadSubKeys(AppsScope scope, string path) => throw new InvalidOperationException();
    }

    private sealed class FolderFixture : IStartupFolderSource
    {
        public IReadOnlyList<StartupFile>? Read(AppsScope scope) => [new($"C:\\Fixture\\{scope}\\app.lnk", "app.lnk", ".lnk", true)];
    }

    private sealed class MsixFixture(IReadOnlyList<MsixPackageSnapshot>? packages) : IMsixPackageSource
    {
        public IReadOnlyList<MsixPackageSnapshot> ReadCurrentUser() => packages ?? throw new UnauthorizedAccessException();
    }
}
