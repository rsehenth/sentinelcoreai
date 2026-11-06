using System.Text;
using Microsoft.Data.Sqlite;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed partial class MemoryStore : IRecoveryHistoryReader
{
    public string GetRecoveryReportPath(RecoveryModule module, Guid id) => module switch
    {
        RecoveryModule.Tweaks => Path.Combine(root, "reports", "transactions", $"{id:D}.md"),
        RecoveryModule.Debloat or RecoveryModule.ControlledRemoval => Path.Combine(root, "reports", "debloat", $"{id:D}.md"),
        RecoveryModule.WindowsRepair => GetRepairReportPath(id),
        _ => throw new ArgumentOutOfRangeException(nameof(module))
    };

    /// <summary>
    /// Reuses the durable readers and validators, without their index/projection write phase.
    /// Opening the center must not repair, migrate, rewrite or create history artifacts.
    /// </summary>
    public Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<RecoveryHistorySource>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<RecoveryHistorySource>();
        FileStream? historyLock = null;
        // Directories already proven free of reparse points during this single pass; never reused across passes.
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            CheckRecoveryPath(root, verified);
            var lockPath = Path.Combine(root, "data", "store.lock");
            CheckRecoveryPath(lockPath, verified);
            historyLock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ReadRecoveryTransactions(new MemoryStore(root), RecoveryModule.Tweaks, sources, cancellationToken, verified);
            ReadRecoveryTransactions(new MemoryStore(root, true), RecoveryModule.Debloat, sources, cancellationToken, verified);
            ReadRecoveryRepairs(sources, cancellationToken, verified);
            CheckRecoveryDatabase(sources, cancellationToken, verified);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsRecoveryReadError(error))
        {
            sources.Clear();
            foreach (var module in Enum.GetValues<RecoveryModule>())
                sources.Add(new(module, RecoveryEvidenceStatus.Unavailable, [], [], [],
                    [$"Histórico indisponível ({error.GetType().Name}): {error.Message}. Não equivale a histórico vazio."]));
        }
        finally { historyLock?.Dispose(); }
        return sources.OrderBy(source => source.Module).ToArray();
    }, cancellationToken);

    private static bool IsRecoveryReadError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidDataException or ArgumentException or System.Text.Json.JsonException or SqliteException;

    private static void ValidateRecoveryHistoryEvent(TransactionEventRecord entry)
    {
        if (!Enum.IsDefined(entry.State) || entry.Limitations is null || entry.AtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Estado, horário ou limitações de evento desconhecidos.");
        if (entry.Results?.Any(step => step is null || string.IsNullOrWhiteSpace(step.TargetIdentity) ||
                step.Effects is null) == true)
            throw new InvalidDataException("Resultados de passo inválidos no histórico.");
        if (entry.Plan is { } plan)
        {
            if (!Enum.IsDefined(plan.Risk) || !Enum.IsDefined(plan.Scope) || plan.PlanId == Guid.Empty ||
                plan.TransactionId != entry.TransactionId || plan.Changes is not { Count: > 0 } ||
                plan.Changes.Any(change => change.Target is null || !Enum.IsDefined(change.Kind) ||
                    !OperationTargetRoots.IsAllowed(change.Target.Root) ||
                    change.Target.Root == OperationTargetRoots.CurrentUser &&
                        !OperationTargetRoots.IsWellFormedCurrentUserTarget(change.Target) ||
                    string.IsNullOrWhiteSpace(change.Target.Path) || change.TargetIdentity != change.Target.Identity ||
                    change.Before is null || change.After is null) ||
                plan.Changes.Select(change => change.TargetIdentity).Distinct(StringComparer.Ordinal).Count() != plan.Changes.Count)
                throw new InvalidDataException("Plano com enum, identidade ou alvo inválido no histórico.");
            var hash = SafetyHashing.PlanHash(plan.PlanId, plan.TransactionId, plan.OperationId,
                plan.OperationVersion, plan.CatalogVersion, plan.Risk, plan.Scope, plan.Protection,
                plan.ObservedStateHash, plan.Changes);
            if (!string.Equals(hash, plan.PlanHash, StringComparison.Ordinal) ||
                !string.Equals(hash, entry.PlanHash, StringComparison.Ordinal))
                throw new InvalidDataException("Hash do plano divergente no histórico.");
        }
        if (entry.Recovery is { } recovery && (recovery.RecoveryId == Guid.Empty ||
            recovery.TransactionId != entry.TransactionId || !Enum.IsDefined(recovery.Outcome) ||
            recovery.Conflicts is null || recovery.Steps is null || recovery.Limitations is null ||
            recovery.Conflicts.Any(conflict => !Enum.IsDefined(conflict.Kind) ||
                string.IsNullOrWhiteSpace(conflict.TargetIdentity)) ||
            recovery.StartedAtUtc.Offset != TimeSpan.Zero || recovery.CompletedAtUtc.Offset != TimeSpan.Zero ||
            recovery.CompletedAtUtc < recovery.StartedAtUtc))
            throw new InvalidDataException("Resultado ou identidade de recuperação inválidos no histórico.");
    }

    private static void ValidateRecoveryHistoryTransaction(IReadOnlyList<TransactionEventRecord> events)
    {
        var plan = events.Select(entry => entry.Plan).FirstOrDefault(value => value is not null)
            ?? throw new InvalidDataException("Histórico sem plano verificável.");
        var targets = plan.Changes.Select(change => change.TargetIdentity).ToHashSet(StringComparer.Ordinal);
        var recoveries = new Dictionary<Guid, string>();
        foreach (var entry in events)
        {
            if (entry.Verification is { } verification)
            {
                if (verification.Steps is null || verification.Limitations is null ||
                    verification.Steps.Any(step => step is null || !targets.Contains(step.TargetIdentity) || step.Effects is null) ||
                    verification.Steps.Select(step => step.TargetIdentity).Distinct(StringComparer.Ordinal).Count() != verification.Steps.Count)
                    throw new InvalidDataException("Verificação sem evidência válida dos alvos do plano.");
                var verified = verification.Steps.Count == targets.Count &&
                    verification.Steps.All(step => step.Verified && step.Applied && string.IsNullOrEmpty(step.Error));
                if (verification.Passed && (!verified || string.IsNullOrWhiteSpace(verification.PostStateHash)) ||
                    entry.State == TransactionState.VerificationPassed && (!verification.Passed || !verified) ||
                    entry.State == TransactionState.VerificationFailed && verification.Passed)
                    throw new InvalidDataException("Estado de verificação contradiz o resultado e as evidências registradas.");
            }
            if (entry.Recovery is not { } recovery) continue;
            if (recovery.Steps is null || recovery.Conflicts is null || recovery.Limitations is null)
                throw new InvalidDataException("Resultado de recuperação incompleto no histórico.");
            if (recovery.Steps.Any(step => step is null || !targets.Contains(step.TargetIdentity) || step.Effects is null) ||
                recovery.Conflicts.Any(conflict => !targets.Contains(conflict.TargetIdentity)) ||
                recovery.Steps.Select(step => step.TargetIdentity).Distinct(StringComparer.Ordinal).Count() != recovery.Steps.Count)
                throw new InvalidDataException("Recuperação contém alvos ausentes do plano ou passos duplicados.");
            var completed = recovery.Outcome is RecoveryOutcome.Completed or RecoveryOutcome.AlreadyRestored;
            var fullyVerified = recovery.Steps.Count == targets.Count &&
                recovery.Steps.All(step => step.Verified && string.IsNullOrEmpty(step.Error)) &&
                recovery.Conflicts.Count == 0 && string.IsNullOrEmpty(recovery.Error) && string.IsNullOrEmpty(entry.Error);
            if (recovery.FullyRecovered != completed || completed && !fullyVerified ||
                entry.State == TransactionState.RecoveryCompleted && (!completed || !recovery.FullyRecovered) ||
                entry.State == TransactionState.RecoveryFailed && (completed || recovery.FullyRecovered) ||
                entry.State is not (TransactionState.RecoveryCompleted or TransactionState.RecoveryFailed))
                throw new InvalidDataException("Conclusão de recuperação contradiz o resultado, conflitos ou verificação dos alvos.");
            var json = CanonicalJson(recovery);
            if (recoveries.TryGetValue(recovery.RecoveryId, out var previous) && previous != json)
                throw new InvalidDataException("Identidade de recuperação repetida com conteúdo divergente.");
            recoveries[recovery.RecoveryId] = json;
        }
    }

    private static bool RecoveryPathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void CheckRecoveryPath(string path, HashSet<string>? verified = null)
    {
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Caminho de histórico fora da raiz; leitura recusada.");
        var proven = verified is null ? null : new List<string>();
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            // Ancestors proven free of reparse points earlier in this same read pass are not queried again.
            if (current != full && verified?.Contains(current) == true) break;
            if (RecoveryAttributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Link ou ponto de redirecionamento no histórico; leitura recusada.");
            if (current != full) proven?.Add(current);
        }
        if (proven is not null) foreach (var directory in proven) verified!.Add(directory);
    }

    private static FileAttributes? RecoveryAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private void CheckRecoveryPartition(string partition, CancellationToken cancellationToken, HashSet<string> verified)
    {
        var directory = Path.Combine(root, "memory", partition);
        CheckRecoveryPath(directory, verified);
        if (!RecoveryPathExists(directory)) return;
        foreach (var operation in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckRecoveryPath(operation, verified);
            foreach (var file in Directory.EnumerateFiles(operation, "*.md", SearchOption.TopDirectoryOnly))
                CheckRecoveryPath(file, verified);
            var journal = Path.Combine(operation, "journal");
            CheckRecoveryPath(journal, verified);
            if (RecoveryPathExists(journal))
                foreach (var file in Directory.EnumerateFiles(journal, "*.md", SearchOption.TopDirectoryOnly))
                    CheckRecoveryPath(file, verified);
        }
    }

    private void ReadRecoveryTransactions(MemoryStore partition, RecoveryModule module,
        List<RecoveryHistorySource> sources, CancellationToken cancellationToken, HashSet<string> verified)
    {
        var warnings = new List<string>();
        var records = new List<TransactionRecord>();
        var status = RecoveryEvidenceStatus.Complete;
        try
        {
            CheckRecoveryPartition(partition.TransactionPartition, cancellationToken, verified);
            records = partition.ReconcileTransactions(null, warnings, cancellationToken);
            for (var index = 0; index < records.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = records[index];
                var limitations = new List<string>(record.Limitations);
                if (record.Backup is not null || record.BackupPath is not null)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(record.BackupPath))
                            throw new InvalidDataException("Caminho de backup ausente.");
                        var backupPath = partition.ResolveArtifactPath(record.BackupPath);
                        CheckRecoveryPath(backupPath, verified);
                        var backup = partition.ReadTransactionBackup(backupPath).Backup;
                        if (backup.TransactionId != record.TransactionId || backup.PlanId != record.PlanId ||
                            backup.PlanHash != record.PlanHash || backup.OperationId != record.OperationId ||
                            record.Backup is null || CanonicalJson(backup) != CanonicalJson(record.Backup) || !backup.IsComplete)
                            throw new InvalidDataException("Backup incompleto ou divergente do journal.");
                    }
                    catch (Exception error) when (IsRecoveryReadError(error))
                    {
                        var warning = $"Transação {record.TransactionId:D}: backup ausente, inválido ou indisponível: {error.Message}";
                        warnings.Add(warning); limitations.Add(warning);
                    }
                }
                records[index] = record with { Limitations = limitations.Distinct().ToArray() };
                CheckRecoveryProjection(partition.GetTransactionReportPath(record.TransactionId),
                    TransactionReport.Render(record, partition.debloatJournal), warnings, verified);
            }
            var projection = Path.Combine(root, "memory", partition.debloatJournal ? "DEBLOAT.md" : "RECOVERY.md");
            if (records.Count > 0 || RecoveryPathExists(projection))
                CheckRecoveryProjection(projection, TransactionReport.RenderIndex(records, partition.debloatJournal), warnings, verified);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsRecoveryReadError(error))
        {
            warnings.Add($"Fonte {module} indisponível ({error.GetType().Name}): {error.Message}; leitura incompleta.");
            status = records.Count == 0 ? RecoveryEvidenceStatus.Unavailable : RecoveryEvidenceStatus.Partial;
        }
        if (status == RecoveryEvidenceStatus.Complete && warnings.Count > 0) status = RecoveryEvidenceStatus.Partial;
        if (module == RecoveryModule.Debloat)
        {
            sources.Add(new(RecoveryModule.Debloat, status, records.Where(r =>
                !r.OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal)).ToArray(), [], [], warnings.ToArray()));
            sources.Add(new(RecoveryModule.ControlledRemoval, status, records.Where(r =>
                r.OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal)).ToArray(), [], [], warnings.ToArray()));
        }
        else sources.Add(new(module, status, records, [], [], warnings));
    }

    private void ReadRecoveryRepairs(List<RecoveryHistorySource> sources, CancellationToken cancellationToken, HashSet<string> verified)
    {
        var warnings = new List<string>();
        RepairHistory? history = null;
        var status = RecoveryEvidenceStatus.Complete;
        try
        {
            CheckRecoveryPartition("repairs", cancellationToken, verified);
            history = ReconcileRepairs(null, warnings, cancellationToken);
            foreach (var record in history.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckRecoveryProjection(GetRepairReportPath(record.Id), RepairReport.Render(record), warnings, verified);
            }
            var projection = Path.Combine(root, "memory", "REPAIRS.md");
            if (history.Records.Count > 0 || history.UnfinishedIntents.Count > 0 || RecoveryPathExists(projection))
                CheckRecoveryProjection(projection, RenderRepairProjection(history), warnings, verified);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsRecoveryReadError(error))
        {
            warnings.Add($"Fonte WindowsRepair indisponível ({error.GetType().Name}): {error.Message}; leitura incompleta.");
            status = history is null ? RecoveryEvidenceStatus.Unavailable : RecoveryEvidenceStatus.Partial;
        }
        if (status == RecoveryEvidenceStatus.Complete && warnings.Count > 0) status = RecoveryEvidenceStatus.Partial;
        sources.Add(new(RecoveryModule.WindowsRepair, status, [], history?.Records ?? [], history?.UnfinishedIntents ?? [], warnings));
    }

    private void CheckRecoveryProjection(string path, string expected, List<string> warnings, HashSet<string> verified)
    {
        try
        {
            CheckRecoveryPath(path, verified);
            if (!RecoveryPathExists(path)) warnings.Add($"Projeção/relatório ausente: {RelativePath(path)}; fonte Markdown preservada.");
            else if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes ||
                File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") != expected.Replace("\r\n", "\n"))
                warnings.Add($"Projeção/relatório desatualizado ou divergente: {RelativePath(path)}; não reconstruído pela central.");
        }
        catch (Exception error) when (IsRecoveryReadError(error))
        { warnings.Add($"Projeção/relatório indisponível: {RelativePath(path)} ({error.GetType().Name}); fonte preservada."); }
    }

    private void CheckRecoveryDatabase(List<RecoveryHistorySource> sources, CancellationToken cancellationToken, HashSet<string> verified)
    {
        try
        {
            CheckRecoveryPath(Database, verified);
            if (!RecoveryPathExists(Database)) throw new IOException("Arquivo do índice SQLite ausente.");
            // This store uses rollback journaling. Do not let a historical WAL index create shared-memory artifacts.
            if (RecoveryPathExists(Database + "-wal") || RecoveryPathExists(Database + "-shm"))
                throw new InvalidDataException("Índice SQLite em WAL não consultado pela central somente leitura.");
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            database.Open();
            using var check = database.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                throw new InvalidDataException("Índice SQLite inconsistente.");
            check.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(check.ExecuteScalar()) != 12)
                throw new InvalidDataException("Versão do índice SQLite incompatível; não migrada pela central.");
            foreach (var source in sources.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Module == RecoveryModule.ControlledRemoval) continue;
                var warnings = source.Warnings.ToList();
                var table = source.Module switch
                { RecoveryModule.Tweaks => "Transactions", RecoveryModule.Debloat => "DebloatTransactions", _ => "Repairs" };
                var transactions = source.Module == RecoveryModule.Debloat
                    ? sources.Where(s => s.Module is RecoveryModule.Debloat or RecoveryModule.ControlledRemoval)
                        .SelectMany(s => s.Transactions).ToArray()
                    : source.Transactions;
                // Keyed lookups preserve the previous first-match semantics without a linear scan per index row.
                var transactionsById = new Dictionary<Guid, TransactionRecord>();
                foreach (var item in transactions) transactionsById.TryAdd(item.TransactionId, item);
                var repairsById = new Dictionary<Guid, RepairRecord>();
                foreach (var item in source.Repairs) repairsById.TryAdd(item.Id, item);
                var expected = source.Module == RecoveryModule.WindowsRepair
                    ? source.Repairs.Select(r => r.Id).ToHashSet()
                    : transactions.Select(r => r.TransactionId).ToHashSet();
                using var query = database.CreateCommand();
                query.CommandText = source.Module == RecoveryModule.WindowsRepair
                    ? "SELECT Id, State, PayloadHash FROM Repairs"
                    : $"SELECT Id, State, PlanHash, EventCount, Indeterminate, Succeeded FROM {table}";
                using var reader = query.ExecuteReader(); var actual = new HashSet<Guid>();
                var divergent = false;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParse(reader.GetString(0), out var id))
                        throw new InvalidDataException("Índice SQLite contém identidade inválida.");
                    actual.Add(id);
                    if (source.Module == RecoveryModule.WindowsRepair)
                    {
                        repairsById.TryGetValue(id, out var repair);
                        divergent |= repair is null || reader.GetString(1) != repair.State.ToString() ||
                            reader.GetString(2) != RecordEnvelope.Hash(CanonicalJson(repair));
                    }
                    else
                    {
                        transactionsById.TryGetValue(id, out var transaction);
                        divergent |= transaction is null || reader.GetString(1) != transaction.State.ToString() ||
                            reader.GetString(2) != transaction.PlanHash || reader.GetInt32(3) != transaction.Events.Count ||
                            (reader.GetInt32(4) != 0) != transaction.IsIndeterminate ||
                            (reader.GetInt32(5) != 0) != transaction.Succeeded;
                    }
                }
                reader.Close();
                if (source.Module != RecoveryModule.WindowsRepair)
                {
                    using var events = database.CreateCommand();
                    var eventTable = source.Module == RecoveryModule.Tweaks ? "TransactionEvents" : "DebloatTransactionEvents";
                    events.CommandText = $"SELECT TransactionId, Sequence, PayloadHash FROM {eventTable}";
                    var eventsByKey = new Dictionary<(Guid, int), TransactionEventRecord>();
                    foreach (var item in transactionsById.Values)
                        foreach (var recorded in item.Events) eventsByKey.TryAdd((item.TransactionId, recorded.Sequence), recorded);
                    using var eventReader = events.ExecuteReader(); var count = 0;
                    while (eventReader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        count++;
                        var transactionId = Guid.TryParse(eventReader.GetString(0), out var parsed) ? parsed : Guid.Empty;
                        eventsByKey.TryGetValue((transactionId, eventReader.GetInt32(1)), out var entry);
                        divergent |= entry is null || eventReader.GetString(2) != RecordEnvelope.Hash(CanonicalJson(entry));
                    }
                    divergent |= count != transactions.Sum(t => t.Events.Count);
                }
                if (divergent || !expected.SetEquals(actual))
                    warnings.Add($"Índice SQLite divergente da fonte Markdown: {table}; reconciliação necessária, não executada pela central.");
                ReplaceRecoveryWarnings(sources, source, warnings);
                if (source.Module == RecoveryModule.Debloat)
                {
                    var removal = sources.Single(s => s.Module == RecoveryModule.ControlledRemoval);
                    ReplaceRecoveryWarnings(sources, removal, removal.Warnings.Concat(warnings.Except(source.Warnings)).Distinct().ToList());
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsRecoveryReadError(error))
        {
            foreach (var source in sources.ToArray())
                ReplaceRecoveryWarnings(sources, source, source.Warnings.Append(
                    $"Índice SQLite indisponível ou incompatível ({error.GetType().Name}): {error.Message}; leitura direta dos Markdowns, sem reconstrução automática.").ToList());
        }
    }

    private static void ReplaceRecoveryWarnings(List<RecoveryHistorySource> sources, RecoveryHistorySource source,
        List<string> warnings) => sources[sources.IndexOf(source)] = source with
        { Warnings = warnings, Status = source.Status == RecoveryEvidenceStatus.Unavailable ? source.Status :
            warnings.Count > 0 ? RecoveryEvidenceStatus.Partial : RecoveryEvidenceStatus.Complete };
}
