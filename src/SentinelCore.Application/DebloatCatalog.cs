using System.Text.Json;
using System.Text.Json.Serialization;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Documentary entries and a separate closed fixture policy. Neither grants real removal.</summary>
public sealed class DebloatCatalog
{
    public const string Version = "t3-debloat-synthetic-1";
    public const string UpstreamCommit = "5d88bfe9ef2be5a8a643166a6400eb78a30c46ec";
    private readonly IReadOnlyDictionary<string, DebloatCatalogEntry> entries;
    private static readonly HashSet<string> SyntheticNames = new(StringComparer.Ordinal)
    {
        "Clipchamp.Clipchamp", "Microsoft.BingWeather", "Microsoft.WindowsCalculator",
        "Microsoft.WindowsNotepad", "Microsoft.MicrosoftSolitaireCollection"
    };

    public DebloatCatalog(IEnumerable<DebloatCatalogEntry> source)
    {
        var items = source.ToArray();
        if (items.Length != 162 || items.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != 162 ||
            items.Select(e => e.TechnicalName).Distinct(StringComparer.Ordinal).Count() != 162 ||
            items.Count(e => e.RemovalMethod == "Appx") != 159 ||
            items.Count(e => e.RemovalMethod == "WinGet") != 3)
            throw new InvalidDataException("Catálogo documental T3 incompleto, duplicado ou com método inválido.");
        foreach (var item in items)
            if (!item.Id.StartsWith("SC-APP-", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.TechnicalName) || string.IsNullOrWhiteSpace(item.FriendlyName) ||
                !Enum.IsDefined(item.PreliminaryRisk) || !Enum.IsDefined(item.RealRisk) ||
                item.RealRisk is not (RiskLevel.High or RiskLevel.NotValidated) ||
                item.UpstreamCommit != UpstreamCommit)
                throw new InvalidDataException("Entrada documental T3 inválida.");
        entries = items.ToDictionary(e => e.Id, StringComparer.Ordinal);
        All = Array.AsReadOnly(items.OrderBy(e => e.FriendlyName, StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<DebloatCatalogEntry> All { get; }
    public DebloatCatalogEntry? Find(string id) => entries.GetValueOrDefault(id);
    public bool AllowsSynthetic(DebloatCatalogEntry entry) =>
        entries.TryGetValue(entry.Id, out var known) && known == entry && entry.RemovalMethod == "Appx" &&
        entry.PreliminaryRisk is RiskLevel.Low or RiskLevel.Medium && SyntheticNames.Contains(entry.TechnicalName);

    public static DebloatCatalog CreateDefault()
    {
        using var stream = typeof(DebloatCatalog).Assembly.GetManifestResourceStream(
            "SentinelCore.Application.DebloatCatalogData.json") ?? throw new InvalidDataException("Catálogo T3 ausente.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return new DebloatCatalog(JsonSerializer.Deserialize<DebloatCatalogEntry[]>(stream, options)
            ?? throw new InvalidDataException("Catálogo T3 nulo."));
    }
}

public static class RealDebloatBuildGate
{
    // No real capability is implemented, even if a property, argument or environment says otherwise.
    public static bool RealExecutionImplemented => false;
    public static bool AllowsRealExecution(IEnumerable<string>? arguments = null) => false;
}
