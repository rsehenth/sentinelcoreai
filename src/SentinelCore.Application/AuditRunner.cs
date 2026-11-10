using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class AuditRunner
{
    private readonly IReadOnlyList<IDiagnosticCollector> collectors;
    private readonly TimeProvider clock;
    private readonly FullAuditDiagnostics? full;
    private readonly IAuditCheckpointStore? checkpoints;
    private int running;

    public AuditRunner(IEnumerable<IDiagnosticCollector> collectors, TimeProvider? clock = null,
        FullAuditDiagnostics? full = null, IAuditCheckpointStore? checkpoints = null)
    {
        this.collectors = collectors.ToArray();
        if (this.collectors.Count == 0 || this.collectors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != this.collectors.Count)
            throw new ArgumentException("Coletores obrigatórios devem ter identificadores únicos e não podem estar vazios.", nameof(collectors));
        this.clock = clock ?? TimeProvider.System;
        this.full = full;
        this.checkpoints = checkpoints;
    }

    public bool SupportsFull => full is not null;
    public bool PersistsAudits => checkpoints is not null;

    public Task<AuditRecord> RunBasicAsync(IProgress<AuditProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(AuditKind.Basic, progress: progress, cancellationToken: cancellationToken);

    public async Task<AuditRecord> RunAsync(AuditKind kind, bool monitoringAuthorized = false,
        IProgress<AuditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == AuditKind.Full && (full is null || !monitoringAuthorized))
            throw new InvalidOperationException("Auditoria completa exige fontes configuradas e autorização explícita do monitoramento.");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma auditoria em andamento.");
        try { return await ExecuteAsync(kind, monitoringAuthorized, progress, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    private async Task<AuditRecord> ExecuteAsync(AuditKind kind, bool monitoringAuthorized,
        IProgress<AuditProgress>? progress, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var duration = Stopwatch.StartNew();
        var steps = collectors.Select(collector => new AuditStep(collector.Id, collector.Version, collector.Source,
            TimeSpan.FromSeconds(30), async (token, _) =>
            {
                var result = await collector.CollectAsync(id, token).ConfigureAwait(false);
                return new(result, result.Data is not null);
            })).ToList();
        if (kind == AuditKind.Full) steps.AddRange(full!.CreateSteps(id, monitoringAuthorized, clock));
        var version = checkpoints is not null || kind == AuditKind.Full ? 2 : 1;
        var results = new List<CollectorResult>(steps.Count);
        AuditRecord Snapshot(DiagnosticStatus status) => new(version, id, kind, SoftwareInfo.Version,
            started, clock.GetUtcNow(), duration.Elapsed, status, results.ToArray())
            { PlannedSteps = version == 2 ? steps.Count : null };

        using var lease = checkpoints is null ? null : await checkpoints.AcquireAuditLeaseAsync(id).ConfigureAwait(false);
        if (lease is not null)
            await lease.SaveCheckpointAsync(Snapshot(DiagnosticStatus.Running)).ConfigureAwait(false);

        string? interruption = null;
        foreach (var step in steps)
        {
            var finished = results.Count;
            void ReportActivity(string activity) => progress?.Report(new(step.Id, DiagnosticStatus.Running,
                finished, steps.Count) { Activity = activity });

            if (!cancellationToken.IsCancellationRequested)
                progress?.Report(new(step.Id, DiagnosticStatus.Running, finished, steps.Count));
            var result = await ReadAsync(id, step, ReportActivity, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new(step.Id, result.Status, results.Count, steps.Count) { Result = result });
            if (lease is null) continue;

            try { await lease.SaveCheckpointAsync(Snapshot(DiagnosticStatus.Running)).ConfigureAwait(false); }
            catch (Exception error)
            {
                interruption = $"O avanço da auditoria não pôde ser gravado ({error.GetType().Name}).";
                break;
            }
        }

        var status = interruption is not null ? DiagnosticStatus.Interrupted
            : cancellationToken.IsCancellationRequested || results.Any(r => r.Status == DiagnosticStatus.Cancelled)
            ? DiagnosticStatus.Cancelled : results.All(r => r.Status == DiagnosticStatus.Completed)
            ? DiagnosticStatus.Completed : results.All(r => r.Status == DiagnosticStatus.Failed)
            ? DiagnosticStatus.Failed : DiagnosticStatus.Partial;
        var audit = Snapshot(status) with { InterruptionReason = interruption };
        if (lease is not null)
        {
            progress?.Report(new("auditoria", status, results.Count, steps.Count) { IsSaving = true });
            try { await lease.CompleteAsync(audit).ConfigureAwait(false); }
            catch (Exception error) { throw new AuditPersistenceException(audit, error); }
        }
        return audit;
    }

    private async Task<CollectorResult> ReadAsync(Guid id, AuditStep step, Action<string> reportActivity,
        CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        var elapsed = Stopwatch.StartNew();
        CollectorResult Failure(DiagnosticStatus status, string reason) => new(id, step.Id, step.Version,
            started, clock.GetUtcNow(), elapsed.Elapsed, status, step.Source, null, [reason]);

        if (cancellationToken.IsCancellationRequested)
            return Failure(DiagnosticStatus.Cancelled, "Etapa não iniciada após o pedido de cancelamento.");

        using var deadline = new CancellationTokenSource(step.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var reading = await step.Read(linked.Token, reportActivity).ConfigureAwait(false);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            var result = reading.Result;
            if (result.AuditId != id || result.CollectorId != step.Id || !Enum.IsDefined(result.Status) ||
                result.Status is DiagnosticStatus.Pending or DiagnosticStatus.Running or DiagnosticStatus.Interrupted ||
                result.Evidence is { } evidence && (!AuditEvidenceReference.IsDiagnosticModule(evidence.Source) ||
                    evidence.RecordId == Guid.Empty || evidence.RecordId == id || result.Data is not null ||
                    step.Id != AuditEvidenceReference.CollectorIdFor(evidence.Source) ||
                    string.IsNullOrWhiteSpace(evidence.Summary) || evidence.Summary.Length > 512) ||
                result.Limitations is null || result.CompletedUtc < result.StartedUtc || result.Duration < TimeSpan.Zero)
                throw new InvalidDataException("Contrato inválido retornado pelo coletor.");

            if (cancellationToken.IsCancellationRequested)
                result = WithLimitation(result, DiagnosticStatus.Cancelled, "Cancelamento solicitado durante a coleta.");
            else if (deadline.IsCancellationRequested || elapsed.Elapsed >= step.Timeout)
                result = WithLimitation(result, reading.HasObservation ? DiagnosticStatus.Partial : DiagnosticStatus.Failed,
                    "Limite de tempo da etapa atingido. As evidências disponíveis foram preservadas.");
            else if (result.Status == DiagnosticStatus.Cancelled)
                result = WithLimitation(result, reading.HasObservation ? DiagnosticStatus.Partial : DiagnosticStatus.Failed,
                    "A fonte encerrou a coleta antes de concluir a etapa.");
            else if (result.Status == DiagnosticStatus.Completed &&
                (!reading.HasObservation || (result.Evidence is null && result.Limitations.Count > 0)))
                result = result with { Status = DiagnosticStatus.Partial };

            if (reading.Save is not null)
            {
                try { await reading.Save().ConfigureAwait(false); }
                catch (Exception error)
                {
                    return Failure(cancellationToken.IsCancellationRequested ? DiagnosticStatus.Cancelled : DiagnosticStatus.Failed,
                        $"Gravação da evidência do módulo não confirmada ({error.GetType().Name}).");
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(DiagnosticStatus.Cancelled, "Coleta cancelada pelo usuário.");
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            return Failure(DiagnosticStatus.Failed, "Limite de tempo da etapa atingido. Resultado indisponível.");
        }
        catch (Exception error)
        {
            return Failure(DiagnosticStatus.Failed, $"Falha de coleta ({error.GetType().Name}): {error.Message}");
        }
    }

    private static CollectorResult WithLimitation(CollectorResult result, DiagnosticStatus status, string reason) =>
        result with { Status = status, Limitations = [.. result.Limitations, reason] };
}

public sealed class AuditPersistenceException(AuditRecord audit, Exception cause)
    : IOException($"O registro da auditoria não foi confirmado ({cause.GetType().Name}).", cause)
{
    public AuditRecord Audit { get; } = audit;
}

internal sealed record AuditStep(string Id, string Version, string Source, TimeSpan Timeout,
    Func<CancellationToken, Action<string>, Task<AuditStepReading>> Read);

internal sealed record AuditStepReading(CollectorResult Result, bool HasObservation, Func<Task>? Save = null);
