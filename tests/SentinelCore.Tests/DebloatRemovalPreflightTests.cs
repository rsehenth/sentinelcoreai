using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class DebloatRemovalPreflightTests
{
    [Theory]
    [InlineData("full-name")]
    [InlineData("wildcard")]
    [InlineData("path")]
    [InlineData("friendly-name")]
    [InlineData("publisher")]
    [InlineData("publisher-id")]
    [InlineData("version")]
    [InlineData("architecture")]
    [InlineData("sid")]
    [InlineData("other-user")]
    [InlineData("unknown-catalog")]
    [InlineData("catalog-version")]
    [InlineData("catalog-hash")]
    [InlineData("protected")]
    [InlineData("unknown-protection")]
    [InlineData("system-package")]
    [InlineData("framework")]
    [InlineData("resource")]
    [InlineData("optional")]
    [InlineData("bundle")]
    [InlineData("provisioned")]
    [InlineData("unknown-provisioned")]
    [InlineData("machine-scope")]
    [InlineData("unavailable-installation")]
    [InlineData("partial")]
    [InlineData("not-executed")]
    [InlineData("denied")]
    [InlineData("interrupted")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("dependency-unknown")]
    [InlineData("dependency-reverse")]
    [InlineData("dependency-shared")]
    [InlineData("dependency-direct")]
    [InlineData("indirect-removal")]
    [InlineData("effects-unknown")]
    [InlineData("consequence-unknown")]
    [InlineData("reinstall-unknown")]
    [InlineData("data-loss")]
    [InlineData("consequences-changed")]
    [InlineData("windows-10")]
    [InlineData("old-build")]
    [InlineData("executor-unavailable")]
    [InlineData("native-observation")]
    [InlineData("unknown-query-enum")]
    public async Task MandatoryUnknownOrContradictoryEvidenceBlocksBeforeAuthorization(string mutation)
    {
        using var scope = new DebloatRemovalTestScope();
        var o = scope.Adapter.Observation;
        var p = o.Packages[0];
        DebloatRemovalObservation Package(DebloatNativePackageMetadata changed) => o with { Packages = [changed] };
        scope.Adapter.Observation = mutation switch
        {
            "full-name" => o with { SelectedPackage = o.SelectedPackage with { PackageFullName = p.TechnicalName + "_suffix" } },
            "wildcard" => o with { SelectedPackage = o.SelectedPackage with { PackageFullName = "*" } },
            "path" => o with { SelectedPackage = o.SelectedPackage with { PackageFullName = @"C:\arbitrary" } },
            "friendly-name" => o with { SelectedPackage = o.SelectedPackage with { PackageFullName = "Clipchamp" } },
            "publisher" => Package(p with { Publisher = "CN=Different" }),
            "publisher-id" => Package(p with { PublisherId = "different0000" }),
            "version" => Package(p with { Version = "2.0.0.0" }),
            "architecture" => Package(p with { Architecture = "x86" }),
            "sid" => o with { CurrentUserSecurityId = "not-a-SID" },
            "other-user" => Package(p with { UserSecurityId = "S-1-5-21-100-200-300-1002" }),
            "unknown-catalog" => o with { CatalogId = "SC-APP-UNKNOWN" },
            "catalog-version" => o with { CatalogVersion = "unknown" },
            "catalog-hash" => o with { CatalogHash = "mismatch" },
            "protected" => o with { Protected = true },
            "unknown-protection" => o with { Protected = null },
            "system-package" => o with { SystemPackage = true },
            "framework" => Package(p with { IsFramework = true }),
            "resource" => Package(p with { IsResourcePackage = true }),
            "optional" => Package(p with { IsOptional = true }),
            "bundle" => Package(p with { IsBundle = true }),
            "provisioned" => Package(p with { Provisioned = true }),
            "unknown-provisioned" => Package(p with { Provisioned = null }),
            "machine-scope" => Package(p with { Scope = DebloatScope.AllUsers }),
            "unavailable-installation" => Package(p with { InstallState = DebloatNativeInstallState.Degraded }),
            "partial" => o with { Completeness = DebloatObservationCompleteness.Partial },
            "not-executed" => o with { QueryState = DebloatInventoryQueryState.NotExecuted },
            "denied" => o with { QueryState = DebloatInventoryQueryState.AccessDenied },
            "interrupted" => o with { QueryState = DebloatInventoryQueryState.Interrupted },
            "empty" => o with { Packages = [] },
            "duplicate" => o with { Packages = [p, p with { Version = "2.0.0.0" }] },
            "dependency-unknown" => o with { Dependencies = o.Dependencies with { Completeness = DebloatObservationCompleteness.Partial } },
            "dependency-reverse" => o with { Dependencies = o.Dependencies with { Reverse = [o.SelectedPackage] } },
            "dependency-shared" => o with { Dependencies = o.Dependencies with { Shared = [o.SelectedPackage] } },
            "dependency-direct" => o with { Dependencies = o.Dependencies with { Direct = [o.SelectedPackage] } },
            "indirect-removal" => o with { Dependencies = o.Dependencies with { PotentiallyAffected = [o.SelectedPackage] } },
            "effects-unknown" => o with { Dependencies = o.Dependencies with { EffectsKnown = false } },
            "consequence-unknown" => o with { Dependencies = o.Dependencies with { UnknownConsequences = ["Unknown"] } },
            "reinstall-unknown" => o with { ReinstallationDocumented = false },
            "data-loss" => o with { DataLossAcknowledged = false },
            "consequences-changed" => o with { Consequences = ["Removal is safe"] },
            "windows-10" => o with { IsWindows11 = false },
            "old-build" => o with { WindowsBuild = 19045 },
            "executor-unavailable" => o with { ExecutorAvailable = false },
            "native-observation" => o with { Synthetic = false },
            "unknown-query-enum" => o with { QueryState = (DebloatInventoryQueryState)999 },
            _ => throw new InvalidOperationException()
        };
        var result = DebloatRemovalPreflightEngine.Evaluate(scope.Catalog, scope.Adapter.Observation);
        Assert.False(result.CanSimulate);
        Assert.False(result.CanExecuteReal);
        Assert.NotEmpty(result.Blockers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Prepare());
        Assert.Equal(0, scope.Adapter.ExecutionCount);
    }

    [Fact]
    public async Task CompleteSyntheticEvidenceDoesNotChangeRealPolicy()
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare();
        Assert.True(plan.Preflight.CanSimulate);
        Assert.False(plan.Preflight.CanExecuteReal);
        Assert.Equal(RiskLevel.NotValidated, plan.Preflight.RealRisk);
        Assert.Equal(153, scope.Catalog.All.Count(e => e.RealRisk == RiskLevel.NotValidated));
        Assert.Equal(9, scope.Catalog.All.Count(e => e.RealRisk == RiskLevel.High));
        Assert.Contains(plan.Preflight.RealBlockers, s => s.Contains("Build gate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MediumFixtureRequiresAdditionalReview()
    {
        using var scope = new DebloatRemovalTestScope("Microsoft.WindowsNotepad");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Prepare());
        var reviewed = await scope.Manager.PrepareAsync(mediumReviewConfirmed: true);
        Assert.True(reviewed.Preflight.CanSimulate);
        Assert.False(reviewed.Preflight.CanExecuteReal);
    }

    [Theory]
    [InlineData("x64", "1.0.0.0", true)]
    [InlineData("arm64", "65535.0.0.0", true)]
    [InlineData("unknown", "1.0.0.0", false)]
    [InlineData("x64", "65536.0.0.0", false)]
    [InlineData("x64", "1.0", false)]
    [InlineData("x64", "1.0.0.0.0", false)]
    public void FullNameMustExactlyEncodeVersionArchitectureAndPublisher(string architecture, string version, bool valid)
    {
        using var scope = new DebloatRemovalTestScope();
        var p = scope.Adapter.Observation.SelectedPackage with { Version = version, Architecture = architecture };
        p = p with { PackageFullName = $"{p.TechnicalName}_{version}_{architecture}__{p.PublisherId}" };
        Assert.Equal(valid, DebloatRemovalIdentityPolicy.IsValid(p));
        Assert.False(DebloatRemovalIdentityPolicy.IsValid(p with { PackageFullName = p.PackageFullName + "_extra" }));
    }
}
