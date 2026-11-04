using System.Reflection;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyArchitectureTests
{
    private static Assembly WindowsAssembly() => Assembly.Load("SentinelCore.Infrastructure.Windows");
    private static Assembly ModificationsAssembly() => Assembly.Load("SentinelCore.Infrastructure.Modifications");
    private static Assembly ApplicationAssembly() => Assembly.Load("SentinelCore.Application");

    [Fact]
    public void DiagnosticCollectorsDoNotReferenceModificationAssembly()
    {
        var references = WindowsAssembly().GetReferencedAssemblies().Select(item => item.Name).ToList();

        Assert.DoesNotContain("SentinelCore.Infrastructure.Modifications", references);
        Assert.DoesNotContain("SentinelCore.Application", references);
        Assert.DoesNotContain("SentinelCore.Persistence", references);
    }

    [Fact]
    public void ApplicationDoesNotReferenceModificationAssembly()
    {
        var references = ApplicationAssembly().GetReferencedAssemblies().Select(item => item.Name).ToList();

        Assert.DoesNotContain("SentinelCore.Infrastructure.Modifications", references);
        Assert.DoesNotContain("System.Management", references);
        Assert.DoesNotContain("Microsoft.Win32.Registry", references);
        Assert.DoesNotContain("PresentationFramework", references);
    }

    [Fact]
    public void DiagnosticRunnersDoNotReceiveExecutionPorts()
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
            typeof(IOperationExecutor), typeof(ITransactionJournalStore), typeof(IExclusiveLockFactory),
            typeof(ITargetStateReader), typeof(IProtectionProvider)
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
    public void ModificationAssemblyExposesOnlyControlledAdapters()
    {
        var publicTypes = ModificationsAssembly().GetExportedTypes().ToList();
        var allowedPrefixes = new[] { "Synthetic", "Hkcu", "IHkcu", "Windows11" };

        Assert.NotEmpty(publicTypes);
        Assert.All(publicTypes, type =>
            Assert.Contains(allowedPrefixes, prefix => type.Name.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.DoesNotContain(publicTypes, type =>
            type.Name.Contains("Machine", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(publicTypes, type =>
            type.Name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(publicTypes, type =>
            type.Name.Contains("Service", StringComparison.OrdinalIgnoreCase) &&
            !type.Name.Contains("Hkcu", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModificationSourceContainsNoScriptExecutionOrAlternateHives()
    {
        var directory = TestRepository.Combine("src", "SentinelCore.Infrastructure.Modifications");
        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[]
                     {
                         "System.Management", "Process.Start", "ProcessStartInfo", "powershell", "DISM",
                         "Appx", "ServiceController", "TaskService", "DllImport", "LibraryImport",
                         "schtasks", "reg.exe", "bcdedit", "RegistryHive", "CreateSubKey", "DeleteSubKey",
                         "OpenBaseKey"
                     })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);

            if (!string.Equals(Path.GetFileName(file), "HkcuRegistryAccess.cs", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("key.SetValue", text, StringComparison.Ordinal);
                Assert.DoesNotContain("key.DeleteValue", text, StringComparison.Ordinal);
            }
        }
        Assert.Empty(Directory.GetFiles(directory, "*.ps1", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory, "*.reg", SearchOption.AllDirectories));
    }

    [Fact]
    public void ModificationAssemblyHasExactlyOneRealExecutorForHkcu()
    {
        var implementations = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "SentinelCore.Application"
                or "SentinelCore.Infrastructure.Modifications")
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IOperationExecutor).IsAssignableFrom(type) && !type.IsInterface)
            .ToList();
        Assert.All(implementations, type => Assert.True(
            type.Name.StartsWith("Synthetic", StringComparison.Ordinal) ||
            type.Name.StartsWith("Hkcu", StringComparison.Ordinal) ||
            type.Name == "DisabledOperationExecutor", type.Name));
        var real = implementations.Where(type =>
            type.Name.StartsWith("Hkcu", StringComparison.Ordinal)).ToList();
        Assert.Single(real);
        Assert.Equal("HkcuRegistryOperationExecutor", real[0].Name);
    }

    [Fact]
    public void SafetyEngineIsIndependentOfWpf()
    {
        var engine = typeof(SafetyEngine);
        var references = engine.Assembly.GetReferencedAssemblies().Select(item => item.Name).ToList();
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("WindowsBase", references);
        Assert.Equal("SentinelCore.Application", engine.Assembly.GetName().Name);
    }
}
