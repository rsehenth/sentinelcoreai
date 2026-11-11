namespace SentinelCore.Tests;

/// <summary>Localiza arquivos do repositório para testes que protegem contratos de build e de código-fonte.</summary>
internal static class TestRepository
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SentinelCore.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("SentinelCore.sln não encontrado.");
    }

    public static string Combine(params string[] parts) => Path.Combine([Root(), .. parts]);

    /// <summary>Resolve apenas referências de texto locais; não carrega XAML nem compõe o aplicativo.</summary>
    public static string AppSource(string file)
    {
        var text = File.ReadAllText(Combine("src", "SentinelCore.App", file));
        if (!file.EndsWith(".xaml", StringComparison.Ordinal)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{DynamicResource T(\d+)\}", match =>
            System.Security.SecurityElement.Escape(SentinelCore.App.Localization.Strings.Entries[
                int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)].Pt)!);
    }

    /// <summary>Versão do produto declarada em <c>Directory.Build.props</c>, fonte única do versionamento.</summary>
    public static string ProductVersion() =>
        System.Xml.Linq.XDocument.Load(Combine("Directory.Build.props"))
            .Descendants("Version").Single().Value.Trim();
}
