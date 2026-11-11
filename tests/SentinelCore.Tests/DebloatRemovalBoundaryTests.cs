using System.Xml.Linq;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>Source/project inspection only. No native removal assembly is loaded or invoked.</summary>
public sealed class DebloatRemovalBoundaryTests
{
    [Fact]
    public void NativeRemovalIsSeparateDomainOnlyAndWinRtCompatible()
    {
        var project = XDocument.Load(TestRepository.Combine("src", "SentinelCore.Infrastructure.Debloat.Removal.Native",
            "SentinelCore.Infrastructure.Debloat.Removal.Native.csproj"));
        Assert.Equal("net10.0-windows10.0.22621.0", project.Descendants("TargetFramework").Single().Value);
        Assert.Equal("10.0.22000.0", project.Descendants("SupportedOSPlatformVersion").Single().Value);
        Assert.Contains("SentinelCore.Domain", Assert.Single(project.Descendants("ProjectReference")).Attribute("Include")!.Value,
            StringComparison.Ordinal);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Contains("Infrastructure.Debloat.Removal.Native", File.ReadAllText(TestRepository.Combine("SentinelCore.sln")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SentinelCore.App")]
    [InlineData("SentinelCore.Application")]
    [InlineData("SentinelCore.Infrastructure.Debloat")]
    [InlineData("SentinelCore.Infrastructure.Debloat.Native")]
    [InlineData("SentinelCore.Infrastructure.Repairs")]
    [InlineData("SentinelCore.Infrastructure.Modifications")]
    public void ExistingAssembliesNeverReferenceNativeRemoval(string projectName)
    {
        var project = File.ReadAllText(TestRepository.Combine("src", projectName, projectName + ".csproj"));
        Assert.DoesNotContain("Infrastructure.Debloat.Removal.Native", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FunctionalTestsDoNotReferenceLoadOrInvokeNativeRemoval()
    {
        var project = File.ReadAllText(TestRepository.Combine("tests", "SentinelCore.Tests", "SentinelCore.Tests.csproj"));
        Assert.DoesNotContain("Infrastructure.Debloat.Removal.Native", project, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "SentinelCore.Infrastructure.Debloat.Removal.Native.dll")));
        Assert.DoesNotContain(typeof(DebloatRemovalBoundaryTests).Assembly.GetReferencedAssemblies(),
            a => a.Name?.Contains("Removal.Native", StringComparison.Ordinal) == true);
        var app = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));
        Assert.DoesNotContain("NativeAppxRemovalExecutor", app, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCallUsesExactIdentityNoExpandedScopeAndCannotRunBehindGate()
    {
        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Infrastructure.Debloat.Removal.Native", "NativeAppxRemovalExecutor.cs"));
        Assert.Contains("manager.RemovePackageAsync(request.Package.PackageFullName)", source, StringComparison.Ordinal);
        Assert.Contains("manager.DeprovisionPackageForAllUsersAsync(request.Package.PackageFamilyName)", source, StringComparison.Ordinal);
        Assert.Contains("await operation.AsTask()", source, StringComparison.Ordinal);
        Assert.Contains("operation.Progress", source, StringComparison.Ordinal);
        Assert.Equal(3, source.Split("DebloatRemovalBuildGate.AllowsRealExecution()", StringSplitOptions.None).Length - 1);
        foreach (var forbidden in new[] { "RemovalOptions.AllUsers", "RemovalOptions.PreserveApplicationData", "FindPackages", "manager.ProvisionPackage", "AddPackageAsync", "Process.Start", "ProcessStartInfo",
                     "powershell", "winget", "cmd.exe", "runas", "requireAdministrator", "operation.Cancel(", ".WaitAsync(" })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        var inventory = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Infrastructure.Debloat.Native", "NativeAppxInventoryReader.cs"));
        Assert.DoesNotContain("RemovePackage", inventory, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--enable-real-debloat-removal")]
    [InlineData("--enable-real-debloat")]
    [InlineData("SentinelCoreRealDebloat=true")]
    [InlineData("SentinelCoreRealDebloatRemoval=true")]
    [InlineData("")]
    public async Task NoArgumentOrInternalModeEnablesRealExecution(string argument)
    {
        Assert.False(DebloatRemovalBuildGate.AllowsRealExecution([argument]));
        Assert.False(NativeDebloatInventoryGate.Operational);
        Assert.False(RealDebloatBuildGate.AllowsRealExecution([argument]));
        using var scope = new DebloatRemovalTestScope(); var plan = await scope.Prepare();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Manager.ExecuteAsync(plan, scope.Authorize(plan), DebloatExecutionMode.Real));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Manager.ExecuteAsync(plan, scope.Authorize(plan), (DebloatExecutionMode)999));
        Assert.Equal(0, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public void NewPresentationContainsNoActionOrCancelPromise()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(TestRepository.Combine("src", "SentinelCore.App", "DebloatRemovalView.xaml"));
        Assert.Empty(document.Descendants(wpf + "Button"));
        Assert.Contains(document.Descendants(wpf + "TextBlock"), e => UiResourceText.ResolveAttribute(e, "Text") == DebloatLimits.SyntheticWarning);
        Assert.Contains(document.Descendants(wpf + "Setter"), e => (string?)e.Attribute("Property") == "IsReadOnly" &&
            (string?)e.Attribute("Value") == "True");
        var data = DebloatRemovalPresentation.Unavailable();
        Assert.Contains("bloqueada", data.Plan, StringComparison.Ordinal);
        Assert.Contains("NÃO VALIDADOS", data.Risks, StringComparison.Ordinal);
    }
}
