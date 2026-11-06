using SentinelCore.Domain;
using SentinelCore.Infrastructure.Repairs;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Validação de identidade/caminho do executável oficial. Os testes apenas consultam nomes e atributos;
/// nenhum processo é iniciado.
/// </summary>
public sealed class RepairToolPathTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void PolicyRejectsWrongNameExtensionDirectoryAndScripts()
    {
        Assert.False(RepairToolPathPolicy.IsCanonicalToolPath(RepairTool.Sfc,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), out _));
        Assert.False(RepairToolPathPolicy.IsCanonicalToolPath(RepairTool.Sfc,
            Path.Combine(Environment.SystemDirectory, "sfc.bat"), out _));
        Assert.False(RepairToolPathPolicy.IsCanonicalToolPath(RepairTool.Dism,
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "dism.exe"), out _));
        Assert.False(RepairToolPathPolicy.IsCanonicalToolPath(RepairTool.Chkdsk,
            Path.Combine(Path.GetTempPath(), "chkdsk.exe"), out _));
        Assert.False(RepairToolPathPolicy.IsCanonicalToolPath(RepairTool.Reagentc, "reagentc.exe", out _));
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void PolicyAcceptsOnlyTheExactExistingExecutableInSystem32()
    {
        foreach (var tool in Enum.GetValues<RepairTool>())
        {
            var path = Path.Combine(Environment.SystemDirectory, RepairToolNames.Executable(tool));
            if (!File.Exists(path)) continue;

            Assert.True(RepairToolPathPolicy.IsCanonicalToolPath(tool, path, out var reason), reason);
            Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(path));
        }
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void LocatorResolvesOnlySystem32AndNeverSearchesThePathVariable()
    {
        var locator = new WindowsRepairToolLocator();

        foreach (var tool in Enum.GetValues<RepairTool>())
        {
            var location = locator.Resolve(tool);
            if (location.Available)
            {
                Assert.NotNull(location.Path);
                Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(location.Path));
                Assert.Equal(RepairToolNames.Executable(tool), Path.GetFileName(location.Path));
            }
            else
            {
                Assert.Contains("System32", location.Error, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
