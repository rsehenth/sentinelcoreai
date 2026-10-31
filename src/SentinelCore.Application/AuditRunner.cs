using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class AuditRunner
{
    private readonly IReadOnlyList<IDiagnosticCollector> collectors;
    private readonly TimeProvider clock;
    public AuditRunner(IEnumerable<IDiagnosticCollector> collectors, TimeProvider? clock = null)
    {
        this.collectors = collectors.ToArray();
        if (this.collectors.Count == 0 || this.collectors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != this.collectors.Count)
            throw new ArgumentException("Coletores obrigatórios devem ter identificadores únicos e não podem estar vazios.", nameof(collectors));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<AuditRecord> RunBasicAsync(IProgress<AuditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var duration = Stopwatch.StartNew();
        var results = new List<CollectorResult>();
        foreach (var collector in collectors)
        {
            var collectorStart = clock.GetUtcNow();
            var timer = Stopwatch.StartNew();
            CollectorResult result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(collector.Id, DiagnosticStatus.Running, results.Count, collectors.Count));
                result = await collector.CollectAsync(id, cancellationToken).ConfigureAwait(false);
                if (result.AuditId != id || result.CollectorId != collector.Id ||
                    result.Status is DiagnosticStatus.Pending or DiagnosticStatus.Running)
                    throw new InvalidDataException("Contrato inválido retornado pelo coletor.");
                if (result.Status == DiagnosticStatus.Completed && (result.Data is null || result.Limitations.Count > 0))
                    result = result with { Status = DiagnosticStatus.Partial };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new(id, collector.Id, collector.Version, collectorStart, clock.GetUtcNow(), timer.Elapsed,
                    DiagnosticStatus.Cancelled, collector.Source, null, ["Coleta cancelada pelo usuário."]);
            }
            catch (Exception error)
            {
                result = new(id, collector.Id, collector.Version, collectorStart, clock.GetUtcNow(), timer.Elapsed,
                    DiagnosticStatus.Failed, collector.Source, null, [$"Falha de coleta ({error.GetType().Name}): {error.Message}"]);
            }
            results.Add(result);
            progress?.Report(new(collector.Id, result.Status, results.Count, collectors.Count));
        }
        var status = cancellationToken.IsCancellationRequested || results.Any(r => r.Status == DiagnosticStatus.Cancelled)
            ? DiagnosticStatus.Cancelled : results.All(r => r.Status == DiagnosticStatus.Completed)
            ? DiagnosticStatus.Completed : results.All(r => r.Status == DiagnosticStatus.Failed)
            ? DiagnosticStatus.Failed : DiagnosticStatus.Partial;
        return new(1, id, AuditKind.Basic, SoftwareInfo.Version, started, clock.GetUtcNow(), duration.Elapsed, status, results);
    }
}
