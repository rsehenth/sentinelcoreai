using SentinelCore.Application;
using SentinelCore.Domain;

namespace RecoveryBenchmark;

internal sealed class HostAuditFixture : IDiagnosticCollector
{
    public string Id => "host-fixture";
    public string Version => "1";
    public string Source => "synthetic";
    public Task<CollectorResult> CollectAsync(Guid auditId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("O host não executa auditoria.");
    public static AuditRunner CreateRunner() => new([new HostAuditFixture()]);
}
