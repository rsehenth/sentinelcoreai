using SentinelCore.Application;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// O gate do Repair Center é independente do gate dos Tweaks. Nesta fase nem o build experimental compõe
/// executor real: <see cref="RealRepairBuildGate.RealExecutionImplemented"/> permanece falso.
/// </summary>
public sealed class RepairDistributionGateTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void LaunchPolicyRequiresIndependentCompilationImplementationAndArgument(bool compiled,
        bool implemented, bool argumentPresent, bool expected)
    {
        string[] arguments = argumentPresent ? [RealRepairBuildGate.EnableArgument] : [];

        Assert.Equal(expected, RealRepairLaunchPolicy.Resolve(compiled, implemented, arguments));
    }

    [Fact]
    public void LaunchPolicyRejectsArgumentVariants()
    {
        Assert.False(RealRepairLaunchPolicy.Resolve(true, true, ["--enable-real-repair"]));
        Assert.False(RealRepairLaunchPolicy.Resolve(true, true, ["--enable-real-repairs=true"]));
        Assert.False(RealRepairLaunchPolicy.Resolve(true, true, ["--ENABLE-REAL-REPAIRS"]));
        Assert.False(RealRepairLaunchPolicy.Resolve(true, true, ["--data-root", "C:\\temp"]));
    }

    [Fact]
    public void RepositoryDefaultDisablesTheIndependentRepairGate()
    {
        var document = System.Xml.Linq.XDocument.Load(
            TestRepository.Combine("Directory.Build.props"));
        var value = document.Descendants("SentinelCoreRealRepairs").FirstOrDefault()?.Value.Trim();

        Assert.Equal("false", value);
    }

    [Fact]
    public void OnlyTheBuildGateParsesTheRepairOptInArgument()
    {
        var separator = Path.DirectorySeparatorChar;
        var sources = Directory.GetFiles(TestRepository.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains(RealRepairBuildGate.EnableArgument,
                StringComparison.Ordinal))
            .ToList();

        var gate = Assert.Single(sources);
        Assert.Equal("RealRepairBuildGate.cs", Path.GetFileName(gate));
    }

    [Fact]
    public void DistributionBuildNeverEnablesRealRepairs()
    {
        Assert.False(RealRepairBuildGate.RealExecutionCompiled);
        Assert.False(RealRepairBuildGate.RealExecutionImplemented);
        Assert.False(RealRepairBuildGate.AllowsRealExecution([]));
        Assert.False(RealRepairBuildGate.AllowsRealExecution([RealRepairBuildGate.EnableArgument]));
        Assert.False(RealRepairBuildGate.AllowsRealExecution(
            ["--data-root", "C:\\isolated", RealRepairBuildGate.EnableArgument]));
    }

    [Fact]
    public void AppDoesNotComposeAnyRealRepairExecutorOrParseTheRepairArgument()
    {
        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));

        Assert.DoesNotContain(RealRepairBuildGate.EnableArgument, source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsRepairProcessExecutor", source, StringComparison.Ordinal);
        Assert.Contains("new SyntheticRepairExecutor()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishScriptsDoNotEnableRealRepairs()
    {
        var publish = File.ReadAllText(TestRepository.Combine("scripts", "publish.ps1"));
        var validation = File.ReadAllText(TestRepository.Combine("scripts", "publish-validation.ps1"));

        Assert.DoesNotContain("SentinelCoreRealRepairs=true", publish, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SentinelCoreRealRepairs=true", validation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RealRepairBuildGate.EnableArgument, publish, StringComparison.Ordinal);
        Assert.DoesNotContain(RealRepairBuildGate.EnableArgument, validation, StringComparison.Ordinal);
    }
}
