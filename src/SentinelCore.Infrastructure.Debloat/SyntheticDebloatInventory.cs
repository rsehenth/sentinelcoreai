using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Debloat;

/// <summary>Only in-memory fixture metadata. No OS package API, shell, file removal or network.</summary>
public sealed class SyntheticDebloatInventory : IDebloatInventory
{
    private readonly object sync = new();
    private List<DebloatPackage> packages;
    public SyntheticDebloatInventory(IEnumerable<DebloatPackage> packages) => this.packages = Copy(packages.ToList());
    public bool SyntheticOnly => true;
    public string CurrentUserId => DebloatFixtureIdentity.User;
    public Task<IReadOnlyList<DebloatPackage>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult<IReadOnlyList<DebloatPackage>>(Copy(packages));
    }
    public void Replace(IEnumerable<DebloatPackage> replacement)
    {
        lock (sync) packages = Copy(replacement.ToList());
    }
    public void Set(DebloatPackage package)
    {
        lock (sync)
        {
            var index = packages.FindIndex(p => p.CatalogId == package.CatalogId);
            if (index < 0) throw new InvalidOperationException("Fixture desconhecida.");
            packages[index] = Copy(new List<DebloatPackage> { package })[0];
        }
    }
    public static SyntheticDebloatInventory Create(IEnumerable<DebloatCatalogEntry> catalog)
    {
        var examples = new HashSet<string>(StringComparer.Ordinal)
        {
            "Clipchamp.Clipchamp", "Microsoft.BingWeather", "Microsoft.WindowsCalculator",
            "Microsoft.WindowsNotepad", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.Windows.Photos",
            "Microsoft.WindowsCamera", "Microsoft.YourPhone"
        };
        var items = catalog.Where(entry => examples.Contains(entry.TechnicalName) || entry.PreliminaryRisk == RiskLevel.High)
            .Select(entry => Package(entry)).ToList();
        for (var i = 0; i < items.Count; i++)
            items[i] = items[i].TechnicalName switch
            {
                "Microsoft.Windows.Photos" => items[i] with { Scope = DebloatScope.OtherUser, UserId = "S-1-5-21-100-200-300-1002" },
                "Microsoft.WindowsCamera" => items[i] with { Kind = DebloatPackageKind.Framework },
                "Microsoft.YourPhone" => items[i] with { Scope = DebloatScope.Provisioned, Provisioned = true },
                _ => items[i]
            };
        return new SyntheticDebloatInventory(items);
    }
    public static DebloatPackage Package(DebloatCatalogEntry entry) => new(
        entry.Id, entry.FriendlyName, entry.TechnicalName,
        entry.TechnicalName + "_synthetic", $"{entry.TechnicalName}_1.0.0.0_x64__synthetic",
        DebloatFixtureIdentity.Publisher, "1.0.0.0", "x64", DebloatFixtureIdentity.User,
        entry.RemovalMethod == "Appx" ? DebloatPackageKind.Appx : DebloatPackageKind.Win32,
        DebloatScope.CurrentUser, DebloatInstallState.Installed, false,
        entry.PreliminaryRisk == RiskLevel.High, entry.PreliminaryRisk == RiskLevel.High,
        DebloatDependencyStatus.KnownNone, [], true, "Fixture sintética T3",
        ["Identidades, versões e dependências são ilustrativas. Não representam pacotes deste computador."]);
    private static List<DebloatPackage> Copy(List<DebloatPackage> value) =>
        JsonSerializer.Deserialize<List<DebloatPackage>>(JsonSerializer.Serialize(value))!;
}
