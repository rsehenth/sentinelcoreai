using System.Xml.Linq;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Static T3B boundary checks. These read source/configuration files; they never execute the native adapter.
/// </summary>
public sealed class DebloatNativeBoundaryTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string NativeSource() => string.Join(Environment.NewLine,
        Directory.GetFiles(TestRepository.Combine("src", "SentinelCore.Infrastructure.Debloat.Native"),
            "*.cs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText));

    [Fact]
    public void NativeReaderIsStrictlyReadOnlyWithExplicitScopeQueries()
    {
        var source = NativeSource();
        foreach (var forbidden in new[]
                 {
                     "RemovePackage", "AddPackage", "RegisterPackage", "ProvisionPackage", "Deprovision",
                     "DeployPackage", "SetPackageStatus", "Process.Start", "ProcessStartInfo",
                     "LibraryImport", "powershell", "pwsh", "Get-AppxPackage", "winget", "cmd.exe", "runas",
                     "requireAdministrator", "FindPackages()", "FindPackage(",
                     "FindPackagesForUser(sid", "System.Management"
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FindPackagesForUserWithPackageTypes(string.Empty, Types)", source, StringComparison.Ordinal);
        Assert.Contains("manager.FindPackagesWithPackageTypes(Types)", source, StringComparison.Ordinal);
        Assert.Contains("manager.FindProvisionedPackages", source, StringComparison.Ordinal);
        Assert.Contains("WindowsBuiltInRole.Administrator", source, StringComparison.Ordinal);
        Assert.Contains("OpenPackageInfoByFullName", source, StringComparison.Ordinal);
        Assert.Contains("GetPackageInfo", source, StringComparison.Ordinal);
        Assert.Contains("ClosePackageInfo", source, StringComparison.Ordinal);
        Assert.Equal(3, source.Split("[DllImport(\"kernel32.dll\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("IDebloatNativeInventoryReader", source, StringComparison.Ordinal);
        Assert.Contains("ReadOnly => true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeProjectTargetsWinRtAndReferencesOnlyDomain()
    {
        var document = XDocument.Load(TestRepository.Combine("src", "SentinelCore.Infrastructure.Debloat.Native",
            "SentinelCore.Infrastructure.Debloat.Native.csproj"));
        Assert.StartsWith("net10.0-windows10.0.", document.Descendants("TargetFramework").Single().Value);
        Assert.Contains(document.Descendants().Select(element => element.Name.LocalName), name => name == "SupportedOSPlatformVersion");
        var references = document.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty).ToArray();
        Assert.Single(references);
        Assert.Contains("SentinelCore.Domain", references[0], StringComparison.Ordinal);
        Assert.DoesNotContain(references, reference => reference.Contains("Application", StringComparison.Ordinal) ||
            reference.Contains("Persistence", StringComparison.Ordinal) ||
            reference.Contains("Infrastructure.Windows", StringComparison.Ordinal));
    }

    [Fact]
    public void DistributionAppComposesOnlyTheNativeReaderAndDefersQueries()
    {
        var project = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "SentinelCore.App.csproj"));
        Assert.Contains("Infrastructure.Debloat.Native", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Infrastructure.Debloat.Removal.Native", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SentinelCore.Infrastructure.Debloat", project, StringComparison.Ordinal);
        var app = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));
        Assert.Contains("new DebloatInventoryObserver(debloatCatalog, new NativeAppxInventoryReader())", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ObserveAsync(", app, StringComparison.Ordinal);
        Assert.Contains("SyntheticDebloatInventory", app, StringComparison.Ordinal);
    }

    [Fact]
    public void TestProjectNeverReferencesOrExecutesNativeAdapter()
    {
        var project = File.ReadAllText(TestRepository.Combine("tests", "SentinelCore.Tests", "SentinelCore.Tests.csproj"));
        Assert.DoesNotContain("Infrastructure.Debloat.Native", project, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory,
            "SentinelCore.Infrastructure.Debloat.Native.dll")));
        Assert.DoesNotContain(typeof(DebloatNativeBoundaryTests).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.Contains("Debloat.Native", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void SolutionCompilesNativeAdapter()
    {
        var solution = File.ReadAllText(TestRepository.Combine("SentinelCore.sln"));
        Assert.Contains("SentinelCore.Infrastructure.Debloat.Native", solution, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--enable-native-debloat-inventory")]
    [InlineData("--enable-real-debloat")]
    [InlineData("SentinelCoreRealDebloat=true")]
    [InlineData("")]
    public void NativeActivationGateIsNeverOpen(string argument)
    {
        Assert.False(NativeDebloatInventoryGate.Operational);
        Assert.False(NativeDebloatInventoryGate.AllowsActivation([argument]));
        Assert.False(NativeDebloatInventoryGate.AllowsActivation());
    }

    [Fact]
    public void AllRealExecutionGatesRemainClosed()
    {
        Assert.False(RealDebloatBuildGate.RealExecutionImplemented);
        Assert.False(RealDebloatBuildGate.AllowsRealExecution());
        Assert.False(RealDebloatBuildGate.AllowsRealExecution(["--enable-real-debloat"]));
        Assert.False(RealTweakBuildGate.RealExecutionCompiled);
        Assert.False(RealTweakBuildGate.AllowsRealExecution([]));
        Assert.False(RealTweakBuildGate.AllowsRealExecution([RealTweakBuildGate.EnableArgument]));
        Assert.False(RealRepairBuildGate.RealExecutionCompiled);
        Assert.False(RealRepairBuildGate.RealExecutionImplemented);
        Assert.False(RealRepairBuildGate.AllowsRealExecution([RealRepairBuildGate.EnableArgument]));
    }

    [Fact]
    public void NativeInventoryExposesReadAndPreviewWithoutMutation()
    {
        var document = XDocument.Parse(File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App",
            "DebloatView.xaml")));
        var status = document.Descendants(Presentation + "TextBlock")
            .Single(item => (string?)item.Attribute("AutomationProperties.AutomationId") == "DebloatNativeInventoryStatus");
        var statusText = UiResourceText.ResolveAttribute(status, "Text");
        Assert.Contains("Consulta ainda não executada", statusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("somente leitura", statusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), item =>
            (string?)item.Attribute("AutomationProperties.AutomationId") == "DebloatNativeInventoryStates");
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), item =>
            UiResourceText.ResolveAttribute(item, "Text") == DebloatLimits.SyntheticWarning);
        var buttons = document.Descendants(Presentation + "Button")
            .Select(item => UiResourceText.ResolveAttribute(item, "Content")).ToArray();
        Assert.Contains("Consultar inventário", buttons);
        Assert.Contains("Ver prévia de remoção", buttons);
        Assert.DoesNotContain(buttons, label => label.Contains("remover", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("desprovisionar", StringComparison.OrdinalIgnoreCase));
    }
}
