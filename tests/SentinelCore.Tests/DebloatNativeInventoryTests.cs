using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// T3B contracts exercised exclusively with synthetic observations. No Windows package API is called here.
/// </summary>
public sealed class DebloatNativeInventoryTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private static readonly DebloatCatalog Catalog = DebloatCatalog.CreateDefault();
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidNativeIdentityIsAccepted()
    {
        Assert.True(DebloatNativeIdentity.IsValid(Metadata()));
        Assert.True(DebloatNativeIdentity.IsValid(Metadata(architecture: "arm64")));
    }

    [Fact]
    public void InvalidNativeIdentityIsRejected()
    {
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(technicalName: "")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(technicalName: "Clipchamp*")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(technicalName: "Clip/champ")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(packageFamilyName: "Other_8wekyb3d8bbwe")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(publisherId: "SHORT")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(version: "not-a-version")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(architecture: "sparc")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(userSecurityId: "user")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(scope: DebloatScope.OtherUser)));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(source: "")));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(kind: (DebloatPackageKind)999)));
        Assert.False(DebloatNativeIdentity.IsValid(Metadata(publisher: "CN=Bad\u0001Publisher")));
    }

    [Fact]
    public async Task CompletedQuerySeparatesObservedPresenceFromConfirmedAbsence()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete, Metadata()));

        Assert.True(observation.AbsenceConfirmed);
        Assert.Equal(1, observation.ObservedCount);
        Assert.Equal(161, observation.CatalogOnlyCount);
        var observed = Assessment(observation, "Clipchamp.Clipchamp");
        Assert.Equal(DebloatCatalogMatchKind.Exact, observed.MatchKind);
        Assert.Equal(DebloatPresence.PresentInNativeInventory, observed.Presence);
        Assert.Equal(DebloatEligibility.UnavailableForSimulation, observed.Eligibility);
        Assert.False(observed.CanSimulate);
        Assert.False(observed.CanExecuteReal);
        var absent = Assessment(observation, "Microsoft.BingWeather");
        Assert.Equal(DebloatCatalogMatchKind.CatalogOnly, absent.MatchKind);
        Assert.Equal(DebloatPresence.AbsentInConsultedInventory, absent.Presence);
        Assert.Contains(DebloatNativeMessages.ObservedAbsent, absent.Limitations);
        Assert.DoesNotContain(DebloatNativeMessages.Unobserved, absent.Limitations);
        Assert.False(absent.CanSimulate);
    }

    [Fact]
    public async Task CompletedQueryWithoutResultsIsAnObservedEmptyInventory()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.CompletedNoResults,
            DebloatObservationCompleteness.Complete));

        Assert.True(observation.AbsenceConfirmed);
        Assert.Equal(0, observation.ObservedCount);
        Assert.Equal(162, observation.CatalogOnlyCount);
        Assert.All(observation.Assessments, item => Assert.Equal(DebloatPresence.AbsentInConsultedInventory, item.Presence));
        Assert.All(observation.Assessments, item => Assert.False(item.CanSimulate || item.CanExecuteReal));
    }

    [Fact]
    public async Task NotExecutedQueryIsNeverAnEmptyInventory()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.NotExecuted,
            DebloatObservationCompleteness.NotObserved));

        Assert.False(observation.AbsenceConfirmed);
        Assert.All(observation.Assessments, item => Assert.Equal(DebloatPresence.NotVerified, item.Presence));
        Assert.Contains(observation.Warnings, warning => warning.Contains("Consulta não executada", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DebloatInventoryQueryState.AccessDenied, DebloatObservationCompleteness.Unavailable)]
    [InlineData(DebloatInventoryQueryState.Failed, DebloatObservationCompleteness.Unavailable)]
    [InlineData(DebloatInventoryQueryState.Unavailable, DebloatObservationCompleteness.Unavailable)]
    [InlineData(DebloatInventoryQueryState.Interrupted, DebloatObservationCompleteness.NotObserved)]
    public async Task IncompleteQueryNeverConfirmsAbsence(DebloatInventoryQueryState state,
        DebloatObservationCompleteness completeness)
    {
        var observation = await ObserveAsync(Query(state, completeness));

        Assert.False(observation.AbsenceConfirmed);
        Assert.All(observation.Assessments, item => Assert.Equal(DebloatPresence.NotVerified, item.Presence));
        Assert.DoesNotContain(observation.Assessments, item => item.Presence == DebloatPresence.AbsentInConsultedInventory);
        Assert.All(observation.Assessments, item => Assert.False(item.CanSimulate || item.CanExecuteReal));
        Assert.Contains(observation.Warnings, warning => warning.Contains("incompleta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PartialQueryKeepsUnobservedApplicationsIndeterminate()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Partial,
            DebloatObservationCompleteness.Partial, Metadata()));

        Assert.False(observation.AbsenceConfirmed);
        Assert.Equal(DebloatPresence.PresentInNativeInventory, Assessment(observation, "Clipchamp.Clipchamp").Presence);
        Assert.All(observation.Assessments.Where(item => item.CatalogEntry?.TechnicalName != "Clipchamp.Clipchamp"),
            item => Assert.Equal(DebloatPresence.Indeterminate, item.Presence));
        Assert.DoesNotContain(observation.Assessments, item => item.Presence == DebloatPresence.AbsentInConsultedInventory);
    }

    [Fact]
    public async Task PartialQueryWithoutResultsIsNotAnEmptyInventory()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Partial,
            DebloatObservationCompleteness.Partial));

        Assert.False(observation.AbsenceConfirmed);
        Assert.All(observation.Assessments, item => Assert.Equal(DebloatPresence.Indeterminate, item.Presence));
        Assert.DoesNotContain(observation.Assessments, item => item.Presence == DebloatPresence.AbsentInConsultedInventory);
    }

    [Fact]
    public async Task NativePreviewUsesScopeCoverageAndDoesNotTreatMissingDependenciesAsEmpty()
    {
        var target = Metadata() with { Signature = DebloatNativeSignature.Store, DependencyFullNames = [] };
        var dependent = Metadata(technicalName: "Contoso.Dependent") with { DependencyFullNames = [target.PackageFullName] };
        var unresolved = Metadata(technicalName: "Contoso.Unresolved");
        var other = target with { Scope = DebloatScope.OtherUser, UserSecurityId = "" };
        var query = Query(DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete, target, dependent, unresolved) with
        {
            AdditionalScopes =
            [
                new(DebloatScope.OtherUser, DebloatInventoryQueryState.Completed,
                    DebloatObservationCompleteness.Complete, [other], []),
                new(DebloatScope.Provisioned, DebloatInventoryQueryState.AccessDenied,
                    DebloatObservationCompleteness.Unavailable, [], [new("DENIED", "Negado.", true)])
            ]
        };
        var observation = await ObserveAsync(query);
        var current = observation.Assessments.Single(item => item.Package is { Scope: DebloatScope.CurrentUser } p &&
            p.PackageFullName == target.PackageFullName);
        var preview = DebloatRemovalPreflightEngine.PreviewNative(observation, current);
        Assert.Null(preview.Package.Provisioned);
        Assert.False(preview.DependenciesComplete);
        Assert.Equal(dependent.PackageFullName, Assert.Single(preview.Dependents));
        Assert.False(preview.Preflight.CanExecuteReal);
        Assert.Equal(RiskLevel.NotValidated, preview.Preflight.RealRisk);
    }

    [Fact]
    public async Task DuplicateObservationsAreAmbiguousAndBlocked()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete, Metadata(), Metadata()));

        var duplicates = observation.Assessments.Where(item => item.Package?.TechnicalName == "Clipchamp.Clipchamp").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, item => Assert.Equal(DebloatCatalogMatchKind.Ambiguous, item.MatchKind));
        Assert.All(duplicates, item => Assert.Equal(DebloatPresence.Indeterminate, item.Presence));
        Assert.All(duplicates, item => Assert.Equal(DebloatEligibility.ProtectedOrBlocked, item.Eligibility));
        Assert.All(duplicates, item => Assert.False(item.CanSimulate || item.CanExecuteReal));
        Assert.Contains(observation.Warnings, warning => warning.Contains("duplicadas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultipleVersionsAreAmbiguousAndBlocked()
    {
        var second = Metadata(version: "2.0.0.0",
            packageFullName: "Clipchamp.Clipchamp_2.0.0.0_x64__8wekyb3d8bbwe");
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete, Metadata(), second));

        var versions = observation.Assessments.Where(item => item.Package?.TechnicalName == "Clipchamp.Clipchamp").ToArray();
        Assert.Equal(2, versions.Length);
        Assert.All(versions, item => Assert.Equal(DebloatCatalogMatchKind.Ambiguous, item.MatchKind));
        Assert.All(versions, item => Assert.Equal(DebloatEligibility.ProtectedOrBlocked, item.Eligibility));
        Assert.Equal(2, observation.ObservedCount);
        Assert.Equal(161, observation.CatalogOnlyCount);
    }

    [Fact]
    public async Task OutOfCatalogApplicationIsIdentifiedButNeverRemovable()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete, Metadata(technicalName: "Contoso.UnknownApp")));

        var item = Assessment(observation, "Contoso.UnknownApp");
        Assert.Equal(DebloatCatalogMatchKind.NotInCatalog, item.MatchKind);
        Assert.Null(item.CatalogEntry);
        Assert.Equal(DebloatPresence.PresentInNativeInventory, item.Presence);
        Assert.Equal(DebloatEligibility.UnavailableForSimulation, item.Eligibility);
        Assert.Contains(item.Blockers, blocker => blocker.Contains("fora do catálogo", StringComparison.Ordinal));
        Assert.Contains(DebloatNativeMessages.OutOfCatalog, item.Limitations);
        Assert.False(item.CanSimulate || item.CanExecuteReal);
    }

    [Theory]
    [InlineData("framework")]
    [InlineData("resource")]
    [InlineData("optional")]
    [InlineData("bundle")]
    [InlineData("high-risk")]
    public async Task ProtectedOrSpecialPackagesAreBlocked(string mutation)
    {
        var high = Catalog.All.First(entry => entry.RealRisk == RiskLevel.High);
        var package = mutation switch
        {
            "framework" => Metadata(kind: DebloatPackageKind.Framework, isFramework: true, isMainPackage: false),
            "resource" => Metadata(kind: DebloatPackageKind.Resource, isResourcePackage: true, isMainPackage: false),
            "optional" => Metadata(isOptional: true, isMainPackage: false),
            "bundle" => Metadata(isBundle: true),
            _ => Metadata(technicalName: high.TechnicalName)
        };
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete, package));

        var item = Assessment(observation, package.TechnicalName);
        Assert.Equal(DebloatEligibility.ProtectedOrBlocked, item.Eligibility);
        Assert.False(item.CanSimulate);
        Assert.False(item.CanExecuteReal);
        Assert.NotEmpty(item.Blockers);
    }

    [Fact]
    public void RealRiskClassificationIsNeverLowered()
    {
        foreach (var entry in Catalog.All)
        {
            var assessment = DebloatNativePolicy.Assess(Catalog, Metadata(technicalName: entry.TechnicalName), 1,
                DebloatInventoryQueryState.Completed);
            Assert.Equal(entry.RealRisk == RiskLevel.High ? RiskLevel.High : RiskLevel.NotValidated,
                assessment.EffectiveRisk);
            Assert.False(assessment.CanSimulate);
            Assert.False(assessment.CanExecuteReal);
        }
    }

    [Fact]
    public async Task DivergentIdentityIsBlockedEvenForACataloguedName()
    {
        var divergent = Metadata(packageFamilyName: "Clipchamp.Clipchamp_0000000000000");
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete, divergent));

        var item = Assessment(observation, "Clipchamp.Clipchamp");
        Assert.Equal(DebloatCatalogMatchKind.IdentityDivergent, item.MatchKind);
        Assert.Equal(DebloatPresence.IdentityDivergent, item.Presence);
        Assert.Equal(DebloatEligibility.ProtectedOrBlocked, item.Eligibility);
        Assert.Contains(item.Blockers, blocker => blocker.Contains("Identidade nativa inválida", StringComparison.Ordinal));
        Assert.False(item.CanSimulate || item.CanExecuteReal);
    }

    [Fact]
    public async Task NativeObservationNeverGrantsSimulationOrExecution()
    {
        var observation = await ObserveAsync(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete, Metadata(), Metadata(technicalName: "Contoso.UnknownApp")));

        Assert.All(observation.Assessments, item => Assert.False(item.CanSimulate));
        Assert.All(observation.Assessments, item => Assert.False(item.CanExecuteReal));
        Assert.All(observation.Assessments, item => Assert.NotEqual(DebloatEligibility.SimulationAvailable, item.Eligibility));
    }

    [Fact]
    public async Task OtherUserScopeIsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(Query(
            DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete,
            Metadata(scope: DebloatScope.OtherUser))));
    }

    [Fact]
    public async Task NonNativeOriginIsRejected()
    {
        var result = Query(DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete) with
        {
            Origin = DebloatInventoryOrigin.Synthetic
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(result));
    }

    [Fact]
    public void NonReadOnlyReaderIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Observer(Query(DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete), readOnly: false));
    }

    [Fact]
    public async Task NotExecutedQueryCannotCarryPackages()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(Query(
            DebloatInventoryQueryState.NotExecuted, DebloatObservationCompleteness.NotObserved, Metadata())));
    }

    [Fact]
    public async Task EmptyResultStateCannotCarryPackages()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(Query(
            DebloatInventoryQueryState.CompletedNoResults, DebloatObservationCompleteness.Complete, Metadata())));
    }

    [Fact]
    public async Task InconsistentCompletenessIsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(Query(
            DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Partial)));
    }

    [Theory]
    [InlineData(DebloatInventoryQueryState.CompletedNoResults, DebloatObservationCompleteness.Partial)]
    [InlineData(DebloatInventoryQueryState.NotExecuted, DebloatObservationCompleteness.Partial)]
    [InlineData(DebloatInventoryQueryState.Partial, DebloatObservationCompleteness.NotObserved)]
    public async Task ContradictoryStateAndCompletenessAreRejected(DebloatInventoryQueryState state,
        DebloatObservationCompleteness completeness)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(Query(state, completeness)));
    }

    [Fact]
    public async Task ProviderTextIsSanitizedInWarnings()
    {
        var result = Query(DebloatInventoryQueryState.Partial, DebloatObservationCompleteness.Partial) with
        {
            Errors = [new DebloatInventoryError("CODE\u0001", "Bad\u0002Message", false)]
        };
        var observation = await ObserveAsync(result);

        Assert.Contains(observation.Warnings, warning => warning.Contains("CODE: BadMessage", StringComparison.Ordinal));
        Assert.DoesNotContain(observation.Warnings, warning => warning.Any(char.IsControl));
    }

    [Fact]
    public async Task InventoryAboveLimitIsRejected()
    {
        var packages = Enumerable.Range(0, DebloatNativeLimits.MaximumPackages + 1)
            .Select(index => Metadata(technicalName: $"Contoso.App{index}")).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObserveAsync(Query(
            DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete, packages)));
    }

    [Fact]
    public async Task QueryErrorsAndLimitationsArePreserved()
    {
        var result = Query(DebloatInventoryQueryState.Partial, DebloatObservationCompleteness.Partial, Metadata()) with
        {
            Errors = [new DebloatInventoryError("PACKAGE_MAP_FAILED", "Pacote não mapeado.", true)],
            Limitations = ["Limitação declarada pelo provedor."]
        };
        var observation = await ObserveAsync(result);

        Assert.Contains(observation.Warnings, warning => warning.Contains("PACKAGE_MAP_FAILED", StringComparison.Ordinal));
        Assert.Contains(observation.Warnings, warning => warning.Contains("Limitação declarada pelo provedor.", StringComparison.Ordinal));
        Assert.False(observation.AbsenceConfirmed);
    }

    [Fact]
    public void DocumentaryCatalogIsPreserved()
    {
        Assert.Equal(162, Catalog.All.Count);
        Assert.Equal(159, Catalog.All.Count(entry => entry.RemovalMethod == "Appx"));
        Assert.Equal(9, Catalog.All.Count(entry => entry.RealRisk == RiskLevel.High));
        Assert.Equal(153, Catalog.All.Count(entry => entry.RealRisk == RiskLevel.NotValidated));
    }

    [Fact]
    public async Task SyntheticInventoryRemainsAvailableAndSeparate()
    {
        var inventory = SyntheticDebloatInventory.Create(Catalog.All);
        var packages = await inventory.ReadAsync();
        Assert.True(inventory.SyntheticOnly);
        Assert.NotEmpty(packages);
        Assert.All(packages, package => Assert.True(package.Synthetic));

        var root = Path.Combine(Path.GetTempPath(), "SentinelCoreNativeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new DebloatManager(Catalog, inventory, new SyntheticDebloatRuntimeFactory(inventory),
                new MemoryStore(root).CreateDebloatJournal(), new FileExclusiveLockFactory(root));
            var loaded = await manager.LoadAsync();
            Assert.Equal(162, loaded.Count);
            Assert.Equal(5, loaded.Count(item => item.CanSimulate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SyntheticManagerRejectsNonSyntheticInventory()
    {
        var root = Path.Combine(Path.GetTempPath(), "SentinelCoreNativeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryStore(root);
            var synthetic = new SyntheticDebloatInventory([]);
            Assert.Throws<InvalidOperationException>(() => new DebloatManager(Catalog, new NonSyntheticInventory(),
                new SyntheticDebloatRuntimeFactory(synthetic), store.CreateDebloatJournal(),
                new FileExclusiveLockFactory(root)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DebloatNativePackageMetadata Metadata(
        string technicalName = "Clipchamp.Clipchamp",
        string? packageFamilyName = null,
        string? packageFullName = null,
        string publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        string publisherId = "8wekyb3d8bbwe",
        string version = "1.0.0.0",
        string architecture = "x64",
        string userSecurityId = Sid,
        DebloatPackageKind kind = DebloatPackageKind.Appx,
        DebloatScope scope = DebloatScope.CurrentUser,
        DebloatNativeInstallState installState = DebloatNativeInstallState.Available,
        bool isFramework = false,
        bool isResourcePackage = false,
        bool? isMainPackage = null,
        bool isOptional = false,
        bool isBundle = false,
        string source = DebloatNativeSources.PackageManager,
        IReadOnlyList<string>? limitations = null)
    {
        packageFamilyName ??= $"{technicalName}_{publisherId}";
        packageFullName ??= $"{technicalName}_{version}_{architecture}__{publisherId}";
        return new DebloatNativePackageMetadata(technicalName, packageFamilyName, packageFullName, publisher,
            publisherId, version, architecture, userSecurityId, kind, scope, installState, null, isFramework,
            isResourcePackage, isMainPackage ?? (!isFramework && !isResourcePackage && !isOptional),
            isOptional, isBundle, source, limitations ?? []);
    }

    private static DebloatInventoryQueryResult Query(DebloatInventoryQueryState state,
        DebloatObservationCompleteness completeness, params DebloatNativePackageMetadata[] packages) =>
        new(DebloatInventoryOrigin.NativeAppx, state, completeness, true, Sid, Now, "test-native-1",
            packages, [], []);

    private static DebloatInventoryObserver Observer(DebloatInventoryQueryResult result, bool readOnly = true) =>
        new(Catalog, new FakeNativeReader(result, readOnly));

    private static Task<DebloatInventoryObservation> ObserveAsync(DebloatInventoryQueryResult result) =>
        Observer(result).ObserveAsync();

    private static DebloatNativeAssessment Assessment(DebloatInventoryObservation observation, string technicalName) =>
        observation.Assessments.Single(item => item.Package?.TechnicalName == technicalName ||
            item.CatalogEntry?.TechnicalName == technicalName);

    private sealed class FakeNativeReader(DebloatInventoryQueryResult result, bool readOnly) : IDebloatNativeInventoryReader
    {
        public bool ReadOnly { get; } = readOnly;
        public string ProviderVersion => "test-native-1";
        public Task<DebloatInventoryQueryResult> ReadCurrentUserAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class NonSyntheticInventory : IDebloatInventory
    {
        public bool SyntheticOnly => false;
        public string CurrentUserId => Sid;
        public Task<IReadOnlyList<DebloatPackage>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DebloatPackage>>([]);
    }
}
