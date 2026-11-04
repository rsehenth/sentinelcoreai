using System.Reflection;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RepairArchitectureTests
{
    private static Assembly RepairsAssembly() => Assembly.Load("SentinelCore.Infrastructure.Repairs");
    private static Assembly ApplicationAssembly() => Assembly.Load("SentinelCore.Application");
    private static Assembly PersistenceAssembly() => Assembly.Load("SentinelCore.Persistence");

    [Fact]
    public void RepairInfrastructureDoesNotReferenceApplicationPersistenceOrWindowsCollectors()
    {
        var references = RepairsAssembly().GetReferencedAssemblies().Select(item => item.Name).ToList();

        Assert.DoesNotContain("SentinelCore.Application", references);
        Assert.DoesNotContain("SentinelCore.Persistence", references);
        Assert.DoesNotContain("SentinelCore.Infrastructure.Windows", references);
        Assert.DoesNotContain("SentinelCore.Infrastructure.Modifications", references);
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("System.Management", references);
    }

    [Fact]
    public void ApplicationAndPersistenceDoNotReferenceRepairInfrastructure()
    {
        Assert.DoesNotContain("SentinelCore.Infrastructure.Repairs",
            ApplicationAssembly().GetReferencedAssemblies().Select(item => item.Name));
        Assert.DoesNotContain("SentinelCore.Infrastructure.Repairs",
            PersistenceAssembly().GetReferencedAssemblies().Select(item => item.Name));
        Assert.DoesNotContain("PresentationFramework",
            ApplicationAssembly().GetReferencedAssemblies().Select(item => item.Name));
    }

    [Fact]
    public void RepairSourceContainsNoProcessExecutionShellOrSystemMutation()
    {
        var directory = TestRepository.Combine("src", "SentinelCore.Infrastructure.Repairs");
        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[]
                     {
                         "Process.Start", "ProcessStartInfo", "System.Diagnostics.Process", "cmd.exe",
                         "powershell", "Start-Process", "bcdedit", "reg.exe", "schtasks", "ServiceController",
                         "DllImport", "LibraryImport", "CreateRemoteThread", "Registry.LocalMachine",
                         "RegistryHive", "SetValue", "CreateSubKey"
                     })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(Directory.GetFiles(directory, "*.ps1", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory, "*.reg", SearchOption.AllDirectories));
    }

    [Fact]
    public void RepairInfrastructureExposesOnlySyntheticAndReadOnlyAdapters()
    {
        var publicTypes = RepairsAssembly().GetExportedTypes().ToList();
        var allowedPrefixes = new[] { "Synthetic", "Windows", "Repair" };

        Assert.NotEmpty(publicTypes);
        Assert.All(publicTypes, type =>
            Assert.Contains(allowedPrefixes, prefix => type.Name.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.DoesNotContain(publicTypes, type =>
            type.Name.Contains("Process", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Service", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlySyntheticExecutorImplementsTheRepairExecutionContract()
    {
        var implementations = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "SentinelCore.Application"
                or "SentinelCore.Infrastructure.Repairs")
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IRepairExecutor).IsAssignableFrom(type) && !type.IsInterface)
            .ToList();

        var implementation = Assert.Single(implementations);
        Assert.Equal("SyntheticRepairExecutor", implementation.Name);
    }

    [Fact]
    public void DiagnosticRunnersDoNotReceiveRepairExecutionPorts()
    {
        var runners = new[]
        {
            typeof(AuditRunner), typeof(HardwareRunner), typeof(EventInvestigationRunner),
            typeof(PerformanceSessionRunner), typeof(NetworkSessionRunner),
            typeof(GraphicsInvestigationRunner), typeof(EvidenceCorrelationRunner),
            typeof(MonitoringSessionRunner)
        };
        var forbidden = new[]
        {
            typeof(IRepairExecutor), typeof(IRepairRecordStore), typeof(IRepairVolumeProvider),
            typeof(IRepairToolLocator)
        };

        foreach (var runner in runners)
        {
            var parameters = runner.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Instance)
                .SelectMany(item => item.GetParameters()).ToList();
            Assert.DoesNotContain(parameters, parameter => forbidden.Contains(parameter.ParameterType));
        }
    }

    [Fact]
    public void ClosedCatalogNeverMakesHighRiskOrDocumentedOperationsExecutable()
    {
        var catalog = RepairCatalog.CreateDefault();

        Assert.DoesNotContain(catalog.All, operation =>
            operation.Risk is RiskLevel.High or RiskLevel.NotValidated);
        Assert.DoesNotContain(catalog.All, operation => operation.IsDocumentedOnly);
        Assert.All(catalog.DocumentedOnly, operation =>
            Assert.False(catalog.TryGet(operation.Id, out _)));
        Assert.All(catalog.All, operation => Assert.True(operation.RequiresElevation ||
            operation.Tool == RepairTool.Reagentc));
    }

    [Fact]
    public void AppCompositionUsesOnlyTheSyntheticRepairExecutor()
    {
        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));

        Assert.Contains("new SyntheticRepairExecutor()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsRepairProcess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RepairProcessExecutor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealRepairBuildGate.AllowsRealExecution", source, StringComparison.Ordinal);
        Assert.Contains("RepairCatalog.CreateDefault()", source, StringComparison.Ordinal);
    }
}
