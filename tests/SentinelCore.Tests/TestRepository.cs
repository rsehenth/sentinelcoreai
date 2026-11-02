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

    /// <summary>Versão do produto declarada em <c>Directory.Build.props</c>, fonte única do versionamento.</summary>
    public static string ProductVersion() =>
        System.Xml.Linq.XDocument.Load(Combine("Directory.Build.props"))
            .Descendants("Version").Single().Value.Trim();
}
