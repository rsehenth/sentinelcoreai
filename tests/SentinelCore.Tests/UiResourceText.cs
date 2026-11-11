using System.Globalization;
using System.Text.RegularExpressions;
using SentinelCore.App.Localization;

namespace SentinelCore.Tests;

/// <summary>
/// Resolve textos da interface que migraram de literais para <c>{DynamicResource Tn}</c>.
/// <see cref="Resolve"/> e <see cref="Resource"/> operam nos mesmos textos catalogados: o primeiro
/// devolve o texto visível em português de uma chave, o segundo a chave exata daquele texto.
/// </summary>
internal static class UiResourceText
{
    private static readonly Regex Dynamic = new(@"^\{DynamicResource\s+T(\d+)\}$", RegexOptions.CultureInvariant);

    public static string Resolve(string value)
    {
        var match = Dynamic.Match(value.Trim());
        if (!match.Success) return value;
        var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return index >= 0 && index < Strings.Entries.Length ? Strings.Entries[index].Pt : value;
    }

    public static string Resource(string portuguese)
    {
        var index = Array.FindIndex(Strings.Entries, entry => string.Equals(entry.Pt, portuguese, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException($"Texto não catalogado: {portuguese}");
        return $"{{DynamicResource T{index}}}";
    }

    public static string ResolveAttribute(System.Xml.Linq.XElement element, string attribute) =>
        Resolve((string?)element.Attribute(attribute) ?? string.Empty);
}
