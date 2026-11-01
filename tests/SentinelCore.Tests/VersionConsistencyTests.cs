using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Defeito D4 da Fase 3.3A: a versão mostrada ao usuário (<c>SoftwareInfo.Version</c>, gravada em cada registro
/// persistido) divergia da versão compilada (<c>Directory.Build.props</c>, usada no nome do diretório de
/// publicação). A fonte única da versão do produto é <c>Directory.Build.props</c>.
///
/// AssemblyVersion e FileVersion têm outra semântica (quatro campos numéricos, sem sufixo de pré-lançamento) e
/// por isso NÃO são comparados por igualdade textual: validam-se apenas contra a parte numérica do produto.
/// </summary>
public sealed class VersionConsistencyTests
{
    private static string ProductVersionFromProps()
    {
        var props = XDocument.Load(TestRepository.Combine("Directory.Build.props"));
        return props.Descendants("Version").Single().Value.Trim();
    }

    private static string WithoutBuildMetadata(string version) => version.Split('+')[0];

    [Fact]
    public void DisplayedSoftwareVersionEqualsTheVersionDeclaredInDirectoryBuildProps()
    {
        Assert.Equal(ProductVersionFromProps(), SoftwareInfo.Version);
    }

    [Fact]
    public void CompiledInformationalVersionOfEveryProductAssemblyEqualsTheProductVersion()
    {
        var expected = ProductVersionFromProps();
        var assemblies = new[]
        {
            typeof(SoftwareInfo).Assembly,
            typeof(SentinelCore.Application.AuditRunner).Assembly,
            typeof(SentinelCore.Persistence.MemoryStore).Assembly,
            typeof(SentinelCore.Infrastructure.Windows.Network.NetworkEndpointCollector).Assembly
        };

        foreach (var assembly in assemblies)
        {
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Assert.NotNull(informational);
            Assert.Equal(expected, WithoutBuildMetadata(informational!));
        }
    }

    [Fact]
    public void NumericAssemblyAndFileVersionsMatchOnlyTheNumericPartOfTheProductVersion()
    {
        var numeric = ProductVersionFromProps().Split('-')[0]; // 0.3.0
        var assembly = typeof(SoftwareInfo).Assembly;

        var assemblyVersion = assembly.GetName().Version!;
        Assert.Equal(numeric, $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");

        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location);
        Assert.Equal(numeric, $"{fileVersion.FileMajorPart}.{fileVersion.FileMinorPart}.{fileVersion.FileBuildPart}");
    }

    [Fact]
    public void PublishScriptDerivesItsOutputFolderFromTheSameCentralVersion()
    {
        var script = File.ReadAllText(TestRepository.Combine("scripts", "publish.ps1"));

        Assert.Contains("Directory.Build.props", script);
        Assert.Contains("$version", script);
        // O script recusa sobrescrever uma publicação existente sem -Force.
        Assert.Contains("Destino ja existe", script);
    }

}
