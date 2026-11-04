using SentinelCore.Application;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Separação verificável entre o build de distribuição e o build experimental de validação da T2A. No build
/// de distribuição a escrita real não é compilada, então nenhum argumento, configuração ou chamada interna
/// consegue compor o executor HKCU.
/// </summary>
public sealed class RealTweakDistributionGateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void LaunchPolicyRequiresTheExperimentalBuildAndTheExplicitArgument(bool compiled,
        bool argumentPresent, bool expected)
    {
        string[] arguments = argumentPresent ? [RealTweakBuildGate.EnableArgument] : [];

        Assert.Equal(expected, RealTweakLaunchPolicy.Resolve(compiled, arguments));
    }

    [Fact]
    public void LaunchPolicyRejectsArgumentVariantsAndPartialMatches()
    {
        Assert.False(RealTweakLaunchPolicy.Resolve(true, ["--enable-real-tweak"]));
        Assert.False(RealTweakLaunchPolicy.Resolve(true, ["--enable-real-tweaks=true"]));
        Assert.False(RealTweakLaunchPolicy.Resolve(true, ["--ENABLE-REAL-TWEAKS"]));
        Assert.False(RealTweakLaunchPolicy.Resolve(true, ["--data-root", @"C:\temp"]));
    }

    [Fact]
    public void RepositoryDefaultIsTheDistributionBuild()
    {
        var document = System.Xml.Linq.XDocument.Load(
            TestRepository.Combine("Directory.Build.props"));
        var value = document.Descendants("SentinelCoreRealTweaks").FirstOrDefault()?.Value.Trim();

        Assert.Equal("false", value);
    }

    [Fact]
    public void OnlyTheBuildGateParsesTheRealExecutionArgument()
    {
        var separator = Path.DirectorySeparatorChar;
        var sources = Directory.GetFiles(TestRepository.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains(RealTweakBuildGate.EnableArgument,
                StringComparison.Ordinal))
            .ToList();

        var gate = Assert.Single(sources);
        Assert.Equal("RealTweakBuildGate.cs", Path.GetFileName(gate));
    }

    [Fact]
    public void AppCompositionUsesTheBuildGateInsteadOfReadingArgumentsDirectly()
    {
        var app = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));

        Assert.Contains("RealTweakBuildGate.AllowsRealExecution(e.Args)", app, StringComparison.Ordinal);
        Assert.DoesNotContain(RealTweakBuildGate.EnableArgument, app, StringComparison.Ordinal);
    }

#if !SENTINELCORE_REAL_TWEAKS
    [Fact]
    public void DistributionBuildDoesNotCompileRealExecution() =>
        Assert.False(RealTweakBuildGate.RealExecutionCompiled);

    [Fact]
    public void DistributionBuildIgnoresTheOptInArgumentFromTheCommandLine()
    {
        Assert.False(RealTweakBuildGate.AllowsRealExecution([]));
        Assert.False(RealTweakBuildGate.AllowsRealExecution([RealTweakBuildGate.EnableArgument]));
        Assert.False(RealTweakBuildGate.AllowsRealExecution(
            ["--data-root", @"C:\isolated", RealTweakBuildGate.EnableArgument]));
        Assert.False(RealTweakBuildGate.AllowsRealExecution(
            [RealTweakBuildGate.EnableArgument.ToUpperInvariant()]));
    }
#else
    [Fact]
    public void ExperimentalBuildCompilesRealExecutionButStillRequiresTheArgument()
    {
        Assert.True(RealTweakBuildGate.RealExecutionCompiled);
        Assert.False(RealTweakBuildGate.AllowsRealExecution([]));
        Assert.True(RealTweakBuildGate.AllowsRealExecution([RealTweakBuildGate.EnableArgument]));
    }
#endif
}
