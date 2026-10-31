using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class HardwareRunner
{
    private readonly IReadOnlyList<IHardwareCollector> collectors;
    private readonly TimeProvider clock;

    public HardwareRunner(IEnumerable<IHardwareCollector> collectors, TimeProvider? clock = null)
    {
        this.collectors = collectors.ToArray();
        if (this.collectors.Count == 0 ||
            this.collectors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != this.collectors.Count)
            throw new ArgumentException("Coletores de hardware devem ter identificadores únicos e não podem estar vazios.",
                nameof(collectors));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<HardwareSnapshot> RunAsync(IProgress<HardwareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var duration = Stopwatch.StartNew();
        var session = new HardwareCollectionSession();
        var results = new List<HardwareCollectorResult>();
        foreach (var collector in collectors)
        {
            var collectorStart = clock.GetUtcNow();
            var timer = Stopwatch.StartNew();
            HardwareCollectorResult result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(collector.Id, HardwareCollectionStatus.Running, results.Count, collectors.Count));
                result = await collector.CollectAsync(id, session, cancellationToken).ConfigureAwait(false);
                if (result.CollectorId != collector.Id || result.Category != collector.Category ||
                    result.Status is HardwareCollectionStatus.Running || result.Items is null || result.Limitations is null)
                    throw new InvalidDataException("Contrato inválido retornado pelo coletor de hardware.");
                if (result.Status == HardwareCollectionStatus.Completed && result.Limitations.Count > 0)
                    result = result with { Status = HardwareCollectionStatus.Partial };
                if (result.Status is HardwareCollectionStatus.Completed or HardwareCollectionStatus.Partial &&
                    (result.StartedUtc > result.CompletedUtc || result.Duration < TimeSpan.Zero))
                    throw new InvalidDataException("Tempos inválidos retornados pelo coletor de hardware.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new(collector.Id, collector.Version, collector.Category, collector.Source, collectorStart,
                    clock.GetUtcNow(), timer.Elapsed, HardwareCollectionStatus.Cancelled, [],
                    ["Coleta cancelada pelo usuário."]);
            }
            catch (Exception error)
            {
                result = new(collector.Id, collector.Version, collector.Category, collector.Source, collectorStart,
                    clock.GetUtcNow(), timer.Elapsed, HardwareCollectionStatus.Failed, [],
                    [$"Falha de coleta ({error.GetType().Name}): {error.Message}"]);
            }
            results.Add(result);
            progress?.Report(new(collector.Id, result.Status, results.Count, collectors.Count));
        }
        var status = OverallStatus(results, cancellationToken.IsCancellationRequested);
        return new(1, id, SoftwareInfo.Version, SoftwareInfo.SnapshotsSchemaVersion, started, clock.GetUtcNow(),
            duration.Elapsed, status, results);
    }

    private static HardwareCollectionStatus OverallStatus(IReadOnlyList<HardwareCollectorResult> results, bool cancelled)
    {
        if (cancelled || results.Any(r => r.Status == HardwareCollectionStatus.Cancelled))
            return HardwareCollectionStatus.Cancelled;
        if (results.All(r => r.Status == HardwareCollectionStatus.Completed))
            return HardwareCollectionStatus.Completed;
        if (results.All(r => r.Status == HardwareCollectionStatus.Failed))
            return HardwareCollectionStatus.Failed;
        if (results.All(r => r.Status == HardwareCollectionStatus.Unsupported))
            return HardwareCollectionStatus.Unsupported;
        return HardwareCollectionStatus.Partial;
    }
}

public sealed class HardwareCollectionSession : IHardwareCollectionSession
{
    private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

    public T GetOrAdd<T>(string key, Func<T> factory) where T : class
    {
        if (values.TryGetValue(key, out var existing)) return (T)existing;
        var created = factory();
        values[key] = created;
        return created;
    }
}
