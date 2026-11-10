using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

// Authored during Stage 6; deliberately not executed or added to the audited synthetic selection.
public sealed class ProductIntegrationTests
{
    [Fact]
    public void JourneyOwnsReservationUntilExplicitlyDisposed()
    {
        var locks = new ProbeLocks();
        var first = new ProductOperationCoordinator(locks);
        var second = new ProductOperationCoordinator(locks);
        using (var lease = first.Acquire())
        {
            Assert.True(first.IsBusy);
            Assert.Throws<TargetLockedException>(() => first.Acquire());
            Assert.Throws<TargetLockedException>(() => second.Acquire());
            Assert.False(second.IsBusy);
            Assert.Equal(0, locks.Releases);
        }
        Assert.False(first.IsBusy);
        using var next = second.Acquire();
        Assert.True(second.IsBusy);
        Assert.Equal(1, locks.Releases);
    }

    [Fact]
    public void FailedReservationDoesNotLeaveCoordinatorBusy()
    {
        var locks = new ProbeLocks { Reject = true };
        var coordinator = new ProductOperationCoordinator(locks);
        Assert.Throws<TargetLockedException>(() => coordinator.Acquire());
        Assert.False(coordinator.IsBusy);
        locks.Reject = false;
        using var lease = coordinator.Acquire();
        Assert.True(coordinator.IsBusy);
    }

    [Fact]
    public void DuplicateDisposalCannotReleaseLaterJourney()
    {
        var locks = new ProbeLocks();
        var coordinator = new ProductOperationCoordinator(locks);
        var first = coordinator.Acquire();
        first.Dispose();
        using var second = coordinator.Acquire();
        first.Dispose();
        Assert.True(coordinator.IsBusy);
        Assert.Equal(1, locks.Releases);
        Assert.Throws<TargetLockedException>(() => coordinator.Acquire());
    }

    [Fact]
    public async Task RecoveryCenterIncludesAuditHistoryWithoutUsingMutableAuditStore()
    {
        var reader = new AuditReader();
        var snapshot = await new RecoveryCenter(reader).LoadAsync();
        Assert.Same(reader.Audits, snapshot.Audits);
        Assert.Single(snapshot.InterruptedAudits!.Audits);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(2, snapshot.Audits!.Audits.Count);
    }

    [Fact]
    public async Task UnavailableAuditsPreventCompleteRecoverySummary()
    {
        var reader = new AuditReader { Audits = new([], ["fixture: auditorias indisponíveis"]) };
        var snapshot = await new RecoveryCenter(reader).LoadAsync();
        Assert.False(snapshot.IsComplete);
        Assert.False(RecoveryCenter.Summarize(snapshot).IsComplete);
    }

    [Theory]
    [InlineData(CorrelationSource.Audits)]
    [InlineData(CorrelationSource.Tweaks)]
    public void AuditReferencesExcludeNonDiagnosticModules(CorrelationSource source) =>
        Assert.False(AuditEvidenceReference.IsDiagnosticModule(source));

    private sealed class AuditReader : IRecoveryHistoryReader, IAuditHistoryReader
    {
        public StoreSnapshot Audits { get; set; } = new(
            [Audit(DiagnosticStatus.Partial), Audit(DiagnosticStatus.Interrupted)], []);
        public Task<StoreSnapshot> ReadAuditHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(Audits);
        public Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecoveryHistorySource>>(Enum.GetValues<RecoveryModule>()
                .Select(module => new RecoveryHistorySource(module, RecoveryEvidenceStatus.Complete, [], [], [], [])).ToArray());
        public string GetRecoveryReportPath(RecoveryModule module, Guid id) => "fixture.md";
        private static AuditRecord Audit(DiagnosticStatus status) => new(2, Guid.NewGuid(), AuditKind.Basic,
            SoftwareInfo.Version, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, TimeSpan.Zero, status, []);
    }

    private sealed class ProbeLocks : IExclusiveLockFactory
    {
        private bool held;
        public bool Reject { get; set; }
        public int Releases { get; private set; }
        public IDisposable Acquire(string key)
        {
            if (held || Reject) throw new TargetLockedException("fixture: conflito");
            held = true;
            return new ProbeLease(() => { held = false; Releases++; });
        }
        private sealed class ProbeLease(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }
}
