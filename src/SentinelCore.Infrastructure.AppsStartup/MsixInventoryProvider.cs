using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

public sealed record MsixPackageSnapshot(string? FullName, string? FamilyName, string? Name, string? Publisher,
    string? Version, string Type, bool? Available, bool Framework, bool Dependency, bool Protected)
{
    public bool ProtectionKnown { get; init; } = true;
}

public interface IMsixPackageSource
{
    IReadOnlyList<MsixPackageSnapshot> ReadCurrentUser();
}

public sealed class MsixInventoryProvider(IMsixPackageSource source) : IInstalledApplicationProvider
{
    public Task<InstalledApplicationInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MsixPackageSnapshot> packages;
        try { packages = source.ReadCurrentUser(); }
        catch (Exception e) when (e is not OperationCanceledException)
        { return Task.FromResult(new InstalledApplicationInventoryResult([], ["msix:unavailable"], false)); }
        var entries = new List<InstalledApplicationEntry>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(p.FullName) || string.IsNullOrWhiteSpace(p.FamilyName) || string.IsNullOrWhiteSpace(p.Name))
            { warnings.Add("msix:incomplete-identity"); continue; }
            if (!seen.Add(p.FullName)) { warnings.Add("msix:duplicate"); continue; }
            entries.Add(new(AppsIdentity.Application(InstalledApplicationKind.Msix, AppsScope.CurrentUser, p.FullName),
                p.Name, p.Publisher, p.Version, InstalledApplicationKind.Msix, "AppModel", AppsScope.CurrentUser,
                null, null, p.FullName, p.Framework || p.Dependency || p.Protected || p.Available != true, AppsDataOrigin.Operational)
                { PackageFamilyName = p.FamilyName, PackageType = p.Type, PackageAvailable = p.Available,
                    PackageClassification = p.Available != true ? "Unavailable" : p.Framework ? "Framework" :
                        p.Dependency ? "Dependency" : p.Protected ? "Protected" : !p.ProtectionKnown ? "ApplicationCandidate" : "Application" });
        }
        return Task.FromResult(new InstalledApplicationInventoryResult(entries, warnings, warnings.Count == 0));
    }
}
