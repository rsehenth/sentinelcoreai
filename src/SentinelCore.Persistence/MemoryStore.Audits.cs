using System.Text;
using System.Text.Json;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed partial class MemoryStore : IAuditCheckpointStore
{
    private string AuditCheckpoints => Path.Combine(root, "memory", "audits", "pending");
    private string AuditCheckpointPath(Guid id) => Path.Combine(AuditCheckpoints, $"{id:D}.md");
    private string AuditLeasePath(Guid id) => Path.Combine(root, "data", "audit-leases", $"{id:D}.lock");

    public Task<IAuditRunLease> AcquireAuditLeaseAsync(Guid auditId) => Task.Run<IAuditRunLease>(() =>
    {
        if (auditId == Guid.Empty) throw new ArgumentException("ID de auditoria inválido.", nameof(auditId));
        var leasePath = AuditLeasePath(auditId);
        var globalPath = Path.Combine(root, "data", "audit.lock");
        CheckRecoveryPath(leasePath);
        CheckRecoveryPath(globalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        var exclusive = new FileStream(globalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new AuditLease(this, auditId, lease, exclusive);
        }
        catch
        {
            exclusive.Dispose();
            throw;
        }
    });

    private Task SaveAuditCheckpointAsync(AuditRecord checkpoint) => Task.Run(() =>
    {
        Validate(checkpoint, checkpoint: true);
        var path = AuditCheckpointPath(checkpoint.Id);
        CheckRecoveryPath(path);
        CheckRecoveryPath(Path.Combine(root, "data", "store.lock"));
        using var storeLock = Lock();
        if (Enum.GetValues<AuditKind>().Any(kind => RecoveryAttributes(RecordPath(checkpoint with { Kind = kind })) is not null))
            throw new InvalidDataException("Auditoria já possui registro final.");

        var previous = RecoveryAttributes(path) is null ? null : ReadRecord(path, checkpoint: true);
        if (previous is null && checkpoint.Results.Count != 0)
            throw new InvalidDataException("Checkpoint inicial deve preceder a coleta.");
        if (previous is not null)
        {
            if (CanonicalJson(previous) == CanonicalJson(checkpoint)) return;
            if (checkpoint.Results.Count != previous.Results.Count + 1 || !ExtendsCheckpoint(checkpoint, previous))
                throw new InvalidDataException("Avanço de auditoria não corresponde ao checkpoint anterior.");
        }
        var document = RecordEnvelope.Wrap("# SentinelCore AI — checkpoint de auditoria\n", CanonicalJson(checkpoint));
        if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Checkpoint excede 4 MiB.");
        Directory.CreateDirectory(AuditCheckpoints);
        RecordEnvelope.AtomicWrite(path, document, overwrite: previous is not null);
    });

    public Task<StoreSnapshot> ReadInterruptedAuditsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var warnings = new List<string>();
            try
            {
                CheckRecoveryPath(AuditCheckpoints);
                if (RecoveryAttributes(AuditCheckpoints) is null) return new StoreSnapshot([], warnings);
                var lockPath = Path.Combine(root, "data", "store.lock");
                CheckRecoveryPath(lockPath);
                using var storeLock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new StoreSnapshot(ReadAuditInterruptions(warnings, cancellationToken), warnings);
            }
            catch (Exception error) when (IsRecoveryReadError(error))
            {
                warnings.Add($"Interrupções de auditoria indisponíveis ({error.GetType().Name}).");
                return new StoreSnapshot([], warnings);
            }
        }, cancellationToken);

    private IReadOnlyList<AuditRecord> ReadAuditInterruptions(List<string> warnings, CancellationToken token)
    {
        var interrupted = new List<AuditRecord>();
        try
        {
            var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CheckRecoveryPath(AuditCheckpoints, verified);
            if (RecoveryAttributes(AuditCheckpoints) is null) return interrupted;
            foreach (var path in Directory.EnumerateFiles(AuditCheckpoints, "*.md"))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    CheckRecoveryPath(path, verified);
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) || id == Guid.Empty)
                        throw new InvalidDataException("Nome de checkpoint inválido.");
                    if (IsAuditActive(id, verified)) continue;
                    var checkpoint = ReadRecord(path, checkpoint: true);
                    if (checkpoint.Id != id) throw new InvalidDataException("Identidade de checkpoint divergente.");
                    var final = RecordPath(checkpoint);
                    var otherFinal = RecordPath(checkpoint with { Kind = checkpoint.Kind == AuditKind.Basic ? AuditKind.Full : AuditKind.Basic });
                    CheckRecoveryPath(final, verified);
                    CheckRecoveryPath(otherFinal, verified);
                    if (RecoveryAttributes(otherFinal) is not null)
                        throw new InvalidDataException("Checkpoint conflita com outro tipo de auditoria.");
                    if (RecoveryAttributes(final) is not null)
                    {
                        var record = ReadRecord(final);
                        if (!ExtendsCheckpoint(record, checkpoint))
                            throw new InvalidDataException("Registro final diverge do checkpoint.");
                        if (record.Status == DiagnosticStatus.Interrupted) interrupted.Add(record);
                        continue;
                    }

                    interrupted.Add(checkpoint with
                    {
                        Status = DiagnosticStatus.Interrupted,
                        InterruptionReason = "A auditoria terminou sem registro final. O horário e as evidências são do último checkpoint confirmado."
                    });
                }
                catch (Exception error) when (IsRecoveryReadError(error))
                {
                    warnings.Add($"Checkpoint {Path.GetFileName(path)} não recuperado ({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        catch (Exception error) when (IsRecoveryReadError(error))
        {
            warnings.Add($"Leitura dos checkpoints de auditoria incompleta ({error.GetType().Name}).");
        }
        return interrupted.OrderByDescending(audit => audit.CompletedUtc).ThenBy(audit => audit.Id).ToArray();
    }

    private bool IsAuditActive(Guid id, HashSet<string>? verified = null)
    {
        var path = AuditLeasePath(id);
        try
        {
            CheckRecoveryPath(path, verified);
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return true; }
    }

    private static bool ExtendsCheckpoint(AuditRecord record, AuditRecord checkpoint) =>
        record.Results.Count >= checkpoint.Results.Count && record.Duration >= checkpoint.Duration &&
        CanonicalJson(checkpoint) == CanonicalJson(record with
        {
            CompletedUtc = checkpoint.CompletedUtc,
            Duration = checkpoint.Duration,
            Status = checkpoint.Status,
            Results = record.Results.Take(checkpoint.Results.Count).ToArray(),
            InterruptionReason = null
        });

    private sealed class AuditLease(MemoryStore owner, Guid id, FileStream perAudit, FileStream exclusive) : IAuditRunLease
    {
        private bool disposed;

        public Task SaveCheckpointAsync(AuditRecord checkpoint)
        {
            EnsureOwned(checkpoint);
            return owner.SaveAuditCheckpointAsync(checkpoint);
        }

        public Task CompleteAsync(AuditRecord audit)
        {
            EnsureOwned(audit);
            return owner.SaveAuditAsync(audit, CancellationToken.None, ownsLease: true);
        }

        private void EnsureOwned(AuditRecord audit)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (audit.Id != id) throw new InvalidDataException("O registro não pertence à auditoria ativa.");
        }

        public void Dispose()
        {
            disposed = true;
            try { perAudit.Dispose(); }
            finally { exclusive.Dispose(); }
        }
    }
}
