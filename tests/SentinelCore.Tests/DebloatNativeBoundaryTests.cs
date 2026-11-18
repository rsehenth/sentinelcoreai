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
    public void DistributionAppDoesNotReferenceOrComposeTheNativeReader()
    {
        // 7B.6: the previous expectation (App composes NativeAppxInventoryReader) contradicted the closed gate.
        var project = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "SentinelCore.App.csproj"));
        Assert.DoesNotContain("Debloat.Native", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Infrastructure.Debloat.Removal.Native", project, StringComparison.OrdinalIgnoreCase);
        // Módulo Aplicativos e Inicialização: o App não referencia mais o projeto legado de Debloat.
        Assert.DoesNotContain("Infrastructure.Debloat", project, StringComparison.Ordinal);
        Assert.Contains("SentinelCore.Infrastructure.AppsStartup", project, StringComparison.Ordinal);
        var app = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));
        Assert.DoesNotContain("NativeAppxInventoryReader", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Infrastructure.Debloat.Native", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ObserveAsync(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Debloat", app, StringComparison.Ordinal);
        Assert.Contains("new AppsStartupManager(syntheticStartup, syntheticApps, syntheticStartup, syntheticApps,", app, StringComparison.Ordinal);
        Assert.Contains("operationStore: store);", app, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheNativeProjectItselfCanReferenceTheNativeInventoryAssembly()
    {
        var offenders = new[] { "src", "tests", "scripts" }
            .SelectMany(folder => Directory.EnumerateFiles(TestRepository.Combine(folder), "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".csproj", StringComparison.Ordinal) || file.EndsWith(".props", StringComparison.Ordinal) ||
                           file.EndsWith(".targets", StringComparison.Ordinal))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(file => File.ReadAllText(file).Contains("Debloat.Native", StringComparison.OrdinalIgnoreCase) &&
                           !file.EndsWith("SentinelCore.Infrastructure.Debloat.Native.csproj", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offenders);
        foreach (var rootFile in Directory.EnumerateFiles(TestRepository.Combine(), "Directory.Build.*", SearchOption.TopDirectoryOnly))
            Assert.DoesNotContain("Debloat.Native", File.ReadAllText(rootFile), StringComparison.OrdinalIgnoreCase);
        // Lock files of the App and of the benchmark hosts (which reference the App) must not list it either.
        foreach (var lockFile in new[] { new[] { "src", "SentinelCore.App", "packages.lock.json" },
                     new[] { "src", "SentinelCore.App", "packages.single-file.lock.json" },
                     new[] { "scripts", "RecoveryBenchmark", "packages.lock.json" } })
            Assert.DoesNotContain("Debloat.Native", File.ReadAllText(TestRepository.Combine(lockFile)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppSourcesContainNoNativeInventoryTypeOrWinRtDeploymentApi()
    {
        var sources = Directory.EnumerateFiles(TestRepository.Combine("src", "SentinelCore.App"), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        Assert.NotEmpty(sources);
        foreach (var forbidden in new[] { "NativeAppxInventoryReader", "Infrastructure.Debloat.Native", "PackageManager",
                     "Windows.Management.Deployment", "FindPackages", "FindProvisionedPackages", "FindUsers",
                     "IDebloatNativeInventoryReader" })
            Assert.DoesNotContain(sources, file => File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void NativeObservationHasNoCallerInTheApp()
    {
        // O painel Debloat foi retirado do produto: ObserveAsync não possui mais nenhum chamador no App.
        var callers = Directory.EnumerateFiles(TestRepository.Combine("src", "SentinelCore.App"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(file => File.ReadAllText(file).Contains("ObserveAsync(", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(callers);
        Assert.False(File.Exists(TestRepository.Combine("src", "SentinelCore.App", "DebloatView.xaml.cs")));
    }

    [Fact]
    public void DisabledNativeInventoryMessageIsClearInBothLanguages()
    {
        const string message = "Inventário nativo Appx/MSIX desabilitado nesta distribuição; nenhuma consulta ao Windows é feita.";
        var english = SentinelCore.App.Localization.Strings.English(message);
        Assert.NotEqual(message, english);
        Assert.Contains("disabled", english, StringComparison.Ordinal);
        Assert.Contains("no Windows query", english, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeGateAndAllOtherRealGatesRemainClosedAndT0IsUntouched()
    {
        Assert.False(NativeDebloatInventoryGate.Operational);
        Assert.False(NativeDebloatInventoryGate.AllowsActivation());
        Assert.False(RealDebloatBuildGate.AllowsRealExecution());
        Assert.False(DebloatRemovalBuildGate.AllowsRealExecution());
        Assert.False(RealRepairBuildGate.AllowsRealExecution(["--enable-real-repairs"]));
        var app = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));
        Assert.Contains("new SyntheticRepairExecutor()", app, StringComparison.Ordinal);
        Assert.Contains("153 NÃO VALIDADOS / 9 ALTO",
            File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Application", "DebloatRemovalPresentation.cs")),
            StringComparison.Ordinal);
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
    public void DebloatInterfaceIsRemovedFromTheApp()
    {
        foreach (var removed in new[] { "DebloatView.xaml", "DebloatView.xaml.cs", "DebloatRemovalView.xaml",
                     "DebloatRemovalView.xaml.cs", "DebloatFilters.cs" })
            Assert.False(File.Exists(TestRepository.Combine("src", "SentinelCore.App", removed)), removed);
        var shell = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.xaml"));
        Assert.DoesNotContain("Debloat", shell, StringComparison.Ordinal);
        Assert.Contains("AppsStartupSection", shell, StringComparison.Ordinal);
    }
}
