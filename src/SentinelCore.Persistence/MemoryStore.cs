using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed class MemoryStore : IAuditStore, IHardwareSnapshotStore, IEventInvestigationStore,
    IPerformanceSessionStore, INetworkSessionStore
{
    private readonly string root;
    private string Database => Path.Combine(root, "data", "sentinelcore.db");
    private string HardwareRecordPath(Guid id) => Path.Combine(root, "memory", "inventory", $"{id:D}.md");
    private string EventRecordPath(Guid id) => Path.Combine(root, "memory", "events", $"{id:D}.md");
    private string PerformanceRecordPath(Guid id) => Path.Combine(root, "memory", "performance", $"{id:D}.md");
    private string NetworkRecordPath(Guid id) => Path.Combine(root, "memory", "network", $"{id:D}.md");
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCoreAI");
    public MemoryStore(string? dataRoot = null) => root = Path.GetFullPath(dataRoot ?? DefaultRoot);
    public string GetReportPath(Guid auditId) => Path.Combine(root, "reports", $"{auditId:D}.md");
    public string GetHardwareReportPath(Guid snapshotId) => Path.Combine(root, "reports", "hardware", $"{snapshotId:D}.md");
    public string GetEventReportPath(Guid investigationId) => Path.Combine(root, "reports", "events", $"{investigationId:D}.md");
    public string GetPerformanceReportPath(Guid sessionId) => Path.Combine(root, "reports", "performance", $"{sessionId:D}.md");
    public string GetNetworkReportPath(Guid sessionId) => Path.Combine(root, "reports", "network", $"{sessionId:D}.md");
    private string RecordPath(AuditRecord audit) => Path.Combine(root, "memory", "audits", audit.Kind.ToString().ToLowerInvariant(), $"{audit.Id:D}.md");

    public Task<StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var records = Reconcile(database, warnings, cancellationToken);
        WriteProjections(records);
        var byId = records.ToDictionary(a => a.Id);
        var ordered = new List<AuditRecord>();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Id FROM Audits ORDER BY CompletedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read()) ordered.Add(byId[Guid.Parse(reader.GetString(0))]);
        return new StoreSnapshot(ordered, warnings);
    }, cancellationToken);

    public Task SaveAsync(AuditRecord audit, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        Validate(audit);
        EnsureDirectories();
        using var storeLock = Lock();
        var otherKind = audit.Kind == AuditKind.Basic ? AuditKind.Full : AuditKind.Basic;
        if (File.Exists(RecordPath(audit with { Kind = otherKind })) ||
            File.Exists(HardwareRecordPath(audit.Id)) || File.Exists(EventRecordPath(audit.Id)) ||
            File.Exists(PerformanceRecordPath(audit.Id)) || File.Exists(NetworkRecordPath(audit.Id)))
            throw new InvalidDataException("ID já existe em outro histórico; histórico preservado.");
        var json = CanonicalJson(audit);
        var path = RecordPath(audit);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = ReadRecord(path);
            if (CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = RecordEnvelope.Wrap(MarkdownReport.Render(audit), json);
            if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException("Registro excede 4 MiB e não pode ser persistido neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
        }
        // Once the durable record exists, finish the index without the audit cancellation token.
        // A crash here is recoverable by the next reconciliation from the immutable record.
        using var database = OpenDatabase([]);
        var records = Reconcile(database, [], CancellationToken.None);
        if (!records.Any(record => record.Id == audit.Id) || !File.Exists(GetReportPath(audit.Id)))
            throw new IOException("A gravação não foi concluída. O registro durável, se existente, será recuperado na próxima tentativa.");
        WriteProjections(records);
    }, cancellationToken);

    private void EnsureDirectories()
    {
        foreach (var relative in new[] { "memory", "memory/inventory", "memory/events", "memory/performance", "memory/network", "data", "reports", "reports/hardware", "reports/events", "reports/performance", "reports/network", "logs", Path.Combine("memory", "audits", "basic") })
            Directory.CreateDirectory(Path.Combine(root, relative));
    }

    private FileStream Lock() => new(Path.Combine(root, "data", "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private SqliteConnection OpenDatabase(List<string> warnings)
    {
        SqliteConnection Connect()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Database, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
            try
            {
                connection.Open();
                using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                    throw new SqliteException("Índice SQLite inconsistente.", 11);
                Migrate(connection);
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }
        try { return Connect(); }
        catch (SqliteException error) when (error.SqliteErrorCode is 11 or 26)
        {
            var backup = Database + ".corrupt-" + Guid.NewGuid().ToString("N");
            File.Move(Database, backup);
            warnings.Add("Índice SQLite corrompido preservado em data/" + Path.GetFileName(backup) + "; reconstruído dos registros Markdown válidos.");
            return Connect();
        }
    }

    private static void Migrate(SqliteConnection database)
    {
        using var version = database.CreateCommand(); version.CommandText = "PRAGMA user_version";
        var schema = Convert.ToInt32(version.ExecuteScalar());
        if (schema > 6)
            throw new InvalidDataException("Banco de uma versão mais recente. Abra com uma versão compatível do aplicativo.");
        if (schema == 6) return;
        using var transaction = database.BeginTransaction();
        using var command = database.CreateCommand(); command.Transaction = transaction;
        if (schema < 1)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Audits (
                    Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, SoftwareVersion TEXT NOT NULL,
                    FormatVersion INTEGER NOT NULL, StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL,
                    Status TEXT NOT NULL, MarkdownPath TEXT NOT NULL UNIQUE, ExpiresUtc TEXT NULL, PayloadHash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS CollectorResults (
                    AuditId TEXT NOT NULL REFERENCES Audits(Id) ON DELETE CASCADE,
                    CollectorId TEXT NOT NULL, CollectorVersion TEXT NOT NULL, Status TEXT NOT NULL,
                    Source TEXT NOT NULL, CollectedUtc TEXT NOT NULL, DurationMs REAL NOT NULL,
                    InventoryJson TEXT NULL, LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (AuditId, CollectorId));
                CREATE INDEX IF NOT EXISTS IX_Audits_Kind_Completed ON Audits(Kind, CompletedUtc DESC);
                """;
            command.ExecuteNonQuery();
        }
        if (schema < 2)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS HardwareSnapshots (
                    Id TEXT PRIMARY KEY, FormatVersion INTEGER NOT NULL, SchemaVersion TEXT NOT NULL,
                    SoftwareVersion TEXT NOT NULL, StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL,
                    Status TEXT NOT NULL, MarkdownPath TEXT NOT NULL UNIQUE, PayloadHash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS HardwareCollectorResults (
                    SnapshotId TEXT NOT NULL REFERENCES HardwareSnapshots(Id) ON DELETE CASCADE,
                    CollectorId TEXT NOT NULL, CollectorVersion TEXT NOT NULL, Category TEXT NOT NULL,
                    Status TEXT NOT NULL, Source TEXT NOT NULL, StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL,
                    DurationMs REAL NOT NULL, LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (SnapshotId, CollectorId));
                CREATE TABLE IF NOT EXISTS HardwareItems (
                    SnapshotId TEXT NOT NULL REFERENCES HardwareSnapshots(Id) ON DELETE CASCADE,
                    Category TEXT NOT NULL, Kind TEXT NOT NULL, StableId TEXT NOT NULL,
                    Name TEXT NULL, Manufacturer TEXT NULL, Model TEXT NULL, HardwareId TEXT NULL,
                    DriverVersion TEXT NULL, FirmwareVersion TEXT NULL, WindowsState TEXT NULL,
                    Presence TEXT NOT NULL, PropertiesJson TEXT NOT NULL,
                    PRIMARY KEY (SnapshotId, Category, Kind, StableId));
                CREATE INDEX IF NOT EXISTS IX_HardwareSnapshots_Completed ON HardwareSnapshots(CompletedUtc DESC);
                """;
            command.ExecuteNonQuery();
        }
        if (schema < 3)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS EventInvestigations (
                    Id TEXT PRIMARY KEY, FormatVersion INTEGER NOT NULL, SchemaVersion TEXT NOT NULL,
                    SoftwareVersion TEXT NOT NULL, WindowPreset TEXT NOT NULL,
                    PeriodFromUtc TEXT NOT NULL, PeriodToUtc TEXT NOT NULL,
                    StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL,
                    Status TEXT NOT NULL, MarkdownPath TEXT NOT NULL UNIQUE, PayloadHash TEXT NOT NULL,
                    GroupsJson TEXT NOT NULL, LimitationsJson TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS EventSources (
                    InvestigationId TEXT NOT NULL REFERENCES EventInvestigations(Id) ON DELETE CASCADE,
                    CollectorId TEXT NOT NULL, CollectorVersion TEXT NOT NULL, Source TEXT NOT NULL,
                    Status TEXT NOT NULL, StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL,
                    DurationMs REAL NOT NULL, EventsRead INTEGER NOT NULL, ReportsRead INTEGER NOT NULL,
                    LimitHit INTEGER NOT NULL, LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (InvestigationId, CollectorId));
                CREATE TABLE IF NOT EXISTS EventGroups (
                    InvestigationId TEXT NOT NULL REFERENCES EventInvestigations(Id) ON DELETE CASCADE,
                    GroupId TEXT NOT NULL, Category TEXT NOT NULL, Title TEXT NOT NULL,
                    Severity TEXT NOT NULL, Confidence TEXT NOT NULL, Causality TEXT NOT NULL,
                    EventRecordCount INTEGER NOT NULL, DistinctReportCount INTEGER NOT NULL,
                    RePresentationCount INTEGER NOT NULL, FirstObservedUtc TEXT NULL, LastObservedUtc TEXT NULL,
                    PRIMARY KEY (InvestigationId, GroupId));
                CREATE INDEX IF NOT EXISTS IX_EventInvestigations_Completed ON EventInvestigations(CompletedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_EventGroups_Category ON EventGroups(InvestigationId, Category);
                """;
            command.ExecuteNonQuery();
        }
        if (schema < 4)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS PerformanceSessions (
                    Id TEXT PRIMARY KEY, FormatVersion INTEGER NOT NULL, SchemaVersion TEXT NOT NULL,
                    SoftwareVersion TEXT NOT NULL, Preset TEXT NOT NULL,
                    RequestedDurationSeconds REAL NOT NULL, SampleIntervalSeconds REAL NOT NULL,
                    StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL, DurationSeconds REAL NOT NULL,
                    Status TEXT NOT NULL, MarkdownPath TEXT NOT NULL UNIQUE, PayloadHash TEXT NOT NULL,
                    StatisticsJson TEXT NOT NULL, AnalysisJson TEXT NOT NULL,
                    SelfJson TEXT NOT NULL, LimitationsJson TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS PerformanceSources (
                    SessionId TEXT NOT NULL REFERENCES PerformanceSessions(Id) ON DELETE CASCADE,
                    CollectorId TEXT NOT NULL, CollectorVersion TEXT NOT NULL, Category TEXT NOT NULL,
                    Source TEXT NOT NULL, Status TEXT NOT NULL, StartedUtc TEXT NOT NULL,
                    CompletedUtc TEXT NOT NULL, DurationMs REAL NOT NULL, SamplesRead INTEGER NOT NULL,
                    LimitHit INTEGER NOT NULL, LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (SessionId, CollectorId));
                CREATE TABLE IF NOT EXISTS PerformanceObservations (
                    SessionId TEXT NOT NULL REFERENCES PerformanceSessions(Id) ON DELETE CASCADE,
                    ObservationId TEXT NOT NULL, RuleId TEXT NOT NULL, Kind TEXT NOT NULL,
                    Severity TEXT NOT NULL, Confidence TEXT NOT NULL, Causality TEXT NOT NULL,
                    Metric TEXT NOT NULL, Threshold REAL NULL, Subject TEXT NULL,
                    WindowFromUtc TEXT NOT NULL, WindowToUtc TEXT NOT NULL,
                    SupportingSamples INTEGER NOT NULL, EvaluatedSamples INTEGER NOT NULL,
                    ObservedMin REAL NULL, ObservedMax REAL NULL,
                    PRIMARY KEY (SessionId, ObservationId));
                CREATE INDEX IF NOT EXISTS IX_PerformanceSessions_Completed ON PerformanceSessions(CompletedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_PerformanceObservations_Rule ON PerformanceObservations(SessionId, RuleId);
                """;
            command.ExecuteNonQuery();
        }
        if (schema < 5)
        {
            AddColumnIfMissing(database, transaction, "PerformanceSessions", "ExecutionStatus", "TEXT NULL");
            AddColumnIfMissing(database, transaction, "PerformanceSessions", "CoverageStatus", "TEXT NULL");
            AddColumnIfMissing(database, transaction, "PerformanceSessions", "CoverageJson", "TEXT NULL");
            AddColumnIfMissing(database, transaction, "PerformanceSources", "MeanReadMs", "REAL NULL");
            AddColumnIfMissing(database, transaction, "PerformanceSources", "MaxReadMs", "REAL NULL");
        }
        if (schema < 6)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS NetworkSessions (
                    Id TEXT PRIMARY KEY, FormatVersion INTEGER NOT NULL, SchemaVersion TEXT NOT NULL,
                    SoftwareVersion TEXT NOT NULL, Preset TEXT NOT NULL,
                    RequestedDurationSeconds REAL NOT NULL, SampleIntervalSeconds REAL NOT NULL,
                    StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL, DurationSeconds REAL NOT NULL,
                    Status TEXT NOT NULL, ExecutionStatus TEXT NULL, CoverageStatus TEXT NULL,
                    SampleCount INTEGER NOT NULL, EndpointDetailCount INTEGER NOT NULL,
                    IncludesAddresses INTEGER NOT NULL, MarkdownPath TEXT NOT NULL UNIQUE,
                    PayloadHash TEXT NOT NULL, StatisticsJson TEXT NOT NULL, CoverageJson TEXT NULL,
                    PortRangesJson TEXT NOT NULL, AdaptersJson TEXT NOT NULL,
                    TcpStatisticsJson TEXT NULL, UdpStatisticsJson TEXT NULL,
                    TcpipEventsJson TEXT NULL, SelfJson TEXT NOT NULL, LimitationsJson TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS NetworkSources (
                    SessionId TEXT NOT NULL REFERENCES NetworkSessions(Id) ON DELETE CASCADE,
                    CollectorId TEXT NOT NULL, CollectorVersion TEXT NOT NULL, Category TEXT NOT NULL,
                    Source TEXT NOT NULL, Status TEXT NOT NULL, StartedUtc TEXT NOT NULL,
                    CompletedUtc TEXT NOT NULL, DurationMs REAL NOT NULL, SamplesRead INTEGER NOT NULL,
                    LimitHit INTEGER NOT NULL, LimitationsJson TEXT NOT NULL,
                    MeanReadMs REAL NULL, MaxReadMs REAL NULL,
                    PRIMARY KEY (SessionId, CollectorId));
                CREATE TABLE IF NOT EXISTS NetworkObservations (
                    SessionId TEXT NOT NULL REFERENCES NetworkSessions(Id) ON DELETE CASCADE,
                    ObservationId TEXT NOT NULL, RuleId TEXT NOT NULL, Kind TEXT NOT NULL,
                    Severity TEXT NOT NULL, Confidence TEXT NOT NULL, Causality TEXT NOT NULL,
                    Metric TEXT NOT NULL, Threshold REAL NULL, Subject TEXT NULL,
                    WindowFromUtc TEXT NOT NULL, WindowToUtc TEXT NOT NULL,
                    SupportingSamples INTEGER NOT NULL, EvaluatedSamples INTEGER NOT NULL,
                    ObservedMin REAL NULL, ObservedMax REAL NULL,
                    PRIMARY KEY (SessionId, ObservationId));
                CREATE INDEX IF NOT EXISTS IX_NetworkSessions_Completed ON NetworkSessions(CompletedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_NetworkObservations_Rule ON NetworkObservations(SessionId, RuleId);
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version = 6";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void AddColumnIfMissing(SqliteConnection database, SqliteTransaction transaction,
        string table, string column, string definition)
    {
        using var info = database.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = $"PRAGMA table_info({table})";
        using (var reader = info.ExecuteReader())
            while (reader.Read())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
        using var alter = database.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private List<AuditRecord> Reconcile(SqliteConnection database, List<string> warnings, CancellationToken cancellationToken)
    {
        var records = new List<AuditRecord>();
        var seen = new HashSet<Guid>();
        foreach (var kind in new[] { AuditKind.Basic, AuditKind.Full })
        {
            var directory = Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant());
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var audit = ReadRecord(path);
                    if (audit.Kind != kind || Path.GetFileName(path) != $"{audit.Id:D}.md" || !seen.Add(audit.Id))
                        throw new InvalidDataException("Identidade ou localização de registro inválida.");
                    records.Add(audit);
                    RecordEnvelope.AtomicWrite(GetReportPath(audit.Id), MarkdownReport.Render(audit), overwrite: true);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    warnings.Add($"Registro {Path.GetFileName(path)} não recuperado ({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM Audits";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seen.Contains(id))
                    warnings.Add($"Auditoria indexada {reader.GetString(0)} sem registro Markdown válido; excluída da validade.");
        }
        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction; clear.CommandText = "DELETE FROM CollectorResults; DELETE FROM Audits"; clear.ExecuteNonQuery();
        }
        foreach (var audit in records) Index(database, transaction, audit);
        transaction.Commit();
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"), $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return records;
    }

    private void Index(SqliteConnection database, SqliteTransaction transaction, AuditRecord audit)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO Audits VALUES ($id,$kind,$software,$format,$start,$end,$status,$path,$expires,$hash)";
        command.Parameters.AddWithValue("$id", audit.Id.ToString("D")); command.Parameters.AddWithValue("$kind", audit.Kind.ToString());
        command.Parameters.AddWithValue("$software", audit.SoftwareVersion); command.Parameters.AddWithValue("$format", audit.FormatVersion);
        command.Parameters.AddWithValue("$start", audit.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", audit.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$status", audit.Status.ToString());
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, RecordPath(audit)));
        command.Parameters.AddWithValue("$expires", audit.Status == DiagnosticStatus.Completed
            ? audit.CompletedUtc.AddDays(audit.Kind == AuditKind.Basic ? 7 : 30).ToUniversalTime().ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$hash", RecordEnvelope.Hash(CanonicalJson(audit))); command.ExecuteNonQuery();
        foreach (var result in audit.Results)
        {
            using var child = database.CreateCommand(); child.Transaction = transaction;
            child.CommandText = "INSERT INTO CollectorResults VALUES ($audit,$id,$version,$status,$source,$at,$duration,$data,$limits)";
            child.Parameters.AddWithValue("$audit", audit.Id.ToString("D")); child.Parameters.AddWithValue("$id", result.CollectorId);
            child.Parameters.AddWithValue("$version", result.CollectorVersion); child.Parameters.AddWithValue("$status", result.Status.ToString());
            child.Parameters.AddWithValue("$source", result.Source); child.Parameters.AddWithValue("$at", result.CompletedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$duration", result.Duration.TotalMilliseconds);
            child.Parameters.AddWithValue("$data", result.Data is null ? DBNull.Value : JsonSerializer.Serialize(result.Data, AuditJson.Options));
            child.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(result.Limitations, AuditJson.Options)); child.ExecuteNonQuery();
        }
    }

    private void WriteProjections(IReadOnlyList<AuditRecord> records)
    {
        var latest = records.OrderByDescending(a => a.CompletedUtc).ToArray();
        string Last(AuditKind kind)
        {
            var audit = latest.FirstOrDefault(a => a.Kind == kind && a.Status == DiagnosticStatus.Completed && a.CompletedUtc <= DateTimeOffset.UtcNow);
            return audit is null ? "Nenhuma auditoria concluída com sucesso." : $"{audit.Id:D} — {audit.CompletedUtc:O} — versão {audit.SoftwareVersion}";
        }
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "MEMORY.md"),
            $"# SentinelCore AI — Memória persistente\n\nFormato: 1\n\n## Última básica concluída\n\n{Last(AuditKind.Basic)}\n\n## Última completa concluída\n\n{Last(AuditKind.Full)}\n\n## Histórico\n\n" +
            (latest.Length == 0 ? "Nenhuma auditoria anterior.\n" : string.Join("\n", latest.Select(a => $"- [{a.Id:D}](audits/{a.Kind.ToString().ToLowerInvariant()}/{a.Id:D}.md) — {a.CompletedUtc:O} — {a.Status}"))), true);
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "SYSTEM_STATE.md"),
            latest.Length == 0 ? "# Estado observado\n\nNenhuma auditoria anterior.\n" : MarkdownReport.Render(latest[0]), true);
        var hardware = latest.FirstOrDefault(a => a.Results.Any(r => r.Data is not null));
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "HARDWARE.md"), hardware is null
            ? "# Hardware\n\nNenhum inventário coletado.\n" : $"# Hardware observado\n\nAuditoria: {hardware.Id:D}\n\n" +
            string.Join("\n", hardware.Results.SelectMany(r => MarkdownReport.Fields(r.Data)).Select(f => $"- {f.Label}: {f.Value ?? "Indisponível"}")), true);
    }

    public Task<HardwareStoreSnapshot> LoadSnapshotsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var snapshots = ReconcileHardware(database, warnings, cancellationToken);
        var byId = snapshots.ToDictionary(s => s.Id);
        var ordered = new List<HardwareSnapshot>();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Id FROM HardwareSnapshots ORDER BY CompletedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read()) ordered.Add(byId[Guid.Parse(reader.GetString(0))]);
        return new HardwareStoreSnapshot(ordered, warnings);
    }, cancellationToken);

    public Task SaveSnapshotAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateHardware(snapshot);
        EnsureDirectories();
        using var storeLock = Lock();
        foreach (var kind in Enum.GetValues<AuditKind>())
            if (File.Exists(Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant(), $"{snapshot.Id:D}.md")))
                throw new InvalidDataException("ID já existe em uma auditoria; histórico preservado.");
        if (File.Exists(EventRecordPath(snapshot.Id)))
            throw new InvalidDataException("ID já existe em uma investigação; histórico preservado.");
        if (File.Exists(PerformanceRecordPath(snapshot.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de desempenho; histórico preservado.");
        if (File.Exists(NetworkRecordPath(snapshot.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de rede; histórico preservado.");
        var json = CanonicalJson(snapshot);
        var path = HardwareRecordPath(snapshot.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = ReadHardwareRecord(path);
            if (CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = RecordEnvelope.Wrap(HardwareReport.Render(snapshot), json);
            if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException("Inventário excede 4 MiB e não pode ser persistido neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
        }
        // Once the durable record exists, finish the index without the collection cancellation token.
        using var database = OpenDatabase([]);
        var snapshots = ReconcileHardware(database, [], CancellationToken.None);
        if (!snapshots.Any(s => s.Id == snapshot.Id) || !File.Exists(GetHardwareReportPath(snapshot.Id)))
            throw new IOException("A gravação do inventário não foi concluída. O registro durável, se existente, será recuperado na próxima tentativa.");
    }, cancellationToken);

    public Task<EventStoreSnapshot> LoadInvestigationsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var investigations = ReconcileEvents(database, warnings, cancellationToken);
        WriteEventProjection(investigations);
        var byId = investigations.ToDictionary(investigation => investigation.Id);
        var ordered = new List<EventInvestigation>();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Id FROM EventInvestigations ORDER BY CompletedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (Guid.TryParse(reader.GetString(0), out var id) && byId.TryGetValue(id, out var investigation))
                ordered.Add(investigation);
        return new EventStoreSnapshot(ordered, warnings);
    }, cancellationToken);

    public Task SaveInvestigationAsync(EventInvestigation investigation,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateInvestigation(investigation);
        EnsureDirectories();
        using var storeLock = Lock();
        foreach (var kind in Enum.GetValues<AuditKind>())
            if (File.Exists(Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant(), $"{investigation.Id:D}.md")))
                throw new InvalidDataException("ID já existe em uma auditoria; histórico preservado.");
        if (File.Exists(HardwareRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em um inventário; histórico preservado.");
        if (File.Exists(PerformanceRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de desempenho; histórico preservado.");
        if (File.Exists(NetworkRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de rede; histórico preservado.");
        var json = CanonicalJson(investigation);
        var path = EventRecordPath(investigation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = ReadEventRecord(path);
            if (CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = RecordEnvelope.Wrap(EventReport.Render(investigation), json);
            if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException("Investigação excede 4 MiB e não pode ser persistida neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
        }
        // Once the durable record exists, finish the index without the investigation cancellation token.
        // A crash here is recoverable by the next reconciliation from the immutable record.
        using var database = OpenDatabase([]);
        var investigations = ReconcileEvents(database, [], CancellationToken.None);
        WriteEventProjection(investigations);
        if (!investigations.Any(item => item.Id == investigation.Id) ||
            !File.Exists(GetEventReportPath(investigation.Id)))
            throw new IOException("A gravação da investigação não foi concluída. O registro durável, se existente, será recuperado na próxima tentativa.");
    }, cancellationToken);

    public Task<PerformanceStoreSnapshot> LoadSessionsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var sessions = ReconcilePerformance(database, warnings, cancellationToken);
        WritePerformanceProjection(sessions);
        var byId = sessions.ToDictionary(session => session.Id);
        var ordered = new List<PerformanceSession>();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Id FROM PerformanceSessions ORDER BY CompletedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (Guid.TryParse(reader.GetString(0), out var id) && byId.TryGetValue(id, out var session))
                ordered.Add(session);
        return new PerformanceStoreSnapshot(ordered, warnings);
    }, cancellationToken);

    public Task<long> SaveSessionAsync(PerformanceSession session,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateSession(session);
        EnsureDirectories();
        using var storeLock = Lock();
        foreach (var kind in Enum.GetValues<AuditKind>())
            if (File.Exists(Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant(), $"{session.Id:D}.md")))
                throw new InvalidDataException("ID já existe em uma auditoria; histórico preservado.");
        if (File.Exists(HardwareRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em um inventário; histórico preservado.");
        if (File.Exists(EventRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma investigação; histórico preservado.");
        if (File.Exists(NetworkRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de rede; histórico preservado.");
        var json = CanonicalJson(session);
        var path = PerformanceRecordPath(session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long bytes;
        if (File.Exists(path))
        {
            var (existing, existingHash) = ReadPerformanceRecord(path);
            if (existingHash != RecordEnvelope.Hash(json) && CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
            // Repetição idempotente: o envelope durável e o relatório existentes não são reescritos.
            bytes = new FileInfo(path).Length;
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = PerformanceReport.Render(session);
            var document = RecordEnvelope.Wrap(report, json);
            bytes = Encoding.UTF8.GetByteCount(document);
            if (bytes > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException("Sessão de desempenho excede 4 MiB e não pode ser persistida neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
            RecordEnvelope.AtomicWrite(GetPerformanceReportPath(session.Id), report, overwrite: false);
        }
        // Once the durable record exists, finish the index without the collection cancellation token.
        using var database = OpenDatabase([]);
        var sessions = ReconcilePerformance(database, [], CancellationToken.None);
        WritePerformanceProjection(sessions);
        if (!sessions.Any(item => item.Id == session.Id) ||
            !File.Exists(GetPerformanceReportPath(session.Id)))
            throw new IOException("A gravação da sessão de desempenho não foi concluída. O registro durável, se existente, será recuperado na próxima tentativa.");
        return bytes;
    }, cancellationToken);

    private List<PerformanceSession> ReconcilePerformance(SqliteConnection database, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var sessions = new List<PerformanceSession>();
        var sessionHashes = new Dictionary<Guid, string>();
        var seen = new HashSet<Guid>();
        var directory = Path.Combine(root, "memory", "performance");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (session, payloadHash) = ReadPerformanceRecord(path);
                    if (Path.GetFileName(path) != $"{session.Id:D}.md" || !seen.Add(session.Id))
                        throw new InvalidDataException("Identidade ou localização de sessão inválida.");
                    sessions.Add(session);
                    sessionHashes[session.Id] = payloadHash;
                    RecordEnvelope.AtomicWrite(GetPerformanceReportPath(session.Id),
                        PerformanceReport.Render(session), overwrite: true);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                    or UnauthorizedAccessException or ArgumentException or NullReferenceException)
                {
                    warnings.Add($"Sessão de desempenho {Path.GetFileName(path)} não recuperada ({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM PerformanceSessions";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seen.Contains(id))
                    warnings.Add($"Sessão de desempenho indexada {reader.GetString(0)} sem registro Markdown válido; excluída da validade.");
        }
        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM PerformanceObservations; DELETE FROM PerformanceSources; DELETE FROM PerformanceSessions";
            clear.ExecuteNonQuery();
        }
        foreach (var session in sessions)
            IndexPerformance(database, transaction, session, sessionHashes.GetValueOrDefault(session.Id));
        transaction.Commit();
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return sessions;
    }

    private void IndexPerformance(SqliteConnection database, SqliteTransaction transaction,
        PerformanceSession session, string? payloadHash = null)
    {
        var coverage = session.Coverage ?? PerformanceCoverage.FromLegacy(session);
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PerformanceSessions
                (Id, FormatVersion, SchemaVersion, SoftwareVersion, Preset, RequestedDurationSeconds,
                 SampleIntervalSeconds, StartedUtc, CompletedUtc, DurationSeconds, Status, MarkdownPath,
                 PayloadHash, StatisticsJson, AnalysisJson, SelfJson, LimitationsJson,
                 ExecutionStatus, CoverageStatus, CoverageJson)
            VALUES ($id,$format,$schema,$software,$preset,$requested,$interval,$start,$end,$duration,
                 $status,$path,$hash,$statistics,$analysis,$self,$limits,$execution,$coverage,$coverageJson)
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$format", session.FormatVersion);
        command.Parameters.AddWithValue("$schema", session.SchemaVersion);
        command.Parameters.AddWithValue("$software", session.SoftwareVersion);
        command.Parameters.AddWithValue("$preset", session.Preset.ToString());
        command.Parameters.AddWithValue("$requested", session.RequestedDuration.TotalSeconds);
        command.Parameters.AddWithValue("$interval", session.SampleInterval.TotalSeconds);
        command.Parameters.AddWithValue("$start", session.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", session.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$duration", session.Duration.TotalSeconds);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, PerformanceRecordPath(session.Id)));
        command.Parameters.AddWithValue("$hash", payloadHash ?? RecordEnvelope.Hash(CanonicalJson(session)));
        command.Parameters.AddWithValue("$statistics", JsonSerializer.Serialize(session.Statistics, AuditJson.Options));
        command.Parameters.AddWithValue("$analysis", JsonSerializer.Serialize(
            new { observations = session.Observations, notes = session.AnalysisNotes }, AuditJson.Options));
        command.Parameters.AddWithValue("$self", JsonSerializer.Serialize(session.SelfOverhead, AuditJson.Options));
        command.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(session.Limitations, AuditJson.Options));
        command.Parameters.AddWithValue("$execution", coverage.Execution.ToString());
        command.Parameters.AddWithValue("$coverage", coverage.Status.ToString());
        command.Parameters.AddWithValue("$coverageJson", JsonSerializer.Serialize(coverage, AuditJson.Options));
        command.ExecuteNonQuery();
        foreach (var source in session.Sources)
        {
            using var child = database.CreateCommand(); child.Transaction = transaction;
            child.CommandText = """
                INSERT INTO PerformanceSources
                    (SessionId, CollectorId, CollectorVersion, Category, Source, Status, StartedUtc,
                     CompletedUtc, DurationMs, SamplesRead, LimitHit, LimitationsJson, MeanReadMs, MaxReadMs)
                VALUES ($session,$id,$version,$category,$source,$status,$start,$end,$duration,$samples,$limit,
                     $limits,$mean,$max)
                """;
            child.Parameters.AddWithValue("$session", session.Id.ToString("D"));
            child.Parameters.AddWithValue("$id", source.CollectorId);
            child.Parameters.AddWithValue("$version", source.CollectorVersion);
            child.Parameters.AddWithValue("$category", source.Category.ToString());
            child.Parameters.AddWithValue("$source", source.Source);
            child.Parameters.AddWithValue("$status", source.Status.ToString());
            child.Parameters.AddWithValue("$start", source.StartedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$end", source.CompletedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$duration", source.Duration.TotalMilliseconds);
            child.Parameters.AddWithValue("$samples", source.SamplesRead);
            child.Parameters.AddWithValue("$limit", source.LimitHit ? 1 : 0);
            child.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(source.Limitations, AuditJson.Options));
            child.Parameters.AddWithValue("$mean", (object?)source.MeanReadMilliseconds ?? DBNull.Value);
            child.Parameters.AddWithValue("$max", (object?)source.MaxReadMilliseconds ?? DBNull.Value);
            child.ExecuteNonQuery();
        }
        foreach (var observation in session.Observations)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = "INSERT INTO PerformanceObservations VALUES ($session,$id,$rule,$kind,$severity,$confidence,$causality,$metric,$threshold,$subject,$from,$to,$supporting,$evaluated,$min,$max)";
            row.Parameters.AddWithValue("$session", session.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", observation.Id);
            row.Parameters.AddWithValue("$rule", observation.RuleId);
            row.Parameters.AddWithValue("$kind", observation.Kind.ToString());
            row.Parameters.AddWithValue("$severity", observation.Severity.ToString());
            row.Parameters.AddWithValue("$confidence", observation.Confidence.ToString());
            row.Parameters.AddWithValue("$causality", observation.Causality.ToString());
            row.Parameters.AddWithValue("$metric", observation.Metric);
            row.Parameters.AddWithValue("$threshold", (object?)observation.Threshold ?? DBNull.Value);
            row.Parameters.AddWithValue("$subject", (object?)observation.Subject ?? DBNull.Value);
            row.Parameters.AddWithValue("$from", observation.WindowFromUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$to", observation.WindowToUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$supporting", observation.SupportingSampleCount);
            row.Parameters.AddWithValue("$evaluated", observation.EvaluatedSampleCount);
            row.Parameters.AddWithValue("$min", (object?)observation.ObservedMin ?? DBNull.Value);
            row.Parameters.AddWithValue("$max", (object?)observation.ObservedMax ?? DBNull.Value);
            row.ExecuteNonQuery();
        }
    }

    private void WritePerformanceProjection(IReadOnlyList<PerformanceSession> sessions)
    {
        var latest = sessions.OrderByDescending(session => session.CompletedUtc).ToArray();
        var lastCompleted = latest.FirstOrDefault(session =>
            (session.Coverage ?? PerformanceCoverage.FromLegacy(session)).Execution ==
            PerformanceExecutionStatus.Completed);
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Sessões de desempenho");
        text.AppendLine();
        text.AppendLine("Formato: 2");
        text.AppendLine();
        text.AppendLine("Projeção derivada dos registros Markdown imutáveis; não é fonte de verdade.");
        text.AppendLine("Sessões de desempenho não renovam a validade de auditorias, inventários ou investigações.");
        text.AppendLine();
        text.AppendLine("## Última sessão concluída");
        text.AppendLine();
        text.AppendLine(lastCompleted is null ? "Nenhuma sessão concluída."
            : $"{lastCompleted.Id:D} — {lastCompleted.CompletedUtc:O} — {lastCompleted.Preset} — " +
              $"{ExecutionName((lastCompleted.Coverage ?? PerformanceCoverage.FromLegacy(lastCompleted)).Execution)} — " +
              $"cobertura {CoverageName((lastCompleted.Coverage ?? PerformanceCoverage.FromLegacy(lastCompleted)).Status)} — " +
              $"{lastCompleted.Samples.Count} amostra(s) — {lastCompleted.Observations.Count} observação(ões)");
        text.AppendLine();
        text.AppendLine("## Histórico");
        text.AppendLine();
        if (latest.Length == 0) text.AppendLine("Nenhuma sessão anterior.");
        else
            foreach (var session in latest)
            {
                var coverage = session.Coverage ?? PerformanceCoverage.FromLegacy(session);
                var quality = PerformanceQualityCatalog.Find(session.Id);
                text.AppendLine($"- [{session.Id:D}](performance/{session.Id:D}.md) — " +
                    $"{session.CompletedUtc:O} — execução {ExecutionName(coverage.Execution)} — " +
                    $"cobertura {CoverageName(coverage.Status)} — {session.Preset} — " +
                    $"{session.Duration.TotalSeconds:0}s — {session.Samples.Count} amostra(s) — " +
                    $"{session.Observations.Count} observação(ões)" +
                    (quality is null ? string.Empty : $" — ATENÇÃO: {quality.DefectId}"));
            }
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "PERFORMANCE.md"), text.ToString(), overwrite: true);
    }

    private static string ExecutionName(PerformanceExecutionStatus status) => status switch
    {
        PerformanceExecutionStatus.Completed => "concluída",
        PerformanceExecutionStatus.Cancelled => "cancelada",
        PerformanceExecutionStatus.Failed => "falhou",
        PerformanceExecutionStatus.Unsupported => "não suportada",
        _ => status.ToString()
    };

    private static string CoverageName(PerformanceCoverageStatus status) => status switch
    {
        PerformanceCoverageStatus.Complete => "completa",
        PerformanceCoverageStatus.Partial => "parcial",
        PerformanceCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    };

    private static (PerformanceSession Session, string PayloadHash) ReadPerformanceRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Sessão de desempenho excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var session = JsonSerializer.Deserialize<PerformanceSession>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de sessão de desempenho nulo.");
        ValidateSession(session);
        return (session, hash);
    }

    private static void ValidateSession(PerformanceSession session)
    {
        if (session is null) throw new InvalidDataException("Sessão de desempenho nula.");
        var sources = session.Sources;
        var observations = session.Observations;
        var notes = session.AnalysisNotes;
        var samples = session.Samples;
        if (session.FormatVersion is not (1 or 2) || session.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(session.SoftwareVersion) ||
            string.IsNullOrWhiteSpace(session.SchemaVersion) ||
            !Enum.IsDefined(session.Preset) || !Enum.IsDefined(session.Status) ||
            session.Status == PerformanceSessionStatus.Running ||
            session.RequestedDuration < TimeSpan.Zero || session.SampleInterval <= TimeSpan.Zero ||
            session.StartedUtc.Offset != TimeSpan.Zero || session.CompletedUtc.Offset != TimeSpan.Zero ||
            session.StartedUtc > session.CompletedUtc || session.Duration < TimeSpan.Zero ||
            session.Statistics is null || sources is null || observations is null || notes is null ||
            samples is null || session.SelfOverhead is null || session.Limitations is null)
            throw new InvalidDataException("Registro de sessão de desempenho inválido ou versão incompatível.");

        if (sources.Count == 0 ||
            sources.Select(source => source?.CollectorId).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.CollectorId) ||
                string.IsNullOrWhiteSpace(source.CollectorVersion) || string.IsNullOrWhiteSpace(source.Source) ||
                !Enum.IsDefined(source.Category) || !Enum.IsDefined(source.Status) ||
                source.Status == PerformanceSourceStatus.Running ||
                source.StartedUtc > source.CompletedUtc || source.Duration < TimeSpan.Zero ||
                source.SamplesRead < 0 || source.Limitations is null ||
                source.MeanReadMilliseconds is < 0 || source.MaxReadMilliseconds is < 0))
            throw new InvalidDataException("Fontes da sessão de desempenho inválidas.");

        if (session.FormatVersion == 1 && session.Coverage is not null ||
            session.FormatVersion == 2 && session.Coverage is null)
            throw new InvalidDataException("Cobertura da sessão de desempenho incompatível com o formato.");
        if (session.Coverage is { } coverage &&
            (!Enum.IsDefined(coverage.Execution) || coverage.Execution == PerformanceExecutionStatus.Running ||
                !Enum.IsDefined(coverage.Status) ||
                !ExecutionMatches(coverage.Execution, session.Status) ||
                coverage.ExpectedSamples < 0 || coverage.CollectedSamples != samples.Count ||
                coverage.MissedOpportunities < 0 || coverage.UnavailableCollectors is null ||
                coverage.Limitations is null ||
                coverage.UnavailableCollectors.Distinct(StringComparer.Ordinal).Count() !=
                coverage.UnavailableCollectors.Count ||
                coverage.UnavailableCollectors.Any(string.IsNullOrWhiteSpace) ||
                coverage.Status == PerformanceCoverageStatus.Complete &&
                (coverage.Execution != PerformanceExecutionStatus.Completed ||
                 coverage.CollectedSamples < coverage.ExpectedSamples || coverage.SampleLimitReached ||
                 coverage.ProcessLimitReached || coverage.StorageLimitReached ||
                 coverage.MissedOpportunities != 0 || coverage.UnavailableCollectors.Count != 0 ||
                 sources.Any(s => s.Status != PerformanceSourceStatus.Completed))))
            throw new InvalidDataException("Cobertura da sessão de desempenho inválida.");

        if (session.Statistics.TotalSamples != samples.Count ||
            session.Statistics.CpuTotalPercent is null ||
            session.Statistics.CpuUserPercent is null ||
            session.Statistics.CpuKernelPercent is null ||
            session.Statistics.ProcessorQueueLength is null ||
            session.Statistics.MemoryAvailablePercent is null ||
            session.Statistics.MemoryUsedBytes is null ||
            session.Statistics.CommitUsagePercent is null ||
            session.Statistics.PagesPerSecond is null ||
            session.Statistics.Processes is null ||
            session.Statistics.Storage is null ||
            session.Statistics.Limitations is null)
            throw new InvalidDataException("Estatísticas da sessão de desempenho estruturalmente inválidas ou inconsistentes.");

        if (samples.Select(sample => sample?.Index).Distinct().Count() != samples.Count ||
            samples.Where((sample, position) => sample is null || sample.Index != position).Any() ||
            samples.Any(sample => sample.TimestampUtc.Offset != TimeSpan.Zero ||
                sample.Processes is null || sample.Exits is null || sample.Storage is null ||
                sample.Limitations is null || sample.CollectionDurationMilliseconds < 0))
            throw new InvalidDataException("Amostras da sessão de desempenho inválidas.");

        if (observations.Select(observation => observation?.Id)
                .Distinct(StringComparer.Ordinal).Count() != observations.Count ||
            observations.Any(observation => observation is null || string.IsNullOrWhiteSpace(observation.Id) ||
                string.IsNullOrWhiteSpace(observation.RuleId) || string.IsNullOrWhiteSpace(observation.Title) ||
                string.IsNullOrWhiteSpace(observation.Metric) || string.IsNullOrWhiteSpace(observation.Source) ||
                !Enum.IsDefined(observation.Kind) || !Enum.IsDefined(observation.Severity) ||
                !Enum.IsDefined(observation.Confidence) || !Enum.IsDefined(observation.Causality) ||
                observation.Causality != CausalityStatus.NotAssessed ||
                observation.WindowFromUtc.Offset != TimeSpan.Zero ||
                observation.WindowToUtc.Offset != TimeSpan.Zero ||
                observation.WindowFromUtc > observation.WindowToUtc ||
                observation.SupportingSampleCount < 0 || observation.EvaluatedSampleCount < 0 ||
                observation.Points is null || observation.Limitations is null))
            throw new InvalidDataException("Observações da sessão de desempenho inválidas.");

        if (notes.Any(note => note is null || string.IsNullOrWhiteSpace(note.RuleId) ||
                string.IsNullOrWhiteSpace(note.RuleVersion) || note.Detail is null))
            throw new InvalidDataException("Notas de análise da sessão de desempenho inválidas.");
    }

    private static bool ExecutionMatches(PerformanceExecutionStatus execution, PerformanceSessionStatus status) =>
        execution switch
        {
            PerformanceExecutionStatus.Completed => status == PerformanceSessionStatus.Completed,
            PerformanceExecutionStatus.Cancelled => status == PerformanceSessionStatus.Cancelled,
            PerformanceExecutionStatus.Failed => status == PerformanceSessionStatus.Failed,
            PerformanceExecutionStatus.Unsupported => status == PerformanceSessionStatus.Unsupported,
            _ => false
        };

    private static string CanonicalJson(PerformanceSession session) =>
        JsonSerializer.Serialize(session, AuditJson.Options).Replace("\r\n", "\n");

    public Task<NetworkStoreSnapshot> LoadNetworkSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            EnsureDirectories();
            using var storeLock = Lock();
            var warnings = new List<string>();
            using var database = OpenDatabase(warnings);
            var sessions = ReconcileNetworks(database, warnings, cancellationToken);
            WriteNetworkProjection(sessions);
            var byId = sessions.ToDictionary(session => session.Id);
            var ordered = new List<NetworkSession>();
            using var query = database.CreateCommand();
            query.CommandText = "SELECT Id FROM NetworkSessions ORDER BY CompletedUtc DESC, Id";
            using var reader = query.ExecuteReader();
            while (reader.Read())
                if (Guid.TryParse(reader.GetString(0), out var id) && byId.TryGetValue(id, out var session))
                    ordered.Add(session);
            return new NetworkStoreSnapshot(ordered, warnings);
        }, cancellationToken);

    public Task<long> SaveNetworkSessionAsync(NetworkSession session,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateNetworkSession(session);
        EnsureDirectories();
        using var storeLock = Lock();
        foreach (var kind in Enum.GetValues<AuditKind>())
            if (File.Exists(Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant(), $"{session.Id:D}.md")))
                throw new InvalidDataException("ID já existe em uma auditoria; histórico preservado.");
        if (File.Exists(HardwareRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em um inventário; histórico preservado.");
        if (File.Exists(EventRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma investigação; histórico preservado.");
        if (File.Exists(PerformanceRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de desempenho; histórico preservado.");
        var json = CanonicalJson(session);
        var path = NetworkRecordPath(session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long bytes;
        if (File.Exists(path))
        {
            var (existing, existingHash) = ReadNetworkRecord(path);
            if (existingHash != RecordEnvelope.Hash(json) && CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
            // Repetição idempotente: o envelope durável e o relatório existentes não são reescritos.
            bytes = new FileInfo(path).Length;
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = NetworkReport.Render(session);
            var document = RecordEnvelope.Wrap(report, json);
            bytes = Encoding.UTF8.GetByteCount(document);
            if (bytes > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException("Sessão de rede excede 4 MiB e não pode ser persistida neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
            RecordEnvelope.AtomicWrite(GetNetworkReportPath(session.Id), report, overwrite: false);
        }
        // Once the durable record exists, finish the index without the collection cancellation token.
        using var database = OpenDatabase([]);
        var sessions = ReconcileNetworks(database, [], CancellationToken.None);
        WriteNetworkProjection(sessions);
        if (!sessions.Any(item => item.Id == session.Id) || !File.Exists(GetNetworkReportPath(session.Id)))
            throw new IOException("A gravação da sessão de rede não foi concluída. O registro durável, se existente, " +
                "será recuperado na próxima tentativa.");
        return bytes;
    }, cancellationToken);

    private List<NetworkSession> ReconcileNetworks(SqliteConnection database, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var sessions = new List<NetworkSession>();
        var sessionHashes = new Dictionary<Guid, string>();
        var seen = new HashSet<Guid>();
        var directory = Path.Combine(root, "memory", "network");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (session, payloadHash) = ReadNetworkRecord(path);
                    if (Path.GetFileName(path) != $"{session.Id:D}.md" || !seen.Add(session.Id))
                        throw new InvalidDataException("Identidade ou localização de sessão de rede inválida.");
                    sessions.Add(session);
                    sessionHashes[session.Id] = payloadHash;
                    RecordEnvelope.AtomicWrite(GetNetworkReportPath(session.Id),
                        NetworkReport.Render(session), overwrite: true);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                    or UnauthorizedAccessException or ArgumentException or NullReferenceException)
                {
                    warnings.Add($"Sessão de rede {Path.GetFileName(path)} não recuperada " +
                        $"({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM NetworkSessions";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seen.Contains(id))
                    warnings.Add($"Sessão de rede indexada {reader.GetString(0)} sem registro Markdown válido; " +
                        "excluída da validade.");
        }
        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM NetworkObservations; DELETE FROM NetworkSources; DELETE FROM NetworkSessions";
            clear.ExecuteNonQuery();
        }
        foreach (var session in sessions)
            IndexNetwork(database, transaction, session, sessionHashes.GetValueOrDefault(session.Id));
        transaction.Commit();
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return sessions;
    }

    private void IndexNetwork(SqliteConnection database, SqliteTransaction transaction,
        NetworkSession session, string? payloadHash = null)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO NetworkSessions
                (Id, FormatVersion, SchemaVersion, SoftwareVersion, Preset, RequestedDurationSeconds,
                 SampleIntervalSeconds, StartedUtc, CompletedUtc, DurationSeconds, Status, ExecutionStatus,
                 CoverageStatus, SampleCount, EndpointDetailCount, IncludesAddresses, MarkdownPath, PayloadHash,
                 StatisticsJson, CoverageJson, PortRangesJson, AdaptersJson, TcpStatisticsJson, UdpStatisticsJson,
                 TcpipEventsJson, SelfJson, LimitationsJson)
            VALUES ($id,$format,$schema,$software,$preset,$requested,$interval,$start,$end,$duration,$status,
                 $execution,$coverage,$samples,$details,$addresses,$path,$hash,$statistics,$coverageJson,
                 $ranges,$adapters,$tcp,$udp,$tcpip,$self,$limits)
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$format", session.FormatVersion);
        command.Parameters.AddWithValue("$schema", session.SchemaVersion);
        command.Parameters.AddWithValue("$software", session.SoftwareVersion);
        command.Parameters.AddWithValue("$preset", session.Preset.ToString());
        command.Parameters.AddWithValue("$requested", session.RequestedDuration.TotalSeconds);
        command.Parameters.AddWithValue("$interval", session.SampleInterval.TotalSeconds);
        command.Parameters.AddWithValue("$start", session.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", session.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$duration", session.Duration.TotalSeconds);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$execution", (object?)session.Coverage?.Execution.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$coverage", (object?)session.Coverage?.Status.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$samples", session.Samples.Count);
        command.Parameters.AddWithValue("$details", session.EndpointDetails.Count);
        command.Parameters.AddWithValue("$addresses", session.IncludesAddresses ? 1 : 0);
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, NetworkRecordPath(session.Id)));
        command.Parameters.AddWithValue("$hash", payloadHash ?? RecordEnvelope.Hash(CanonicalJson(session)));
        command.Parameters.AddWithValue("$statistics", JsonSerializer.Serialize(session.Statistics, AuditJson.Options));
        command.Parameters.AddWithValue("$coverageJson", session.Coverage is null ? DBNull.Value
            : JsonSerializer.Serialize(session.Coverage, AuditJson.Options));
        command.Parameters.AddWithValue("$ranges",
            JsonSerializer.Serialize(session.PortRanges, AuditJson.Options));
        command.Parameters.AddWithValue("$adapters", JsonSerializer.Serialize(session.Adapters, AuditJson.Options));
        command.Parameters.AddWithValue("$tcp", session.TcpStatistics is null ? DBNull.Value
            : JsonSerializer.Serialize(session.TcpStatistics, AuditJson.Options));
        command.Parameters.AddWithValue("$udp", session.UdpStatistics is null ? DBNull.Value
            : JsonSerializer.Serialize(session.UdpStatistics, AuditJson.Options));
        command.Parameters.AddWithValue("$tcpip", session.TcpipEvents is null ? DBNull.Value
            : JsonSerializer.Serialize(session.TcpipEvents, AuditJson.Options));
        command.Parameters.AddWithValue("$self", JsonSerializer.Serialize(session.SelfOverhead, AuditJson.Options));
        command.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(session.Limitations, AuditJson.Options));
        command.ExecuteNonQuery();
        foreach (var source in session.Sources)
        {
            using var child = database.CreateCommand(); child.Transaction = transaction;
            child.CommandText = """
                INSERT INTO NetworkSources
                    (SessionId, CollectorId, CollectorVersion, Category, Source, Status, StartedUtc,
                     CompletedUtc, DurationMs, SamplesRead, LimitHit, LimitationsJson, MeanReadMs, MaxReadMs)
                VALUES ($session,$id,$version,$category,$source,$status,$start,$end,$duration,$samples,$limit,
                     $limits,$mean,$max)
                """;
            child.Parameters.AddWithValue("$session", session.Id.ToString("D"));
            child.Parameters.AddWithValue("$id", source.CollectorId);
            child.Parameters.AddWithValue("$version", source.CollectorVersion);
            child.Parameters.AddWithValue("$category", source.Category.ToString());
            child.Parameters.AddWithValue("$source", source.Source);
            child.Parameters.AddWithValue("$status", source.Status.ToString());
            child.Parameters.AddWithValue("$start", source.StartedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$end", source.CompletedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$duration", source.Duration.TotalMilliseconds);
            child.Parameters.AddWithValue("$samples", source.SamplesRead);
            child.Parameters.AddWithValue("$limit", source.LimitHit ? 1 : 0);
            child.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(source.Limitations, AuditJson.Options));
            child.Parameters.AddWithValue("$mean", (object?)source.MeanReadMilliseconds ?? DBNull.Value);
            child.Parameters.AddWithValue("$max", (object?)source.MaxReadMilliseconds ?? DBNull.Value);
            child.ExecuteNonQuery();
        }
        foreach (var observation in session.Observations)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = """
                INSERT INTO NetworkObservations
                    (SessionId, ObservationId, RuleId, Kind, Severity, Confidence, Causality, Metric, Threshold,
                     Subject, WindowFromUtc, WindowToUtc, SupportingSamples, EvaluatedSamples, ObservedMin,
                     ObservedMax)
                VALUES ($session,$id,$rule,$kind,$severity,$confidence,$causality,$metric,$threshold,$subject,
                     $from,$to,$supporting,$evaluated,$min,$max)
                """;
            row.Parameters.AddWithValue("$session", session.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", observation.Id);
            row.Parameters.AddWithValue("$rule", observation.RuleId);
            row.Parameters.AddWithValue("$kind", observation.Kind.ToString());
            row.Parameters.AddWithValue("$severity", observation.Severity.ToString());
            row.Parameters.AddWithValue("$confidence", observation.Confidence.ToString());
            row.Parameters.AddWithValue("$causality", observation.Causality.ToString());
            row.Parameters.AddWithValue("$metric", observation.Metric);
            row.Parameters.AddWithValue("$threshold", (object?)observation.Threshold ?? DBNull.Value);
            row.Parameters.AddWithValue("$subject", (object?)observation.Subject ?? DBNull.Value);
            row.Parameters.AddWithValue("$from", observation.WindowFromUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$to", observation.WindowToUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$supporting", observation.SupportingSampleCount);
            row.Parameters.AddWithValue("$evaluated", observation.EvaluatedSampleCount);
            row.Parameters.AddWithValue("$min", (object?)observation.ObservedMin ?? DBNull.Value);
            row.Parameters.AddWithValue("$max", (object?)observation.ObservedMax ?? DBNull.Value);
            row.ExecuteNonQuery();
        }
    }

    private void WriteNetworkProjection(IReadOnlyList<NetworkSession> sessions)
    {
        var latest = sessions.OrderByDescending(session => session.CompletedUtc).ToArray();
        var lastCompleted = latest.FirstOrDefault(session => session.Coverage?.Status == NetworkCoverageStatus.Complete);
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Sessões de rede");
        text.AppendLine();
        text.AppendLine("Formato: 1");
        text.AppendLine();
        text.AppendLine("Projeção derivada dos registros Markdown imutáveis; não é fonte de verdade.");
        text.AppendLine("Diagnóstico de rede não renova a validade de auditorias, inventários ou investigações.");
        text.AppendLine();
        text.AppendLine("## Última sessão com cobertura completa");
        text.AppendLine();
        text.AppendLine(lastCompleted is null ? "Nenhuma sessão com cobertura completa."
            : $"{lastCompleted.Id:D} — {lastCompleted.CompletedUtc:O} — {lastCompleted.Preset} — " +
              $"{lastCompleted.Samples.Count} amostra(s) — {lastCompleted.Observations.Count} observação(ões)");
        text.AppendLine();
        text.AppendLine("## Histórico");
        text.AppendLine();
        if (latest.Length == 0) text.AppendLine("Nenhuma sessão anterior.");
        else
            foreach (var session in latest)
            {
                var coverage = session.Coverage;
                text.AppendLine($"- [{session.Id:D}](network/{session.Id:D}.md) — {session.CompletedUtc:O} — " +
                    $"{session.Preset} — situação {NetworkStatusName(session.Status)} — " +
                    $"cobertura {NetworkCoverageName(coverage?.Status)} — " +
                    $"{session.Duration.TotalSeconds:0}s — {session.Samples.Count} amostra(s) — " +
                    $"{session.Observations.Count} observação(ões) — " +
                    $"{(session.IncludesAddresses ? "com endereços" : "sem endereços")}");
            }
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "NETWORK.md"), text.ToString(), overwrite: true);
    }

    private static string NetworkStatusName(NetworkSessionStatus status) => status switch
    {
        NetworkSessionStatus.Completed => "concluída",
        NetworkSessionStatus.Partial => "parcial",
        NetworkSessionStatus.Cancelled => "cancelada",
        NetworkSessionStatus.Failed => "falhou",
        NetworkSessionStatus.Unsupported => "não suportada",
        NetworkSessionStatus.Running => "em execução",
        _ => status.ToString()
    };

    private static string NetworkCoverageName(NetworkCoverageStatus? status) => status switch
    {
        NetworkCoverageStatus.Complete => "completa",
        NetworkCoverageStatus.Partial => "parcial",
        NetworkCoverageStatus.Unavailable => "indisponível",
        _ => "não informada"
    };

    private static (NetworkSession Session, string PayloadHash) ReadNetworkRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Sessão de rede excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var session = JsonSerializer.Deserialize<NetworkSession>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de sessão de rede nulo.");
        ValidateNetworkSession(session);
        return (session, hash);
    }

    private static void ValidateNetworkSession(NetworkSession session)
    {
        if (session is null) throw new InvalidDataException("Sessão de rede nula.");
        var sources = session.Sources;
        var observations = session.Observations;
        var notes = session.AnalysisNotes;
        var samples = session.Samples;
        var details = session.EndpointDetails;
        if (session.FormatVersion != 1 || session.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(session.SoftwareVersion) ||
            string.IsNullOrWhiteSpace(session.SchemaVersion) ||
            !Enum.IsDefined(session.Preset) || !Enum.IsDefined(session.Status) ||
            session.Status == NetworkSessionStatus.Running ||
            session.RequestedDuration < TimeSpan.Zero || session.SampleInterval < TimeSpan.Zero ||
            session.StartedUtc.Offset != TimeSpan.Zero || session.CompletedUtc.Offset != TimeSpan.Zero ||
            session.StartedUtc > session.CompletedUtc || session.Duration < TimeSpan.Zero ||
            session.Statistics is null || sources is null || observations is null || notes is null ||
            samples is null || details is null || session.SelfOverhead is null || session.Limitations is null)
            throw new InvalidDataException("Registro de sessão de rede inválido ou versão incompatível.");

        if (session.Preset == NetworkSessionPreset.Snapshot
                ? session.RequestedDuration != TimeSpan.Zero || session.SampleInterval != TimeSpan.Zero
                : session.RequestedDuration <= TimeSpan.Zero || session.SampleInterval <= TimeSpan.Zero)
            throw new InvalidDataException("Preset da sessão de rede inconsistente com duração e intervalo.");

        if (sources.Count == 0 ||
            sources.Select(source => source?.CollectorId).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.CollectorId) ||
                string.IsNullOrWhiteSpace(source.CollectorVersion) || string.IsNullOrWhiteSpace(source.Source) ||
                !Enum.IsDefined(source.Category) || !Enum.IsDefined(source.Status) ||
                source.Status == NetworkSourceStatus.Running ||
                source.StartedUtc > source.CompletedUtc || source.Duration < TimeSpan.Zero ||
                source.SamplesRead < 0 || source.Limitations is null ||
                source.MeanReadMilliseconds is < 0 || source.MaxReadMilliseconds is < 0))
            throw new InvalidDataException("Fontes da sessão de rede inválidas.");

        if (session.Coverage is not { } coverage ||
            !Enum.IsDefined(coverage.Execution) || coverage.Execution == NetworkExecutionStatus.Running ||
            !Enum.IsDefined(coverage.Status) ||
            coverage.ExpectedSamples < 1 || coverage.CollectedSamples != samples.Count ||
            coverage.MissedOpportunities < 0 || coverage.UnavailableCollectors is null ||
            coverage.Limitations is null ||
            coverage.UnavailableCollectors.Distinct(StringComparer.Ordinal).Count() !=
            coverage.UnavailableCollectors.Count ||
            coverage.UnavailableCollectors.Any(string.IsNullOrWhiteSpace) ||
            coverage.Execution == NetworkExecutionStatus.Cancelled && session.Status != NetworkSessionStatus.Cancelled ||
            coverage.Execution == NetworkExecutionStatus.Failed && session.Status != NetworkSessionStatus.Failed ||
            coverage.Execution == NetworkExecutionStatus.Unsupported && session.Status != NetworkSessionStatus.Unsupported ||
            coverage.Execution == NetworkExecutionStatus.Completed &&
            session.Status is NetworkSessionStatus.Cancelled or NetworkSessionStatus.Failed
                or NetworkSessionStatus.Unsupported ||
            coverage.Status == NetworkCoverageStatus.Complete &&
            (coverage.Execution != NetworkExecutionStatus.Completed ||
             coverage.CollectedSamples < coverage.ExpectedSamples || coverage.SampleLimitReached ||
             coverage.DetailLimitReached || coverage.MissedOpportunities != 0 ||
             coverage.UnavailableCollectors.Count != 0 ||
             sources.Any(source => source.Status != NetworkSourceStatus.Completed)))
            throw new InvalidDataException("Cobertura da sessão de rede inválida.");

        if (samples.Select(sample => sample?.Index).Distinct().Count() != samples.Count ||
            samples.Where((sample, position) => sample is null || sample.Index != position).Any() ||
            samples.Any(sample => sample.TimestampUtc.Offset != TimeSpan.Zero ||
                sample.ElapsedSeconds < 0 || sample.TcpConnections < 0 || sample.UdpEndpoints < 0 ||
                sample.ListeningTcp < 0 || sample.DistinctLocalPorts < 0 ||
                sample.TcpStates is null || sample.TopLocalPorts is null || sample.TopProcesses is null ||
                sample.Limitations is null || sample.CollectionDurationMilliseconds < 0))
            throw new InvalidDataException("Amostras da sessão de rede inválidas.");

        if (session.Statistics.TotalSamples != samples.Count ||
            session.Statistics.FinalStates is null || session.Statistics.FinalTopLocalPorts is null ||
            session.Statistics.FinalTopProcesses is null || session.Statistics.Limitations is null)
            throw new InvalidDataException("Estatísticas da sessão de rede estruturalmente inválidas.");

        if (!session.IncludesAddresses && details.Any(endpoint =>
                endpoint is null || endpoint.LocalAddress is not null || endpoint.RemoteAddress is not null))
            throw new InvalidDataException(
                "Sessão de rede sem autorização de endereços contém endereço preservado.");
        if (details.Any(endpoint => endpoint is null || endpoint.ObservedUtc.Offset != TimeSpan.Zero ||
                endpoint.Pid < 0 || string.IsNullOrWhiteSpace(endpoint.Source)))
            throw new InvalidDataException("Detalhes de endpoint da sessão de rede inválidos.");
        if (session.IncludesEndpointDetails != (details.Count > 0))
            throw new InvalidDataException("Sinalização de detalhes de endpoint inconsistente.");

        if (observations.Select(observation => observation?.Id)
                .Distinct(StringComparer.Ordinal).Count() != observations.Count ||
            observations.Any(observation => observation is null || string.IsNullOrWhiteSpace(observation.Id) ||
                string.IsNullOrWhiteSpace(observation.RuleId) || string.IsNullOrWhiteSpace(observation.Title) ||
                string.IsNullOrWhiteSpace(observation.Metric) || string.IsNullOrWhiteSpace(observation.Source) ||
                !Enum.IsDefined(observation.Kind) || !Enum.IsDefined(observation.Severity) ||
                !Enum.IsDefined(observation.Confidence) || !Enum.IsDefined(observation.Causality) ||
                observation.Causality != CausalityStatus.NotAssessed ||
                observation.WindowFromUtc.Offset != TimeSpan.Zero ||
                observation.WindowToUtc.Offset != TimeSpan.Zero ||
                observation.WindowFromUtc > observation.WindowToUtc ||
                observation.SupportingSampleCount < 0 || observation.EvaluatedSampleCount < 0 ||
                observation.Points is null || observation.Limitations is null))
            throw new InvalidDataException("Observações da sessão de rede inválidas.");

        if (notes.Any(note => note is null || string.IsNullOrWhiteSpace(note.RuleId) ||
                string.IsNullOrWhiteSpace(note.RuleVersion) || note.Detail is null))
            throw new InvalidDataException("Notas de análise da sessão de rede inválidas.");
    }

    private static string CanonicalJson(NetworkSession session) =>
        JsonSerializer.Serialize(session, AuditJson.Options).Replace("\r\n", "\n");

    private List<EventInvestigation> ReconcileEvents(SqliteConnection database, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var investigations = new List<EventInvestigation>();
        var seen = new HashSet<Guid>();
        var directory = Path.Combine(root, "memory", "events");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var investigation = ReadEventRecord(path);
                    if (Path.GetFileName(path) != $"{investigation.Id:D}.md" || !seen.Add(investigation.Id))
                        throw new InvalidDataException("Identidade ou localização de investigação inválida.");
                    investigations.Add(investigation);
                    RecordEnvelope.AtomicWrite(GetEventReportPath(investigation.Id),
                        EventReport.Render(investigation), overwrite: true);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                    or UnauthorizedAccessException or ArgumentException)
                {
                    warnings.Add($"Investigação {Path.GetFileName(path)} não recuperada ({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM EventInvestigations";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seen.Contains(id))
                    warnings.Add($"Investigação indexada {reader.GetString(0)} sem registro Markdown válido; excluída da validade.");
        }
        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM EventGroups; DELETE FROM EventSources; DELETE FROM EventInvestigations";
            clear.ExecuteNonQuery();
        }
        foreach (var investigation in investigations) IndexEvent(database, transaction, investigation);
        transaction.Commit();
        WriteQualityProjection(investigations);
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return investigations;
    }

    private void IndexEvent(SqliteConnection database, SqliteTransaction transaction,
        EventInvestigation investigation)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO EventInvestigations VALUES ($id,$format,$schema,$software,$window,$from,$to,$start,$end,$status,$path,$hash,$groups,$limits)";
        command.Parameters.AddWithValue("$id", investigation.Id.ToString("D"));
        command.Parameters.AddWithValue("$format", investigation.FormatVersion);
        command.Parameters.AddWithValue("$schema", investigation.SchemaVersion);
        command.Parameters.AddWithValue("$software", investigation.SoftwareVersion);
        command.Parameters.AddWithValue("$window", investigation.Window.ToString());
        command.Parameters.AddWithValue("$from", investigation.PeriodFromUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", investigation.PeriodToUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$start", investigation.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", investigation.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$status", investigation.Status.ToString());
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, EventRecordPath(investigation.Id)));
        command.Parameters.AddWithValue("$hash", RecordEnvelope.Hash(CanonicalJson(investigation)));
        command.Parameters.AddWithValue("$groups", JsonSerializer.Serialize(investigation.Groups, AuditJson.Options));
        command.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(investigation.Limitations, AuditJson.Options));
        command.ExecuteNonQuery();
        foreach (var source in investigation.Sources)
        {
            using var child = database.CreateCommand(); child.Transaction = transaction;
            child.CommandText = "INSERT INTO EventSources VALUES ($investigation,$id,$version,$source,$status,$start,$end,$duration,$events,$reports,$limit,$limits)";
            child.Parameters.AddWithValue("$investigation", investigation.Id.ToString("D"));
            child.Parameters.AddWithValue("$id", source.CollectorId);
            child.Parameters.AddWithValue("$version", source.CollectorVersion);
            child.Parameters.AddWithValue("$source", source.Source);
            child.Parameters.AddWithValue("$status", source.Status.ToString());
            child.Parameters.AddWithValue("$start", source.StartedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$end", source.CompletedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$duration", source.Duration.TotalMilliseconds);
            child.Parameters.AddWithValue("$events", source.EventsRead);
            child.Parameters.AddWithValue("$reports", source.ReportsRead);
            child.Parameters.AddWithValue("$limit", source.LimitHit ? 1 : 0);
            child.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(source.Limitations, AuditJson.Options));
            child.ExecuteNonQuery();
        }
        foreach (var group in investigation.Groups)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = "INSERT INTO EventGroups VALUES ($investigation,$id,$category,$title,$severity,$confidence,$causality,$events,$reports,$presentations,$first,$last)";
            row.Parameters.AddWithValue("$investigation", investigation.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", group.Id);
            row.Parameters.AddWithValue("$category", group.Category.ToString());
            row.Parameters.AddWithValue("$title", group.Title);
            row.Parameters.AddWithValue("$severity", group.Severity.ToString());
            row.Parameters.AddWithValue("$confidence", group.Confidence.ToString());
            row.Parameters.AddWithValue("$causality", group.Causality.ToString());
            row.Parameters.AddWithValue("$events", group.EventRecordCount);
            row.Parameters.AddWithValue("$reports", group.DistinctReportCount);
            row.Parameters.AddWithValue("$presentations", group.RePresentationCount);
            row.Parameters.AddWithValue("$first", group.FirstObservedUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
            row.Parameters.AddWithValue("$last", group.LastObservedUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
            row.ExecuteNonQuery();
        }
    }

    private void WriteEventProjection(IReadOnlyList<EventInvestigation> investigations)
    {
        var latest = investigations.OrderByDescending(investigation => investigation.CompletedUtc).ToArray();
        var lastSuccess = latest.FirstOrDefault(investigation =>
            investigation.Status == EventInvestigationStatus.Completed &&
            EventQualityRules.Evaluate(investigation).State != EventQualityState.Affected &&
            investigation.CompletedUtc <= DateTimeOffset.UtcNow);
        var lastWithQualityReservation = latest.FirstOrDefault(investigation =>
            investigation.Status == EventInvestigationStatus.Completed &&
            EventQualityRules.Evaluate(investigation).State == EventQualityState.Affected &&
            investigation.CompletedUtc <= DateTimeOffset.UtcNow);
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Investigações de estabilidade e eventos");
        text.AppendLine();
        text.AppendLine("Formato: 1");
        text.AppendLine();
        text.AppendLine("Este índice é derivado dos registros Markdown imutáveis e não é fonte de verdade.");
        text.AppendLine("Investigações de eventos não renovam a validade das auditorias de sistema ou de hardware.");
        text.AppendLine();
        text.AppendLine("## Última investigação concluída");
        text.AppendLine();
        text.AppendLine(lastSuccess is null ? "Nenhuma investigação concluída com sucesso."
            : $"{lastSuccess.Id:D} — {lastSuccess.CompletedUtc:O} — versão {lastSuccess.SoftwareVersion} — " +
              $"{lastSuccess.Groups.Count} grupo(s)");
        text.AppendLine();
        text.AppendLine("## Última concluída com ressalva de qualidade");
        text.AppendLine();
        text.AppendLine(lastWithQualityReservation is null ? "Nenhuma."
            : $"{lastWithQualityReservation.Id:D} — {lastWithQualityReservation.CompletedUtc:O} — " +
              "contagens WER potencialmente superestimadas; registro preservado.");
        text.AppendLine();
        text.AppendLine("## Histórico");
        text.AppendLine();
        if (latest.Length == 0) text.AppendLine("Nenhuma investigação anterior.");
        else
            foreach (var investigation in latest)
                text.AppendLine($"- [{investigation.Id:D}](events/{investigation.Id:D}.md) — " +
                    $"{investigation.CompletedUtc:O} — {investigation.Status} — janela {investigation.Window} — " +
                    $"{investigation.Groups.Count} grupo(s) — {investigation.Events.Count} registro(s) — " +
                    $"{investigation.Reports.Select(r => r.ReportId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} relatório(s) distinto(s)");
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "EVENTS.md"), text.ToString(), overwrite: true);
    }

    private void WriteQualityProjection(IReadOnlyList<EventInvestigation> investigations)
    {
        var assessments = investigations
            .Select(investigation => (Investigation: investigation, Quality: EventQualityRules.Evaluate(investigation)))
            .OrderByDescending(item => item.Investigation.CompletedUtc)
            .ToArray();
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Qualidade de dados");
        text.AppendLine();
        text.AppendLine("Projeção derivada. Nenhum registro histórico é alterado ou substituído.");
        text.AppendLine($"Regra: {EventQualityRules.WerWindowRuleId} {EventQualityRules.WerWindowRuleVersion} — " +
            "horário efetivo de relatório WER (`EventTimeUtc ?? CreationTimeUtc`) fora do período da investigação.");
        text.AppendLine();
        text.AppendLine("## Investigações afetadas");
        text.AppendLine();
        var affected = assessments.Where(item => item.Quality.State == EventQualityState.Affected).ToArray();
        if (affected.Length == 0) text.AppendLine("Nenhuma.");
        foreach (var (investigation, quality) in affected)
        {
            text.AppendLine($"- `{investigation.Id:D}` — {quality.ReportsOutsidePeriod}/{quality.DistinctReports} " +
                $"relatório(s) WER fora do período — contagens WER não confiáveis; eventos preservados.");
            foreach (var item in quality.Evidence)
                text.AppendLine($"  - `{item.ReportId}` — {item.EffectiveTimeUtc:O} — {item.Observation}");
            text.AppendLine($"  - SHA-256 do registro: `{RecordEnvelope.Hash(CanonicalJson(investigation))}`");
        }
        text.AppendLine();
        text.AppendLine("## Verificadas");
        text.AppendLine();
        foreach (var (_, quality) in assessments.Where(item => item.Quality.State == EventQualityState.Verified))
            text.AppendLine($"- `{quality.InvestigationId:D}` — {quality.Summary}");
        text.AppendLine();
        text.AppendLine("## Não determinadas ou não aplicáveis");
        text.AppendLine();
        foreach (var (_, quality) in assessments.Where(item =>
                     item.Quality.State is EventQualityState.Undetermined or EventQualityState.NotApplicable))
            text.AppendLine($"- `{quality.InvestigationId:D}` — {quality.Summary}");
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "QUALITY.md"), text.ToString(), overwrite: true);
    }

    private static EventInvestigation ReadEventRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Investigação excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var investigation = JsonSerializer.Deserialize<EventInvestigation>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de investigação nulo.");
        ValidateInvestigation(investigation);
        return investigation;
    }

    private static void ValidateInvestigation(EventInvestigation investigation)
    {
        if (investigation is null) throw new InvalidDataException("Investigação nula.");
        var sources = investigation.Sources;
        var groups = investigation.Groups;
        var events = investigation.Events;
        var reports = investigation.Reports;
        if (investigation.FormatVersion != 1 || investigation.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(investigation.SoftwareVersion) ||
            string.IsNullOrWhiteSpace(investigation.SchemaVersion) ||
            !Enum.IsDefined(investigation.Window) || !Enum.IsDefined(investigation.Status) ||
            investigation.Status == EventInvestigationStatus.Running ||
            investigation.PeriodFromUtc.Offset != TimeSpan.Zero ||
            investigation.PeriodToUtc.Offset != TimeSpan.Zero ||
            investigation.PeriodFromUtc > investigation.PeriodToUtc ||
            investigation.StartedUtc > investigation.CompletedUtc || investigation.Duration < TimeSpan.Zero ||
            investigation.Channels is null || sources is null || groups is null || events is null ||
            reports is null || investigation.Limitations is null)
            throw new InvalidDataException("Registro de investigação inválido ou versão incompatível.");

        if (investigation.Channels.Select(channel => channel?.Channel)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != investigation.Channels.Count ||
            investigation.Channels.Any(channel => channel is null || string.IsNullOrWhiteSpace(channel.Channel)))
            throw new InvalidDataException("Canais da investigação inválidos.");

        if (sources.Count == 0 ||
            sources.Select(source => source?.CollectorId).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.CollectorId) ||
                string.IsNullOrWhiteSpace(source.CollectorVersion) || string.IsNullOrWhiteSpace(source.Source) ||
                !Enum.IsDefined(source.Status) || source.Status == EventSourceStatus.Running ||
                source.StartedUtc > source.CompletedUtc || source.Duration < TimeSpan.Zero ||
                source.EventsRead < 0 || source.ReportsRead < 0 || source.Limitations is null ||
                (source.Status == EventSourceStatus.Completed &&
                 (source.Limitations.Count > 0 || source.LimitHit))))
            throw new InvalidDataException("Fontes da investigação inválidas.");

        if (investigation.Status == EventInvestigationStatus.Completed &&
            (sources.Any(source => source.Status != EventSourceStatus.Completed) ||
             sources.Any(source => source.Limitations.Count > 0 || source.LimitHit) ||
             investigation.Limitations.Count > 0))
            throw new InvalidDataException("Investigação concluída não pode conter fontes incompletas ou limitações.");
        if (investigation.Status == EventInvestigationStatus.Failed &&
            sources.Any(source => source.Status != EventSourceStatus.Failed))
            throw new InvalidDataException("Investigação com falha deve ter todas as fontes com falha.");
        if (investigation.Status == EventInvestigationStatus.Unsupported &&
            sources.Any(source => source.Status != EventSourceStatus.Unsupported))
            throw new InvalidDataException("Investigação não suportada deve ter todas as fontes não suportadas.");

        if (events.Select(item => item?.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != events.Count ||
            events.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.GroupId) || !Enum.IsDefined(item.Level) ||
                item.OccurredUtc.Offset != TimeSpan.Zero || item.Data is null || item.Limitations is null))
            throw new InvalidDataException("Registros de evento da investigação inválidos.");
        if (reports.Select(item => item?.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != reports.Count ||
            reports.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.GroupId) || string.IsNullOrWhiteSpace(item.ReportId) ||
                string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Store) ||
                string.IsNullOrWhiteSpace(item.FolderName) || !Enum.IsDefined(item.Confidence) ||
                item.Data is null || item.Limitations is null))
            throw new InvalidDataException("Relatórios WER da investigação inválidos.");
        if (groups.Select(group => group?.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count ||
            groups.Any(group => group is null || string.IsNullOrWhiteSpace(group.Id) ||
                string.IsNullOrWhiteSpace(group.Title) || !Enum.IsDefined(group.Category) ||
                !Enum.IsDefined(group.Severity) || !Enum.IsDefined(group.Confidence) ||
                group.Causality != CausalityStatus.NotAssessed || group.EventRecordCount < 0 ||
                group.DistinctReportCount < 0 || group.RePresentationCount < 0 ||
                group.Channels is null || group.Sources is null || group.Limitations is null ||
                (group.FirstObservedUtc is { } first && group.LastObservedUtc is { } last && first > last)))
            throw new InvalidDataException("Grupos de evidência da investigação inválidos.");

        var usedGroups = events.Select(item => item.GroupId)
            .Concat(reports.Select(item => item.GroupId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (usedGroups.Count != groups.Count || groups.Any(group => !usedGroups.Contains(group.Id)))
            throw new InvalidDataException("Grupos de evidência não correspondem às referências preservadas.");

        foreach (var group in groups)
        {
            var groupEvents = events.Count(item =>
                string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase));
            var groupReports = reports.Where(item =>
                string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            var distinctReports = groupReports.Select(item => item.ReportId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (group.EventRecordCount != groupEvents ||
                group.DistinctReportCount != distinctReports ||
                group.RePresentationCount != groupReports.Length - distinctReports)
                throw new InvalidDataException("Contagens do grupo não correspondem às evidências preservadas.");
        }
    }

    private static string CanonicalJson(EventInvestigation investigation) =>
        JsonSerializer.Serialize(investigation, AuditJson.Options).Replace("\r\n", "\n");

    private List<HardwareSnapshot> ReconcileHardware(SqliteConnection database, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<HardwareSnapshot>();
        var seen = new HashSet<Guid>();
        var directory = Path.Combine(root, "memory", "inventory");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var snapshot = ReadHardwareRecord(path);
                    if (Path.GetFileName(path) != $"{snapshot.Id:D}.md" || !seen.Add(snapshot.Id))
                        throw new InvalidDataException("Identidade ou localização de snapshot inválida.");
                    snapshots.Add(snapshot);
                    RecordEnvelope.AtomicWrite(GetHardwareReportPath(snapshot.Id), HardwareReport.Render(snapshot), overwrite: true);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                    or UnauthorizedAccessException or ArgumentException)
                {
                    warnings.Add($"Inventário {Path.GetFileName(path)} não recuperado ({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM HardwareSnapshots";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seen.Contains(id))
                    warnings.Add($"Inventário indexado {reader.GetString(0)} sem registro Markdown válido; excluído da validade.");
        }
        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM HardwareItems; DELETE FROM HardwareCollectorResults; DELETE FROM HardwareSnapshots";
            clear.ExecuteNonQuery();
        }
        foreach (var snapshot in snapshots) IndexHardware(database, transaction, snapshot);
        transaction.Commit();
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return snapshots;
    }

    private void IndexHardware(SqliteConnection database, SqliteTransaction transaction, HardwareSnapshot snapshot)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO HardwareSnapshots VALUES ($id,$format,$schema,$software,$start,$end,$status,$path,$hash)";
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$format", snapshot.FormatVersion);
        command.Parameters.AddWithValue("$schema", snapshot.SchemaVersion);
        command.Parameters.AddWithValue("$software", snapshot.SoftwareVersion);
        command.Parameters.AddWithValue("$start", snapshot.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", snapshot.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$status", snapshot.Status.ToString());
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, HardwareRecordPath(snapshot.Id)));
        command.Parameters.AddWithValue("$hash", RecordEnvelope.Hash(CanonicalJson(snapshot)));
        command.ExecuteNonQuery();
        foreach (var result in snapshot.Results)
        {
            using var child = database.CreateCommand(); child.Transaction = transaction;
            child.CommandText = "INSERT INTO HardwareCollectorResults VALUES ($snapshot,$id,$version,$category,$status,$source,$start,$end,$duration,$limits)";
            child.Parameters.AddWithValue("$snapshot", snapshot.Id.ToString("D"));
            child.Parameters.AddWithValue("$id", result.CollectorId);
            child.Parameters.AddWithValue("$version", result.CollectorVersion);
            child.Parameters.AddWithValue("$category", result.Category.ToString());
            child.Parameters.AddWithValue("$status", result.Status.ToString());
            child.Parameters.AddWithValue("$source", result.Source);
            child.Parameters.AddWithValue("$start", result.StartedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$end", result.CompletedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$duration", result.Duration.TotalMilliseconds);
            child.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(result.Limitations, AuditJson.Options));
            child.ExecuteNonQuery();
            foreach (var item in result.Items)
            {
                using var row = database.CreateCommand(); row.Transaction = transaction;
                row.CommandText = "INSERT INTO HardwareItems VALUES ($snapshot,$category,$kind,$stable,$name,$manufacturer,$model,$hardware,$driver,$firmware,$state,$presence,$properties)";
                row.Parameters.AddWithValue("$snapshot", snapshot.Id.ToString("D"));
                row.Parameters.AddWithValue("$category", item.Category.ToString());
                row.Parameters.AddWithValue("$kind", item.Kind);
                row.Parameters.AddWithValue("$stable", item.StableId);
                row.Parameters.AddWithValue("$name", (object?)item.Name ?? DBNull.Value);
                row.Parameters.AddWithValue("$manufacturer", (object?)item.Manufacturer ?? DBNull.Value);
                row.Parameters.AddWithValue("$model", (object?)item.Model ?? DBNull.Value);
                row.Parameters.AddWithValue("$hardware", (object?)item.HardwareId ?? DBNull.Value);
                row.Parameters.AddWithValue("$driver", (object?)item.DriverVersion ?? DBNull.Value);
                row.Parameters.AddWithValue("$firmware", (object?)item.FirmwareVersion ?? DBNull.Value);
                row.Parameters.AddWithValue("$state", (object?)item.WindowsState ?? DBNull.Value);
                row.Parameters.AddWithValue("$presence", item.Presence.ToString());
                row.Parameters.AddWithValue("$properties", JsonSerializer.Serialize(item.Properties, AuditJson.Options));
                row.ExecuteNonQuery();
            }
        }
    }

    private static HardwareSnapshot ReadHardwareRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Inventário excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var snapshot = JsonSerializer.Deserialize<HardwareSnapshot>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de inventário nulo.");
        ValidateHardware(snapshot);
        return snapshot;
    }

    private static void ValidateHardware(HardwareSnapshot snapshot)
    {
        if (snapshot.FormatVersion != 1 || snapshot.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(snapshot.SoftwareVersion) || string.IsNullOrWhiteSpace(snapshot.SchemaVersion) ||
            !Enum.IsDefined(snapshot.Status) || snapshot.Status == HardwareCollectionStatus.Running ||
            snapshot.Results is null || snapshot.StartedUtc > snapshot.CompletedUtc ||
            snapshot.Duration < TimeSpan.Zero || snapshot.Results.Count == 0 ||
            snapshot.Results.Select(r => r?.CollectorId).Distinct().Count() != snapshot.Results.Count ||
            snapshot.Results.Any(r => r is null || string.IsNullOrWhiteSpace(r.CollectorId) ||
                string.IsNullOrWhiteSpace(r.CollectorVersion) || string.IsNullOrWhiteSpace(r.Source) ||
                !Enum.IsDefined(r.Category) || !Enum.IsDefined(r.Status) || r.Status == HardwareCollectionStatus.Running ||
                r.StartedUtc > r.CompletedUtc || r.Duration < TimeSpan.Zero || r.Items is null || r.Limitations is null ||
                (r.Status == HardwareCollectionStatus.Completed && r.Limitations.Count > 0) ||
                r.Items.Select(i => (i.Category, i.Kind, i.StableId)).Distinct().Count() != r.Items.Count ||
                r.Items.Any(i => i is null || i.Category != r.Category || string.IsNullOrWhiteSpace(i.Kind) ||
                    string.IsNullOrWhiteSpace(i.StableId) || i.Properties is null || i.Limitations is null)) ||
            (snapshot.Status == HardwareCollectionStatus.Completed &&
             snapshot.Results.Any(r => r.Status != HardwareCollectionStatus.Completed)))
            throw new InvalidDataException("Registro de inventário de hardware inválido ou versão incompatível.");
    }

    private static string CanonicalJson(HardwareSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, AuditJson.Options).Replace("\r\n", "\n");

    private static AuditRecord ReadRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes) throw new InvalidDataException("Registro excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var audit = JsonSerializer.Deserialize<AuditRecord>(json, AuditJson.Options) ?? throw new InvalidDataException("Payload nulo.");
        Validate(audit); return audit;
    }

    private static void Validate(AuditRecord audit)
    {
        if (audit.FormatVersion != 1 || audit.Id == Guid.Empty || string.IsNullOrWhiteSpace(audit.SoftwareVersion) ||
            !Enum.IsDefined(audit.Kind) || !Enum.IsDefined(audit.Status) ||
            audit.Status is DiagnosticStatus.Pending or DiagnosticStatus.Running || audit.Results is null ||
            audit.StartedUtc > audit.CompletedUtc || audit.Duration < TimeSpan.Zero || audit.Results.Count == 0 ||
            audit.Results.Select(r => r?.CollectorId).Distinct().Count() != audit.Results.Count ||
            audit.Results.Any(r => r is null || r.AuditId != audit.Id || string.IsNullOrWhiteSpace(r.CollectorId) ||
                string.IsNullOrWhiteSpace(r.CollectorVersion) || string.IsNullOrWhiteSpace(r.Source) ||
                r.Limitations is null || !Enum.IsDefined(r.Status) || r.Status is DiagnosticStatus.Pending or DiagnosticStatus.Running ||
                r.StartedUtc > r.CompletedUtc || r.Duration < TimeSpan.Zero ||
                (r.Status == DiagnosticStatus.Completed && (r.Data is null || r.Limitations.Count > 0))) ||
            (audit.Status == DiagnosticStatus.Completed && audit.Results.Any(r => r.Status != DiagnosticStatus.Completed)))
            throw new InvalidDataException("Registro de auditoria inválido ou versão incompatível.");
    }

    private static string CanonicalJson(AuditRecord audit) => JsonSerializer.Serialize(audit, AuditJson.Options).Replace("\r\n", "\n");
}
