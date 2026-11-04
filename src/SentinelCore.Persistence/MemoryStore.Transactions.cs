using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed partial class MemoryStore
{
    private readonly bool debloatJournal;
    private string TransactionPartition => debloatJournal ? "debloat" : "transactions";
    // These identifiers are closed constants, never supplied by payloads or callers.
    private string TransactionsTable => debloatJournal ? "DebloatTransactions" : "Transactions";
    private string TransactionEventsTable => debloatJournal ? "DebloatTransactionEvents" : "TransactionEvents";
    private string TransactionBackupsTable => debloatJournal ? "DebloatTransactionBackups" : "TransactionBackups";
    private string RecoveriesTable => debloatJournal ? "DebloatRecoveries" : "Recoveries";

    private MemoryStore(string dataRoot, bool debloatJournal) : this(dataRoot) =>
        this.debloatJournal = debloatJournal;

    public ITransactionJournalStore CreateDebloatJournal() => new MemoryStore(root, debloatJournal: true);

    private string TransactionDirectory(Guid id) =>
        Path.Combine(root, "memory", TransactionPartition, $"{id:D}");
    private string TransactionJournalDirectory(Guid id) =>
        Path.Combine(TransactionDirectory(id), "journal");
    private string TransactionEventPath(Guid id, int sequence, TransactionState state) =>
        Path.Combine(TransactionJournalDirectory(id), $"{sequence:D4}-{state}.md");
    private string TransactionBackupPath(Guid id, int sequence) =>
        Path.Combine(TransactionDirectory(id), $"backup-{sequence:D2}.md");
    public string GetTransactionReportPath(Guid transactionId) =>
        Path.Combine(root, "reports", TransactionPartition, $"{transactionId:D}.md");

    public Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateEvent(entry);
        EnsureDirectories();
        using var storeLock = Lock();
        EnsureNoForeignRecord(entry.TransactionId);
        var directory = TransactionJournalDirectory(entry.TransactionId);
        Directory.CreateDirectory(directory);
        var json = CanonicalJson(entry);

        var existing = Directory.EnumerateFiles(directory, $"{entry.Sequence:D4}-*.md",
                SearchOption.TopDirectoryOnly)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count > 0)
        {
            foreach (var path in existing)
            {
                var (stored, _) = ReadTransactionEvent(path);
                if (stored.Sequence != entry.Sequence)
                    throw new InvalidDataException("Nome de arquivo de evento não corresponde à sequência.");
                if (CanonicalJson(stored) == json) return FinishEvent(stored);
            }
            throw new InvalidDataException(
                "Sequência de journal já existe com conteúdo diferente; registro preservado.");
        }

        var siblings = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(ReadTransactionEvent).Select(item => item.Event).ToList();
        if (siblings.Count > 0)
        {
            var previous = siblings.Max(item => item.Sequence);
            if (entry.Sequence != previous + 1)
                throw new InvalidDataException("Sequência de journal fora de ordem.");
            var first = siblings.MinBy(item => item.Sequence)!;
            if (first.TransactionId != entry.TransactionId ||
                first.OperationId != entry.OperationId ||
                first.OperationVersion != entry.OperationVersion ||
                !string.Equals(first.CatalogVersion, entry.CatalogVersion, StringComparison.Ordinal) ||
                !string.Equals(first.PlanHash, entry.PlanHash, StringComparison.Ordinal))
                throw new InvalidDataException("Evento não pertence ao mesmo plano da transação.");
            var last = siblings.MaxBy(item => item.Sequence)!;
            if (!TransactionProjection.IsAllowedTransition(last.State, entry.State))
                throw new InvalidDataException(
                    $"Transição inválida no journal: {last.State} → {entry.State}; evento não gravado.");
            ValidateRemovalAssociation(siblings.Append(entry).ToArray());
        }
        else if (entry.Sequence != 1 || entry.State != TransactionState.Planned)
        {
            throw new InvalidDataException("Primeiro evento deve ser Planned na sequência 1.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var document = RecordEnvelope.Wrap(TransactionReport.RenderEvent(entry, debloatJournal), json);
        if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Evento de transação excede 4 MiB.");
        RecordEnvelope.AtomicWrite(TransactionEventPath(entry.TransactionId, entry.Sequence, entry.State),
            document, overwrite: false);
        return FinishEvent(entry);
    }, cancellationToken);

    public Task<string> WriteBackupAsync(BackupRecord backup,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateBackupRecord(backup);
        EnsureDirectories();
        using var storeLock = Lock();
        EnsureNoForeignRecord(backup.TransactionId);
        var directory = TransactionDirectory(backup.TransactionId);
        Directory.CreateDirectory(directory);
        var path = TransactionBackupPath(backup.TransactionId, backup.Sequence);
        var json = CanonicalJson(backup);
        if (File.Exists(path))
        {
            var (existing, _) = ReadTransactionBackup(path);
            if (CanonicalJson(existing) != json)
                throw new InvalidDataException("Backup já existe com conteúdo diferente; histórico preservado.");
            FinishBackup(backup);
            return path;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var document = RecordEnvelope.Wrap(TransactionReport.RenderBackup(backup, debloatJournal), json);
        if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Backup de transação excede 4 MiB.");
        RecordEnvelope.AtomicWrite(path, document, overwrite: false);
        FinishBackup(backup);
        return path;
    }, cancellationToken);

    public Task<BackupRecord> ReadBackupAsync(string path,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var resolved = ResolveArtifactPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        var (backup, _) = ReadTransactionBackup(resolved);
        return backup;
    }, cancellationToken);

    public Task<TransactionJournalSnapshot> LoadTransactionsAsync(
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var records = ReconcileTransactions(database, warnings, cancellationToken);
        WriteTransactionProjections(records);
        var byId = records.ToDictionary(item => item.TransactionId);
        var ordered = new List<TransactionRecord>();
        using var query = database.CreateCommand();
        query.CommandText = $"SELECT Id FROM {TransactionsTable} ORDER BY UpdatedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (byId.TryGetValue(Guid.Parse(reader.GetString(0)), out var record)) ordered.Add(record);
        return new TransactionJournalSnapshot(ordered, warnings);
    }, cancellationToken);

    public async Task<TransactionRecord?> FindTransactionAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadTransactionsAsync(cancellationToken);
        return snapshot.Transactions.FirstOrDefault(item => item.TransactionId == transactionId);
    }

    private TransactionEventRecord FinishEvent(TransactionEventRecord entry)
    {
        using var database = OpenDatabase([]);
        var records = ReconcileTransactions(database, [], CancellationToken.None);
        WriteTransactionProjections(records);
        var record = records.FirstOrDefault(item => item.TransactionId == entry.TransactionId);
        if (record is null || record.Events.All(item => item.Sequence != entry.Sequence))
            throw new IOException("O evento durável existe, mas o índice da transação não foi concluído.");
        return record.Events.First(item => item.Sequence == entry.Sequence);
    }

    private void FinishBackup(BackupRecord backup)
    {
        using var database = OpenDatabase([]);
        var records = ReconcileTransactions(database, [], CancellationToken.None);
        WriteTransactionProjections(records);
        _ = backup;
    }

    private List<TransactionRecord> ReconcileTransactions(SqliteConnection database,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var records = new List<TransactionRecord>();
        var eventRows = new List<(Guid TransactionId, TransactionEventRecord Event, string Path, string Hash)>();
        var backupRows = new List<(Guid TransactionId, BackupRecord Backup, string Path, string Hash)>();
        var rootDirectory = Path.Combine(this.root, "memory", TransactionPartition);

        if (Directory.Exists(rootDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(rootDirectory)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(Path.GetFileName(directory), out var transactionId))
                {
                    warnings.Add($"Pasta de transação {Path.GetFileName(directory)} sem identificador válido; preservada.");
                    continue;
                }

                var journalDirectory = Path.Combine(directory, "journal");
                if (!Directory.Exists(journalDirectory)) continue;
                try { EnsureNoForeignRecord(transactionId); }
                catch (InvalidDataException error)
                {
                    warnings.Add($"Transação {transactionId:D} com ID em outro histórico: {error.Message}");
                    continue;
                }
                var events = new List<(TransactionEventRecord Event, string Path, string Hash)>();
                var invalid = false;
                foreach (var path in Directory.EnumerateFiles(journalDirectory, "*.md",
                             SearchOption.TopDirectoryOnly).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var (stored, hash) = ReadTransactionEvent(path);
                        if (stored.TransactionId != transactionId)
                            throw new InvalidDataException("Evento fora da pasta da transação informada.");
                        var name = Path.GetFileNameWithoutExtension(path);
                        var separator = name.IndexOf('-');
                        if (separator <= 0 || !int.TryParse(name[..separator], out var sequence) ||
                            sequence != stored.Sequence || !name[(separator + 1)..].Equals(stored.State.ToString(),
                                StringComparison.Ordinal))
                            throw new InvalidDataException("Nome de arquivo de evento inválido.");
                        events.Add((stored, path, hash));
                    }
                    catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                        or UnauthorizedAccessException or ArgumentException or NullReferenceException)
                    {
                        invalid = true;
                        warnings.Add($"Evento de transação {Path.GetFileName(path)} não recuperado " +
                            $"({error.GetType().Name}): {error.Message}. Original preservado.");
                    }
                }

                if (invalid || events.Count == 0)
                {
                    if (!invalid) warnings.Add($"Transação {transactionId:D} sem eventos; preservada.");
                    continue;
                }

                var ordered = events.OrderBy(item => item.Event.Sequence).ToList();
                var duplicate = ordered.GroupBy(item => item.Event.Sequence)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate is not null)
                {
                    warnings.Add($"Transação {transactionId:D} possui sequência {duplicate.Key} duplicada; " +
                        "preservada e excluída da validade.");
                    continue;
                }
                if (ordered[0].Event.Sequence != 1 || ordered[0].Event.State != TransactionState.Planned)
                {
                    warnings.Add($"Transação {transactionId:D} não inicia em Planned; preservada e excluída da validade.");
                    continue;
                }
                if (ordered.Where((item, index) => item.Event.Sequence != index + 1).Any())
                {
                    warnings.Add($"Transação {transactionId:D} possui lacuna de sequência; preservada e excluída da validade.");
                    continue;
                }

                var first = ordered[0].Event;
                if (ordered.Any(item => item.Event.OperationId != first.OperationId ||
                                        item.Event.OperationVersion != first.OperationVersion ||
                                        !string.Equals(item.Event.CatalogVersion, first.CatalogVersion,
                                            StringComparison.Ordinal) ||
                                        !string.Equals(item.Event.PlanHash, first.PlanHash,
                                            StringComparison.Ordinal)))
                {
                    warnings.Add($"Transação {transactionId:D} mistura operações ou planos; preservada e excluída da validade.");
                    continue;
                }
                if (ordered.All(item => item.Event.Plan is null))
                {
                    warnings.Add($"Transação {transactionId:D} sem plano íntegro; preservada e excluída da validade.");
                    continue;
                }

                try
                {
                    ValidateRemovalAssociation(ordered.Select(item => item.Event).ToArray());
                    var record = TransactionProjection.Build(ordered.Select(item => item.Event).ToList());
                    records.Add(record);
                    eventRows.AddRange(ordered.Select(item => (transactionId, item.Event, item.Path, item.Hash)));
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                {
                    warnings.Add($"Transação {transactionId:D} inválida: {error.Message}. Original preservado.");
                }
            }
        }

        var backupFiles = Directory.Exists(rootDirectory)
            ? Directory.EnumerateFiles(rootDirectory, "backup-*.md", SearchOption.AllDirectories)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        foreach (var path in backupFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (backup, hash) = ReadTransactionBackup(path);
                if (!records.Any(item => item.TransactionId == backup.TransactionId))
                {
                    warnings.Add($"Backup {Path.GetFileName(path)} sem transação válida; preservado.");
                    continue;
                }
                if (Path.GetFileName(path) != $"backup-{backup.Sequence:D2}.md")
                {
                    warnings.Add($"Backup {Path.GetFileName(path)} com sequência divergente; preservado.");
                    continue;
                }
                backupRows.Add((TransactionId: backup.TransactionId, Backup: backup, Path: path, Hash: hash));
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                or UnauthorizedAccessException or ArgumentException or NullReferenceException)
            {
                warnings.Add($"Backup de transação {Path.GetFileName(path)} não recuperado " +
                    $"({error.GetType().Name}): {error.Message}. Original preservado.");
            }
        }

        IndexTransactions(database, records, eventRows, backupRows);
        return records;
    }

    private void IndexTransactions(SqliteConnection database, IReadOnlyList<TransactionRecord> records,
        IReadOnlyList<(Guid TransactionId, TransactionEventRecord Event, string Path, string Hash)> events,
        IReadOnlyList<(Guid TransactionId, BackupRecord Backup, string Path, string Hash)> backups)
    {
        using var transaction = database.BeginTransaction();
        using var clear = database.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = $"DELETE FROM {TransactionsTable}";
        clear.ExecuteNonQuery();

        using var insert = database.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"""
            INSERT INTO {TransactionsTable} (Id, PlanId, OperationId, OperationVersion, CatalogVersion, PlanHash,
                State, CreatedUtc, UpdatedUtc, EventCount, BackupCount, Indeterminate, Succeeded)
            VALUES ($id, $plan, $operation, $version, $catalog, $hash, $state, $created, $updated,
                $events, $backups, $indeterminate, $succeeded)
            """;
        foreach (var record in records)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$id", record.TransactionId.ToString("D"));
            insert.Parameters.AddWithValue("$plan", record.PlanId.ToString("D"));
            insert.Parameters.AddWithValue("$operation", record.OperationId);
            insert.Parameters.AddWithValue("$version", record.OperationVersion);
            insert.Parameters.AddWithValue("$catalog", record.CatalogVersion);
            insert.Parameters.AddWithValue("$hash", record.PlanHash);
            insert.Parameters.AddWithValue("$state", record.State.ToString());
            insert.Parameters.AddWithValue("$created", record.CreatedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$updated", record.UpdatedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$events", record.Events.Count);
            insert.Parameters.AddWithValue("$backups", backups.Count(item => item.TransactionId == record.TransactionId));
            insert.Parameters.AddWithValue("$indeterminate", record.IsIndeterminate ? 1 : 0);
            insert.Parameters.AddWithValue("$succeeded", record.Succeeded ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        using var insertEvent = database.CreateCommand();
        insertEvent.Transaction = transaction;
        insertEvent.CommandText = $"""
            INSERT INTO {TransactionEventsTable} (TransactionId, Sequence, State, AtUtc, OperationId,
                OperationVersion, CatalogVersion, PlanHash, Detail, Error, MarkdownPath, PayloadHash)
            VALUES ($transaction, $sequence, $state, $at, $operation, $version, $catalog, $hash,
                $detail, $error, $path, $payload)
            """;
        foreach (var row in events)
        {
            insertEvent.Parameters.Clear();
            insertEvent.Parameters.AddWithValue("$transaction", row.TransactionId.ToString("D"));
            insertEvent.Parameters.AddWithValue("$sequence", row.Event.Sequence);
            insertEvent.Parameters.AddWithValue("$state", row.Event.State.ToString());
            insertEvent.Parameters.AddWithValue("$at", row.Event.AtUtc.ToString("O"));
            insertEvent.Parameters.AddWithValue("$operation", row.Event.OperationId);
            insertEvent.Parameters.AddWithValue("$version", row.Event.OperationVersion);
            insertEvent.Parameters.AddWithValue("$catalog", row.Event.CatalogVersion);
            insertEvent.Parameters.AddWithValue("$hash", row.Event.PlanHash);
            insertEvent.Parameters.AddWithValue("$detail", (object?)row.Event.Detail ?? DBNull.Value);
            insertEvent.Parameters.AddWithValue("$error", (object?)row.Event.Error ?? DBNull.Value);
            insertEvent.Parameters.AddWithValue("$path", RelativePath(row.Path));
            insertEvent.Parameters.AddWithValue("$payload", row.Hash);
            insertEvent.ExecuteNonQuery();
        }

        using var insertBackup = database.CreateCommand();
        insertBackup.Transaction = transaction;
        insertBackup.CommandText = $"""
            INSERT INTO {TransactionBackupsTable} (TransactionId, BackupId, Sequence, PlanHash, ContentHash,
                EntryCount, Complete, MarkdownPath, PayloadHash)
            VALUES ($transaction, $backup, $sequence, $plan, $content, $entries, $complete, $path, $payload)
            """;
        foreach (var row in backups)
        {
            insertBackup.Parameters.Clear();
            insertBackup.Parameters.AddWithValue("$transaction", row.TransactionId.ToString("D"));
            insertBackup.Parameters.AddWithValue("$backup", row.Backup.BackupId.ToString("D"));
            insertBackup.Parameters.AddWithValue("$sequence", row.Backup.Sequence);
            insertBackup.Parameters.AddWithValue("$plan", row.Backup.PlanHash);
            insertBackup.Parameters.AddWithValue("$content", row.Backup.ContentHash);
            insertBackup.Parameters.AddWithValue("$entries", row.Backup.Entries.Count);
            insertBackup.Parameters.AddWithValue("$complete", row.Backup.IsComplete ? 1 : 0);
            insertBackup.Parameters.AddWithValue("$path", RelativePath(row.Path));
            insertBackup.Parameters.AddWithValue("$payload", row.Hash);
            insertBackup.ExecuteNonQuery();
        }

        using var insertRecovery = database.CreateCommand();
        insertRecovery.Transaction = transaction;
        insertRecovery.CommandText = $"""
            INSERT OR REPLACE INTO {RecoveriesTable} (RecoveryId, TransactionId, Outcome, FullyRecovered,
                EffectMayHaveOccurred, ConflictCount, StartedUtc, CompletedUtc, PayloadHash)
            VALUES ($recovery, $transaction, $outcome, $fully, $effect, $conflicts, $started, $completed, $hash)
            """;
        foreach (var record in records)
        {
            foreach (var recovery in record.Events.Select(item => item.Recovery)
                         .Where(item => item is not null).Select(item => item!))
            {
                insertRecovery.Parameters.Clear();
                insertRecovery.Parameters.AddWithValue("$recovery", recovery.RecoveryId.ToString("D"));
                insertRecovery.Parameters.AddWithValue("$transaction", record.TransactionId.ToString("D"));
                insertRecovery.Parameters.AddWithValue("$outcome", recovery.Outcome.ToString());
                insertRecovery.Parameters.AddWithValue("$fully", recovery.FullyRecovered ? 1 : 0);
                insertRecovery.Parameters.AddWithValue("$effect", recovery.EffectMayHaveOccurred ? 1 : 0);
                insertRecovery.Parameters.AddWithValue("$conflicts", recovery.Conflicts.Count);
                insertRecovery.Parameters.AddWithValue("$started", recovery.StartedAtUtc.ToString("O"));
                insertRecovery.Parameters.AddWithValue("$completed", recovery.CompletedAtUtc.ToString("O"));
                insertRecovery.Parameters.AddWithValue("$hash", RecordEnvelope.Hash(CanonicalJson(recovery)));
                insertRecovery.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private void WriteTransactionProjections(IReadOnlyList<TransactionRecord> records)
    {
        Directory.CreateDirectory(Path.Combine(root, "reports", TransactionPartition));
        foreach (var record in records)
            RecordEnvelope.AtomicWrite(GetTransactionReportPath(record.TransactionId),
                TransactionReport.Render(record, debloatJournal), overwrite: true);
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", debloatJournal ? "DEBLOAT.md" : "RECOVERY.md"),
            TransactionReport.RenderIndex(records, debloatJournal), overwrite: true);
    }

    private string RelativePath(string path) =>
        Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');

    /// <summary>
    /// Resolve caminhos absolutos gravados antes de uma mudança de raiz de dados. O journal permanece
    /// imutável; se o caminho original estiver fora da raiz atual, a parte
    /// <c>memory/transactions/&lt;id&gt;/...</c> é remapeada para a raiz atual. Nada fora da raiz é aceito.
    /// </summary>
    private string ResolveArtifactPath(string path)
    {
        var rootFull = Path.GetFullPath(root);
        var basePath = rootFull + Path.DirectorySeparatorChar;
        var full = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(rootFull, path));
        var partitionBase = Path.Combine(rootFull, "memory", TransactionPartition) + Path.DirectorySeparatorChar;
        if (full.StartsWith(partitionBase, StringComparison.OrdinalIgnoreCase))
            return full;

        var normalized = full.Replace('\\', '/');
        var marker = $"/memory/{TransactionPartition}/";
        var index = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var relative = normalized[(index + 1)..].Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(rootFull, relative));
            if (candidate.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return candidate;
        }
        throw new InvalidDataException("Caminho de artefato fora da raiz de dados; recusado.");
    }

    private (TransactionEventRecord Event, string PayloadHash) ReadTransactionEvent(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Evento de transação excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var entry = JsonSerializer.Deserialize<TransactionEventRecord>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de evento nulo.");
        ValidateEvent(entry);
        return (entry, RecordEnvelope.Hash(json));
    }

    private (BackupRecord Backup, string PayloadHash) ReadTransactionBackup(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Backup de transação excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var backup = JsonSerializer.Deserialize<BackupRecord>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de backup nulo.");
        ValidateBackupRecord(backup);
        return (backup, RecordEnvelope.Hash(json));
    }

    private void ValidateEvent(TransactionEventRecord entry)
    {
        if (entry.Plan is not null && entry.OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal))
            ValidateRemovalAssociation([entry]);
        if (entry.RemovalReceipt is { } receipt)
        {
            if (!debloatJournal || !entry.OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal) ||
                entry.State is not (TransactionState.ExecutionCompleted or TransactionState.ExecutionFailed or TransactionState.ExecutionIndeterminate) ||
                !receipt.Synthetic || receipt.OperationId != entry.TransactionId ||
                !DebloatRemovalIdentityPolicy.IsValid(receipt.Package) || !Enum.IsDefined(receipt.Phase) ||
                receipt.Progress is null || receipt.Progress.Count > 256 || receipt.Limitations is null ||
                receipt.Progress.Any(p => p.OperationId != receipt.OperationId || !Enum.IsDefined(p.Phase) ||
                    p.ReportedPercentage is > 100) ||
                receipt.CancelledBeforeStart && (receipt.EffectMayHaveOccurred || receipt.Phase != DebloatRemovalPhase.Failed))
                throw new InvalidDataException("Resultado T3C inválido ou fora da partição sintética.");
        }
        if (entry.FormatVersion != 1)
            throw new InvalidDataException("Formato de evento de transação não suportado.");
        if (entry.TransactionId == Guid.Empty)
            throw new InvalidDataException("Transação sem identificador.");
        if (entry.Sequence <= 0)
            throw new InvalidDataException("Sequência de evento inválida.");
        if (string.IsNullOrWhiteSpace(entry.OperationId) || entry.OperationVersion <= 0)
            throw new InvalidDataException("Evento sem operação e versão válidas.");
        ValidateJournalOperation(entry.OperationId);
        if (debloatJournal && (entry.Recovery is not null || entry.State is
                TransactionState.RecoveryRequested or TransactionState.RecoveryStarted or
                TransactionState.RecoveryCompleted or TransactionState.RecoveryFailed))
            throw new InvalidDataException("Debloat sintético não admite recuperação ou rollback.");
        if (debloatJournal && (entry.Results?.Any(result => !IsSyntheticIdentity(result.TargetIdentity)) == true ||
            entry.Verification?.Steps.Any(step => !IsSyntheticIdentity(step.TargetIdentity)) == true))
            throw new InvalidDataException("Resultados Debloat admitem somente alvos Synthetic.");
        if (entry.Backup is not null) ValidateBackupRecord(entry.Backup);
        if (string.IsNullOrWhiteSpace(entry.CatalogVersion) || string.IsNullOrWhiteSpace(entry.PlanHash))
            throw new InvalidDataException("Evento sem catálogo ou hash de plano.");
        if (entry.State == TransactionState.Planned && entry.Plan is null)
            throw new InvalidDataException("Evento Planned sem plano.");
        if (entry.State == TransactionState.Authorized &&
            (entry.Authorization is null || !entry.Authorization.Consumed))
            throw new InvalidDataException("Evento Authorized sem autorização consumida.");
        if (entry.State is TransactionState.BackupPrepared or TransactionState.BackupVerified &&
            entry.Backup is null)
            throw new InvalidDataException("Evento de backup sem conteúdo.");
        if (entry.State is TransactionState.VerificationPassed or TransactionState.VerificationFailed &&
            entry.Verification is null)
            throw new InvalidDataException("Evento de verificação sem resultado.");
        if (entry.State is TransactionState.RecoveryCompleted or TransactionState.RecoveryFailed &&
            entry.Recovery is null)
            throw new InvalidDataException("Evento de recuperação sem resultado.");
        if (entry.Backup is not null && entry.Backup.TransactionId != entry.TransactionId)
            throw new InvalidDataException("Backup de outra transação no evento.");
        if (entry.Recovery is not null && entry.Recovery.TransactionId != entry.TransactionId)
            throw new InvalidDataException("Recuperação de outra transação no evento.");
        if (entry.Authorization is { } authorization)
        {
            if (authorization.TransactionId != entry.TransactionId ||
                authorization.OperationId != entry.OperationId ||
                authorization.OperationVersion != entry.OperationVersion ||
                !string.Equals(authorization.PlanHash, entry.PlanHash, StringComparison.Ordinal))
                throw new InvalidDataException("Autorização não corresponde ao evento.");
            if (entry.Plan is { } authorizedPlan &&
                (authorization.PlanId != authorizedPlan.PlanId ||
                 !string.Equals(authorization.ObservedStateHash, authorizedPlan.ObservedStateHash,
                     StringComparison.Ordinal)))
                throw new InvalidDataException("Autorização não corresponde ao plano observado.");
        }
        if (entry.Plan is { } plan)
        {
            if (debloatJournal && plan.Changes.Any(change =>
                    change.Target.Root != OperationTargetRoots.Synthetic ||
                    !IsSyntheticIdentity(change.TargetIdentity) ||
                    change.TargetIdentity != change.Target.Identity))
                throw new InvalidDataException("Debloat admite somente alvos Synthetic.");
            if (plan.OperationId != entry.OperationId || plan.OperationVersion != entry.OperationVersion ||
                !string.Equals(plan.CatalogVersion, entry.CatalogVersion, StringComparison.Ordinal) ||
                !string.Equals(plan.PlanHash, entry.PlanHash, StringComparison.Ordinal))
                throw new InvalidDataException("Plano não corresponde ao evento que o contém.");
            if (!string.Equals(SafetyHashing.PlanHash(plan.PlanId, plan.TransactionId, plan.OperationId,
                    plan.OperationVersion, plan.CatalogVersion, plan.Risk, plan.Scope, plan.Protection,
                    plan.ObservedStateHash, plan.Changes), plan.PlanHash, StringComparison.Ordinal))
                throw new InvalidDataException("Hash do plano não confere com o conteúdo.");
        }
    }

    private void ValidateBackupRecord(BackupRecord backup)
    {
        if (backup.BackupId == Guid.Empty || backup.TransactionId == Guid.Empty || backup.PlanId == Guid.Empty)
            throw new InvalidDataException("Backup sem identidade completa.");
        if (string.IsNullOrWhiteSpace(backup.OperationId) || backup.OperationVersion <= 0)
            throw new InvalidDataException("Backup sem operação válida.");
        ValidateJournalOperation(backup.OperationId);
        if (string.IsNullOrWhiteSpace(backup.PlanHash) || string.IsNullOrWhiteSpace(backup.ContentHash))
            throw new InvalidDataException("Backup sem hash de plano ou conteúdo.");
        if (backup.Sequence <= 0)
            throw new InvalidDataException("Sequência de backup inválida.");
        if (backup.Entries.Count == 0)
            throw new InvalidDataException("Backup sem alvos.");
        if (backup.Entries.Select(item => item.TargetIdentity).Distinct(StringComparer.Ordinal).Count() !=
            backup.Entries.Count)
            throw new InvalidDataException("Backup contém identidades de alvo duplicadas.");
        foreach (var entry in backup.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.TargetIdentity))
                throw new InvalidDataException("Backup com alvo sem identidade.");
            if (debloatJournal && !IsSyntheticIdentity(entry.TargetIdentity))
                throw new InvalidDataException("Snapshot Debloat admite somente alvos Synthetic.");
            if (!string.Equals(entry.Checksum, SafetyHashing.EntryChecksum(entry), StringComparison.Ordinal))
                throw new InvalidDataException($"Checksum inválido para {entry.TargetIdentity}.");
        }
        if (!string.Equals(backup.ContentHash, SafetyHashing.BackupContentHash(backup.Entries),
                StringComparison.Ordinal))
            throw new InvalidDataException("Hash de conteúdo do backup não confere.");
    }

    private void ValidateJournalOperation(string operationId)
    {
        if (operationId.StartsWith("DBLT-", StringComparison.Ordinal) != debloatJournal)
            throw new InvalidDataException("Operação não pertence à partição de journal selecionada.");
    }

    private static void ValidateRemovalAssociation(IReadOnlyList<TransactionEventRecord> events)
    {
        if (!events[0].OperationId.StartsWith("DBLT-RMV-", StringComparison.Ordinal)) return;
        try
        {
            var plan = events[0].Plan ?? throw new InvalidDataException("Intenção T3C sem plano.");
            var change = plan.Changes.Single();
            var snapshot = JsonSerializer.Deserialize<DebloatRemovalSnapshot>(change.Before.Data!);
            var after = JsonSerializer.Deserialize<DebloatRemovalSnapshot>(change.After.Data!);
            if (snapshot is null || snapshot.SchemaVersion != 1 || snapshot.Removed ||
                !Enum.IsDefined(snapshot.Scenario) || !snapshot.Observation.Synthetic ||
                snapshot.Observation.QueryState != DebloatInventoryQueryState.Completed ||
                snapshot.Observation.Completeness != DebloatObservationCompleteness.Complete ||
                snapshot.Observation.Dependencies.Completeness != DebloatObservationCompleteness.Complete ||
                !snapshot.Observation.Dependencies.EffectsKnown ||
                snapshot.Observation.Dependencies.UnknownConsequences.Count != 0 ||
                snapshot.Observation.Packages is not { Count: > 0 and <= DebloatNativeLimits.MaximumPackages } ||
                snapshot.Observation.Packages.Any(p => !Enum.IsDefined(p.Kind) || !Enum.IsDefined(p.Scope) ||
                    !Enum.IsDefined(p.InstallState) || !DebloatNativeIdentity.IsValid(p)) ||
                !DebloatRemovalIdentityPolicy.IsValid(snapshot.Observation.SelectedPackage) ||
                !Enum.IsDefined(snapshot.CatalogEntry.PreliminaryRisk) || !Enum.IsDefined(snapshot.CatalogEntry.RealRisk) ||
                snapshot.CatalogEntry.RealRisk is not (RiskLevel.High or RiskLevel.NotValidated) ||
                snapshot.CatalogEntry.Id != snapshot.Observation.CatalogId ||
                snapshot.CatalogEntry.TechnicalName != snapshot.Observation.SelectedPackage.TechnicalName ||
                plan.TransactionId != events[0].TransactionId ||
                plan.OperationId != "DBLT-RMV-" + snapshot.CatalogEntry.Id ||
                change.Before.Kind != "DebloatRemovalMetadata" || change.After.Kind != change.Before.Kind ||
                DebloatRemovalHashing.Hash(after) != DebloatRemovalHashing.Hash(snapshot with { Removed = true }))
                throw new InvalidDataException("Snapshot T3C divergente ou versão desconhecida.");
            foreach (var entry in events.Where(e => e.RemovalReceipt is not null))
            {
                var r = entry.RemovalReceipt!;
                if (r.Package != snapshot.Observation.SelectedPackage ||
                    r.Phase is not (DebloatRemovalPhase.Completed or DebloatRemovalPhase.Failed or DebloatRemovalPhase.Indeterminate) ||
                    entry.State == TransactionState.ExecutionCompleted &&
                    (r.Phase != DebloatRemovalPhase.Completed || r.HResult != 0 ||
                        r.ExtendedHResult is not (null or 0) || !string.IsNullOrEmpty(r.ErrorText) || r.PartialFailure) ||
                    entry.State == TransactionState.ExecutionFailed && r.EffectMayHaveOccurred)
                    throw new InvalidDataException("Resultado T3C não corresponde ao plano/estado.");
            }
            if (events[^1].State == TransactionState.VerificationPassed &&
                !events.Any(e => e.RemovalReceipt is { Phase: DebloatRemovalPhase.Completed,
                    PostVerification: { Complete: true, MainPackageAbsent: true, EffectsMatchPlan: true } }))
                throw new InvalidDataException("Sucesso T3C sem resultado e pós-verificação completos.");
        }
        catch (Exception e) when (e is JsonException or NullReferenceException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Metadados T3C incompatíveis; preservar original.", e); }
    }

    private static bool IsSyntheticIdentity(string identity) =>
        identity.StartsWith(OperationTargetRoots.Synthetic + "\\", StringComparison.Ordinal);

    private static string CanonicalJson(TransactionEventRecord entry) =>
        JsonSerializer.Serialize(entry, AuditJson.Options).Replace("\r\n", "\n");

    private static string CanonicalJson(BackupRecord backup) =>
        JsonSerializer.Serialize(backup, AuditJson.Options).Replace("\r\n", "\n");

    private static string CanonicalJson(RecoveryResult recovery) =>
        JsonSerializer.Serialize(recovery, AuditJson.Options).Replace("\r\n", "\n");
}
