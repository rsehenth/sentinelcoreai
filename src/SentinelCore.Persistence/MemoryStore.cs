using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed class MemoryStore : IAuditStore, IHardwareSnapshotStore, IEventInvestigationStore,
    IPerformanceSessionStore, INetworkSessionStore, IGraphicsInvestigationStore, ICorrelationInvestigationStore,
    IMonitoringSessionStore
{
    private readonly string root;
    private string Database => Path.Combine(root, "data", "sentinelcore.db");
    private string HardwareRecordPath(Guid id) => Path.Combine(root, "memory", "inventory", $"{id:D}.md");
    private string EventRecordPath(Guid id) => Path.Combine(root, "memory", "events", $"{id:D}.md");
    private string PerformanceRecordPath(Guid id) => Path.Combine(root, "memory", "performance", $"{id:D}.md");
    private string NetworkRecordPath(Guid id) => Path.Combine(root, "memory", "network", $"{id:D}.md");
    private string GraphicsRecordPath(Guid id) => Path.Combine(root, "memory", "graphics", $"{id:D}.md");
    private string CorrelationRecordPath(Guid id) => Path.Combine(root, "memory", "correlation", $"{id:D}.md");
    private string MonitoringRecordPath(Guid id) => Path.Combine(root, "memory", "monitoring", $"{id:D}.md");
    private string MonitoringSegmentDirectory(Guid sessionId) =>
        Path.Combine(root, "memory", "monitoring", $"{sessionId:D}", "segments");
    private string MonitoringSegmentPath(Guid sessionId, Guid segmentId) =>
        Path.Combine(MonitoringSegmentDirectory(sessionId), $"{segmentId:D}.md");
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCoreAI");
    public MemoryStore(string? dataRoot = null) => root = Path.GetFullPath(dataRoot ?? DefaultRoot);
    public string GetReportPath(Guid auditId) => Path.Combine(root, "reports", $"{auditId:D}.md");
    public string GetHardwareReportPath(Guid snapshotId) => Path.Combine(root, "reports", "hardware", $"{snapshotId:D}.md");
    public string GetEventReportPath(Guid investigationId) => Path.Combine(root, "reports", "events", $"{investigationId:D}.md");
    public string GetPerformanceReportPath(Guid sessionId) => Path.Combine(root, "reports", "performance", $"{sessionId:D}.md");
    public string GetNetworkReportPath(Guid sessionId) => Path.Combine(root, "reports", "network", $"{sessionId:D}.md");
    public string GetGraphicsReportPath(Guid investigationId) => Path.Combine(root, "reports", "graphics", $"{investigationId:D}.md");
    public string GetCorrelationReportPath(Guid investigationId) => Path.Combine(root, "reports", "correlation", $"{investigationId:D}.md");
    public string GetMonitoringReportPath(Guid sessionId) => Path.Combine(root, "reports", "monitoring", $"{sessionId:D}.md");
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
        EnsureNoMonitoringRecord(audit.Id);
        EnsureDirectories();
        using var storeLock = Lock();
        var otherKind = audit.Kind == AuditKind.Basic ? AuditKind.Full : AuditKind.Basic;
        if (File.Exists(RecordPath(audit with { Kind = otherKind })) ||
            File.Exists(HardwareRecordPath(audit.Id)) || File.Exists(EventRecordPath(audit.Id)) ||
            File.Exists(PerformanceRecordPath(audit.Id)) || File.Exists(NetworkRecordPath(audit.Id)) ||
            File.Exists(GraphicsRecordPath(audit.Id)) || File.Exists(CorrelationRecordPath(audit.Id)))
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
        foreach (var relative in new[] { "memory", "memory/inventory", "memory/events", "memory/performance", "memory/network", "memory/graphics", "memory/correlation", "memory/monitoring", "data", "reports", "reports/hardware", "reports/events", "reports/performance", "reports/network", "reports/graphics", "reports/correlation", "reports/monitoring", "logs", Path.Combine("memory", "audits", "basic") })
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
        if (schema > 9)
            throw new InvalidDataException("Banco de uma versão mais recente. Abra com uma versão compatível do aplicativo.");
        if (schema == 9) return;
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
        if (schema < 7)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS GraphicsInvestigations (
                    Id TEXT PRIMARY KEY, FormatVersion INTEGER NOT NULL, SchemaVersion TEXT NOT NULL,
                    SoftwareVersion TEXT NOT NULL, WindowPreset TEXT NOT NULL,
                    PeriodFromUtc TEXT NOT NULL, PeriodToUtc TEXT NOT NULL,
                    StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL, DurationSeconds REAL NOT NULL,
                    Status TEXT NOT NULL, ExecutionStatus TEXT NULL, CoverageStatus TEXT NULL,
                    HardwareReadingId TEXT NULL, EventReadingId TEXT NULL, PreviousInvestigationId TEXT NULL,
                    AdapterCount INTEGER NOT NULL, MonitorCount INTEGER NOT NULL, DriverCount INTEGER NOT NULL,
                    EventCount INTEGER NOT NULL, WerReportCount INTEGER NOT NULL, ChangeCount INTEGER NOT NULL,
                    ChangesComparable INTEGER NOT NULL, MarkdownPath TEXT NOT NULL UNIQUE, PayloadHash TEXT NOT NULL,
                    CoverageJson TEXT NOT NULL, AdaptersJson TEXT NOT NULL, MonitorsJson TEXT NOT NULL,
                    DriversJson TEXT NOT NULL, EventsJson TEXT NOT NULL, WerJson TEXT NOT NULL,
                    ObservationsJson TEXT NOT NULL, NotesJson TEXT NOT NULL, ChangesJson TEXT NOT NULL,
                    LimitationsJson TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS GraphicsSources (
                    InvestigationId TEXT NOT NULL REFERENCES GraphicsInvestigations(Id) ON DELETE CASCADE,
                    CollectorId TEXT NOT NULL, CollectorVersion TEXT NOT NULL, Category TEXT NOT NULL,
                    Source TEXT NOT NULL, Status TEXT NOT NULL, StartedUtc TEXT NOT NULL,
                    CompletedUtc TEXT NOT NULL, DurationMs REAL NOT NULL, RecordsRead INTEGER NOT NULL,
                    EvidenceCount INTEGER NOT NULL, LimitHit INTEGER NOT NULL, LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (InvestigationId, CollectorId));
                CREATE TABLE IF NOT EXISTS GraphicsObservations (
                    InvestigationId TEXT NOT NULL REFERENCES GraphicsInvestigations(Id) ON DELETE CASCADE,
                    ObservationId TEXT NOT NULL, RuleId TEXT NOT NULL, Kind TEXT NOT NULL,
                    Severity TEXT NOT NULL, Confidence TEXT NOT NULL, Causality TEXT NOT NULL,
                    Metric TEXT NOT NULL, ObservedValue REAL NULL,
                    WindowFromUtc TEXT NOT NULL, WindowToUtc TEXT NOT NULL, EvidenceCount INTEGER NOT NULL,
                    PRIMARY KEY (InvestigationId, ObservationId));
                CREATE INDEX IF NOT EXISTS IX_GraphicsInvestigations_Completed ON GraphicsInvestigations(CompletedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_GraphicsObservations_Rule ON GraphicsObservations(InvestigationId, RuleId);
                """;
            command.ExecuteNonQuery();
        }
        if (schema < 8)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS CorrelationInvestigations (
                    Id TEXT PRIMARY KEY, FormatVersion INTEGER NOT NULL, SchemaVersion TEXT NOT NULL,
                    SoftwareVersion TEXT NOT NULL, WindowPreset TEXT NOT NULL,
                    PeriodFromUtc TEXT NOT NULL, PeriodToUtc TEXT NOT NULL,
                    StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL, DurationSeconds REAL NOT NULL,
                    Status TEXT NOT NULL, ExecutionStatus TEXT NOT NULL, CoverageStatus TEXT NOT NULL,
                    RequestedSources INTEGER NOT NULL, LastBootUtc TEXT NULL, BootAuditId TEXT NULL,
                    EvidenceCount INTEGER NOT NULL, TimelineCount INTEGER NOT NULL, RelationCount INTEGER NOT NULL,
                    UndatedCount INTEGER NOT NULL, MergedReferenceCount INTEGER NOT NULL, LimitHit INTEGER NOT NULL,
                    MarkdownPath TEXT NOT NULL UNIQUE, PayloadHash TEXT NOT NULL,
                    SourcesJson TEXT NOT NULL, BootJson TEXT NOT NULL, CoverageJson TEXT NOT NULL,
                    NotesJson TEXT NOT NULL, LimitationsJson TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS CorrelationSources (
                    InvestigationId TEXT NOT NULL REFERENCES CorrelationInvestigations(Id) ON DELETE CASCADE,
                    Source TEXT NOT NULL, Status TEXT NOT NULL, RecordsRead INTEGER NOT NULL,
                    RecordsUsed INTEGER NOT NULL, EvidenceCount INTEGER NOT NULL, LimitHit INTEGER NOT NULL,
                    Note TEXT NULL, LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (InvestigationId, Source));
                CREATE TABLE IF NOT EXISTS CorrelationEvidence (
                    InvestigationId TEXT NOT NULL REFERENCES CorrelationInvestigations(Id) ON DELETE CASCADE,
                    EvidenceId TEXT NOT NULL, Source TEXT NOT NULL, Kind TEXT NOT NULL,
                    SourceRecordId TEXT NOT NULL, EffectiveTimestampUtc TEXT NULL, Precision TEXT NOT NULL,
                    IdentityKey TEXT NULL, IdentityKind TEXT NOT NULL, Severity TEXT NOT NULL,
                    Confidence TEXT NOT NULL, Title TEXT NOT NULL, OriginsJson TEXT NOT NULL,
                    LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (InvestigationId, EvidenceId));
                CREATE TABLE IF NOT EXISTS CorrelationRelations (
                    InvestigationId TEXT NOT NULL REFERENCES CorrelationInvestigations(Id) ON DELETE CASCADE,
                    RelationId TEXT NOT NULL, RuleId TEXT NOT NULL, Kind TEXT NOT NULL,
                    Severity TEXT NOT NULL, Confidence TEXT NOT NULL, Causality TEXT NOT NULL,
                    WindowFromUtc TEXT NULL, WindowToUtc TEXT NULL, MaximumGapSeconds REAL NULL,
                    EvidenceIdsJson TEXT NOT NULL, LimitationsJson TEXT NOT NULL,
                    PRIMARY KEY (InvestigationId, RelationId));
                CREATE INDEX IF NOT EXISTS IX_CorrelationInvestigations_Completed ON CorrelationInvestigations(CompletedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_CorrelationRelations_Rule ON CorrelationRelations(InvestigationId, RuleId);
                CREATE INDEX IF NOT EXISTS IX_CorrelationEvidence_Source ON CorrelationEvidence(InvestigationId, Source);
                """;
            command.ExecuteNonQuery();
        }
        if (schema < 9)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS MonitoringSessions (
                    Id TEXT PRIMARY KEY, FormatVersion INTEGER NOT NULL, SchemaVersion TEXT NOT NULL,
                    SoftwareVersion TEXT NOT NULL, Preset TEXT NOT NULL,
                    AuthorizedDurationSeconds REAL NOT NULL,
                    StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL, DurationSeconds REAL NOT NULL,
                    Status TEXT NOT NULL, CoverageStatus TEXT NOT NULL, RequestedSources INTEGER NOT NULL,
                    SegmentCount INTEGER NOT NULL, IncidentCount INTEGER NOT NULL, Recovered INTEGER NOT NULL,
                    BudgetJson TEXT NOT NULL, SourcesJson TEXT NOT NULL, RecoveryJson TEXT NOT NULL,
                    LimitationsJson TEXT NOT NULL, MarkdownPath TEXT NOT NULL UNIQUE, PayloadHash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS MonitoringSegments (
                    SessionId TEXT NOT NULL REFERENCES MonitoringSessions(Id) ON DELETE CASCADE,
                    SegmentId TEXT PRIMARY KEY, Sequence INTEGER NOT NULL, Source TEXT NOT NULL,
                    CollectorId TEXT NOT NULL, CollectorVersion TEXT NOT NULL,
                    FromUtc TEXT NOT NULL, ToUtc TEXT NOT NULL, Status TEXT NOT NULL,
                    ObservationCount INTEGER NOT NULL,
                    MarkdownPath TEXT NOT NULL UNIQUE, PayloadHash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS MonitoringIncidents (
                    SessionId TEXT NOT NULL REFERENCES MonitoringSessions(Id) ON DELETE CASCADE,
                    IncidentId TEXT PRIMARY KEY, RecordedUtc TEXT NOT NULL, OccurredUtc TEXT NULL,
                    Precision TEXT NOT NULL, Category TEXT NOT NULL, Description TEXT NULL,
                    SessionElapsedSeconds REAL NOT NULL, EvidenceJson TEXT NOT NULL,
                    LimitationsJson TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS MonitoringRecoveries (
                    SessionId TEXT PRIMARY KEY, DetectedUtc TEXT NOT NULL,
                    FirstSegmentUtc TEXT NOT NULL, LastSegmentUtc TEXT NOT NULL,
                    SegmentCount INTEGER NOT NULL, PerformanceObservations INTEGER NOT NULL,
                    EventObservations INTEGER NOT NULL, NetworkObservations INTEGER NOT NULL,
                    Detail TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_MonitoringSessions_Completed ON MonitoringSessions(CompletedUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_MonitoringSegments_Session ON MonitoringSegments(SessionId, Source, Sequence);
                CREATE INDEX IF NOT EXISTS IX_MonitoringIncidents_Session ON MonitoringIncidents(SessionId, RecordedUtc);
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version = 9";
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
        EnsureNoMonitoringRecord(snapshot.Id);
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
        if (File.Exists(GraphicsRecordPath(snapshot.Id)))
            throw new InvalidDataException("ID já existe em uma investigação gráfica; histórico preservado.");
        if (File.Exists(CorrelationRecordPath(snapshot.Id)))
            throw new InvalidDataException("ID já existe em uma correlação; histórico preservado.");
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
        EnsureNoMonitoringRecord(investigation.Id);
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
        if (File.Exists(GraphicsRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma investigação gráfica; histórico preservado.");
        if (File.Exists(CorrelationRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma correlação; histórico preservado.");
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
        EnsureNoMonitoringRecord(session.Id);
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
        if (File.Exists(GraphicsRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma investigação gráfica; histórico preservado.");
        if (File.Exists(CorrelationRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma correlação; histórico preservado.");
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
        EnsureNoMonitoringRecord(session.Id);
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
        if (File.Exists(GraphicsRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma investigação gráfica; histórico preservado.");
        if (File.Exists(CorrelationRecordPath(session.Id)))
            throw new InvalidDataException("ID já existe em uma correlação; histórico preservado.");
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

    public Task<GraphicsStoreSnapshot> LoadGraphicsInvestigationsAsync(
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var investigations = ReconcileGraphics(database, warnings, cancellationToken);
        WriteGraphicsProjection(investigations);
        var byId = investigations.ToDictionary(investigation => investigation.Id);
        var ordered = new List<GraphicsInvestigation>();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Id FROM GraphicsInvestigations ORDER BY CompletedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (Guid.TryParse(reader.GetString(0), out var id) && byId.TryGetValue(id, out var investigation))
                ordered.Add(investigation);
        return new GraphicsStoreSnapshot(ordered, warnings);
    }, cancellationToken);

    public Task<long> SaveGraphicsInvestigationAsync(GraphicsInvestigation investigation,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateGraphicsInvestigation(investigation);
        EnsureNoMonitoringRecord(investigation.Id);
        EnsureDirectories();
        using var storeLock = Lock();
        foreach (var kind in Enum.GetValues<AuditKind>())
            if (File.Exists(Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant(),
                    $"{investigation.Id:D}.md")))
                throw new InvalidDataException("ID já existe em uma auditoria; histórico preservado.");
        if (File.Exists(HardwareRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em um inventário; histórico preservado.");
        if (File.Exists(EventRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma investigação; histórico preservado.");
        if (File.Exists(PerformanceRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de desempenho; histórico preservado.");
        if (File.Exists(NetworkRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de rede; histórico preservado.");
        if (File.Exists(CorrelationRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma correlação; histórico preservado.");
        var json = CanonicalJson(investigation);
        var path = GraphicsRecordPath(investigation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long bytes;
        if (File.Exists(path))
        {
            var (existing, existingHash) = ReadGraphicsRecord(path);
            if (existingHash != RecordEnvelope.Hash(json) && CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
            bytes = new FileInfo(path).Length;
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = GraphicsReport.Render(investigation);
            var document = RecordEnvelope.Wrap(report, json);
            bytes = Encoding.UTF8.GetByteCount(document);
            if (bytes > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException(
                    "Investigação gráfica excede 4 MiB e não pode ser persistida neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
            RecordEnvelope.AtomicWrite(GetGraphicsReportPath(investigation.Id), report, overwrite: false);
        }
        using var database = OpenDatabase([]);
        var investigations = ReconcileGraphics(database, [], CancellationToken.None);
        WriteGraphicsProjection(investigations);
        if (!investigations.Any(item => item.Id == investigation.Id) ||
            !File.Exists(GetGraphicsReportPath(investigation.Id)))
            throw new IOException("A gravação da investigação gráfica não foi concluída. O registro durável, " +
                "se existente, será recuperado na próxima tentativa.");
        return bytes;
    }, cancellationToken);

    private List<GraphicsInvestigation> ReconcileGraphics(SqliteConnection database, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var investigations = new List<GraphicsInvestigation>();
        var hashes = new Dictionary<Guid, string>();
        var seen = new HashSet<Guid>();
        var directory = Path.Combine(root, "memory", "graphics");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (investigation, payloadHash) = ReadGraphicsRecord(path);
                    if (Path.GetFileName(path) != $"{investigation.Id:D}.md" || !seen.Add(investigation.Id))
                        throw new InvalidDataException("Identidade ou localização de investigação gráfica inválida.");
                    investigations.Add(investigation);
                    hashes[investigation.Id] = payloadHash;
                    RecordEnvelope.AtomicWrite(GetGraphicsReportPath(investigation.Id),
                        GraphicsReport.Render(investigation), overwrite: true);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                    or UnauthorizedAccessException or ArgumentException or NullReferenceException)
                {
                    warnings.Add($"Investigação gráfica {Path.GetFileName(path)} não recuperada " +
                        $"({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM GraphicsInvestigations";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seen.Contains(id))
                    warnings.Add($"Investigação gráfica indexada {reader.GetString(0)} sem registro Markdown válido; " +
                        "excluída da validade.");
        }
        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText =
                "DELETE FROM GraphicsObservations; DELETE FROM GraphicsSources; DELETE FROM GraphicsInvestigations";
            clear.ExecuteNonQuery();
        }
        foreach (var investigation in investigations)
            IndexGraphics(database, transaction, investigation, hashes.GetValueOrDefault(investigation.Id));
        transaction.Commit();
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return investigations;
    }

    private void IndexGraphics(SqliteConnection database, SqliteTransaction transaction,
        GraphicsInvestigation investigation, string? payloadHash = null)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO GraphicsInvestigations
                (Id, FormatVersion, SchemaVersion, SoftwareVersion, WindowPreset, PeriodFromUtc, PeriodToUtc,
                 StartedUtc, CompletedUtc, DurationSeconds, Status, ExecutionStatus, CoverageStatus,
                 HardwareReadingId, EventReadingId, PreviousInvestigationId, AdapterCount, MonitorCount,
                 DriverCount, EventCount, WerReportCount, ChangeCount, ChangesComparable, MarkdownPath,
                 PayloadHash, CoverageJson, AdaptersJson, MonitorsJson, DriversJson, EventsJson, WerJson,
                 ObservationsJson, NotesJson, ChangesJson, LimitationsJson)
            VALUES ($id,$format,$schema,$software,$window,$from,$to,$start,$end,$duration,$status,$execution,
                 $coverage,$hardware,$events,$previous,$adapters,$monitors,$drivers,$eventCount,$werCount,
                 $changeCount,$comparable,$path,$hash,$coverageJson,$adaptersJson,$monitorsJson,$driversJson,
                 $eventsJson,$werJson,$observationsJson,$notesJson,$changesJson,$limits)
            """;
        command.Parameters.AddWithValue("$id", investigation.Id.ToString("D"));
        command.Parameters.AddWithValue("$format", investigation.FormatVersion);
        command.Parameters.AddWithValue("$schema", investigation.SchemaVersion);
        command.Parameters.AddWithValue("$software", investigation.SoftwareVersion);
        command.Parameters.AddWithValue("$window", investigation.Window.ToString());
        command.Parameters.AddWithValue("$from", investigation.PeriodFromUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", investigation.PeriodToUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$start", investigation.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", investigation.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$duration", investigation.Duration.TotalSeconds);
        command.Parameters.AddWithValue("$status", investigation.Status.ToString());
        command.Parameters.AddWithValue("$execution", investigation.Coverage.Execution.ToString());
        command.Parameters.AddWithValue("$coverage", investigation.Coverage.Status.ToString());
        command.Parameters.AddWithValue("$hardware", investigation.HardwareReadingId.ToString("D"));
        command.Parameters.AddWithValue("$events",
            (object?)investigation.EventReadingId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous",
            (object?)investigation.PreviousInvestigationId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$adapters", investigation.Adapters.Count);
        command.Parameters.AddWithValue("$monitors", investigation.Monitors.Count);
        command.Parameters.AddWithValue("$drivers", investigation.Drivers.Count);
        command.Parameters.AddWithValue("$eventCount", investigation.Events.Count);
        command.Parameters.AddWithValue("$werCount", investigation.WerReports.Count);
        command.Parameters.AddWithValue("$changeCount", investigation.Changes.Count);
        command.Parameters.AddWithValue("$comparable", investigation.ChangesComparable ? 1 : 0);
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, GraphicsRecordPath(investigation.Id)));
        command.Parameters.AddWithValue("$hash", payloadHash ?? RecordEnvelope.Hash(CanonicalJson(investigation)));
        command.Parameters.AddWithValue("$coverageJson",
            JsonSerializer.Serialize(investigation.Coverage, AuditJson.Options));
        command.Parameters.AddWithValue("$adaptersJson",
            JsonSerializer.Serialize(investigation.Adapters, AuditJson.Options));
        command.Parameters.AddWithValue("$monitorsJson",
            JsonSerializer.Serialize(investigation.Monitors, AuditJson.Options));
        command.Parameters.AddWithValue("$driversJson",
            JsonSerializer.Serialize(investigation.Drivers, AuditJson.Options));
        command.Parameters.AddWithValue("$eventsJson",
            JsonSerializer.Serialize(investigation.Events, AuditJson.Options));
        command.Parameters.AddWithValue("$werJson",
            JsonSerializer.Serialize(investigation.WerReports, AuditJson.Options));
        command.Parameters.AddWithValue("$observationsJson",
            JsonSerializer.Serialize(investigation.Observations, AuditJson.Options));
        command.Parameters.AddWithValue("$notesJson",
            JsonSerializer.Serialize(investigation.AnalysisNotes, AuditJson.Options));
        command.Parameters.AddWithValue("$changesJson",
            JsonSerializer.Serialize(investigation.Changes, AuditJson.Options));
        command.Parameters.AddWithValue("$limits",
            JsonSerializer.Serialize(investigation.Limitations, AuditJson.Options));
        command.ExecuteNonQuery();
        foreach (var source in investigation.Sources)
        {
            using var child = database.CreateCommand(); child.Transaction = transaction;
            child.CommandText = """
                INSERT INTO GraphicsSources
                    (InvestigationId, CollectorId, CollectorVersion, Category, Source, Status, StartedUtc,
                     CompletedUtc, DurationMs, RecordsRead, EvidenceCount, LimitHit, LimitationsJson)
                VALUES ($investigation,$id,$version,$category,$source,$status,$start,$end,$duration,$records,
                     $evidence,$limit,$limits)
                """;
            child.Parameters.AddWithValue("$investigation", investigation.Id.ToString("D"));
            child.Parameters.AddWithValue("$id", source.CollectorId);
            child.Parameters.AddWithValue("$version", source.CollectorVersion);
            child.Parameters.AddWithValue("$category", source.Category.ToString());
            child.Parameters.AddWithValue("$source", source.Source);
            child.Parameters.AddWithValue("$status", source.Status.ToString());
            child.Parameters.AddWithValue("$start", source.StartedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$end", source.CompletedUtc.ToUniversalTime().ToString("O"));
            child.Parameters.AddWithValue("$duration", source.Duration.TotalMilliseconds);
            child.Parameters.AddWithValue("$records", source.RecordsRead);
            child.Parameters.AddWithValue("$evidence", source.EvidenceCount);
            child.Parameters.AddWithValue("$limit", source.LimitHit ? 1 : 0);
            child.Parameters.AddWithValue("$limits", JsonSerializer.Serialize(source.Limitations, AuditJson.Options));
            child.ExecuteNonQuery();
        }
        foreach (var observation in investigation.Observations)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = """
                INSERT INTO GraphicsObservations
                    (InvestigationId, ObservationId, RuleId, Kind, Severity, Confidence, Causality, Metric,
                     ObservedValue, WindowFromUtc, WindowToUtc, EvidenceCount)
                VALUES ($investigation,$id,$rule,$kind,$severity,$confidence,$causality,$metric,$value,$from,$to,
                     $evidence)
                """;
            row.Parameters.AddWithValue("$investigation", investigation.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", observation.Id);
            row.Parameters.AddWithValue("$rule", observation.RuleId);
            row.Parameters.AddWithValue("$kind", observation.Kind.ToString());
            row.Parameters.AddWithValue("$severity", observation.Severity.ToString());
            row.Parameters.AddWithValue("$confidence", observation.Confidence.ToString());
            row.Parameters.AddWithValue("$causality", observation.Causality.ToString());
            row.Parameters.AddWithValue("$metric", observation.Metric);
            row.Parameters.AddWithValue("$value", (object?)observation.ObservedValue ?? DBNull.Value);
            row.Parameters.AddWithValue("$from", observation.WindowFromUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$to", observation.WindowToUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$evidence", observation.EvidenceCount);
            row.ExecuteNonQuery();
        }
    }

    private void WriteGraphicsProjection(IReadOnlyList<GraphicsInvestigation> investigations)
    {
        var latest = investigations.OrderByDescending(item => item.CompletedUtc).ToArray();
        var lastCompleted = latest.FirstOrDefault(item => item.Coverage.Status == GraphicsCoverageStatus.Complete);
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Investigações gráficas");
        text.AppendLine();
        text.AppendLine("Formato: 1");
        text.AppendLine();
        text.AppendLine("Projeção derivada dos registros Markdown imutáveis; não é fonte de verdade.");
        text.AppendLine("Diagnóstico gráfico não renova a validade de auditorias, inventários, eventos ou sessões.");
        text.AppendLine();
        text.AppendLine("## Última investigação com cobertura completa");
        text.AppendLine();
        text.AppendLine(lastCompleted is null ? "Nenhuma investigação com cobertura completa."
            : $"{lastCompleted.Id:D} — {lastCompleted.CompletedUtc:O} — {lastCompleted.Adapters.Count} adaptador(es) — " +
              $"{lastCompleted.Events.Count} evento(s) — {lastCompleted.Observations.Count} observação(ões)");
        text.AppendLine();
        text.AppendLine("## Histórico");
        text.AppendLine();
        if (latest.Length == 0) text.AppendLine("Nenhuma investigação anterior.");
        else
            foreach (var investigation in latest)
                text.AppendLine($"- [{investigation.Id:D}](graphics/{investigation.Id:D}.md) — " +
                    $"{investigation.CompletedUtc:O} — situação {GraphicsStatusName(investigation.Status)} — " +
                    $"cobertura {GraphicsCoverageName(investigation.Coverage.Status)} — " +
                    $"{investigation.Adapters.Count} adaptador(es) — {investigation.Monitors.Count} monitor(es) — " +
                    $"{investigation.Events.Count} evento(s) — {investigation.WerReports.Count} relatório(s) WER — " +
                    $"{investigation.Observations.Count} observação(ões)");
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "GRAPHICS.md"), text.ToString(), overwrite: true);
    }

    private static string GraphicsStatusName(GraphicsInvestigationStatus status) => status switch
    {
        GraphicsInvestigationStatus.Completed => "concluída",
        GraphicsInvestigationStatus.Partial => "parcial",
        GraphicsInvestigationStatus.Cancelled => "cancelada",
        GraphicsInvestigationStatus.Failed => "falhou",
        GraphicsInvestigationStatus.Unsupported => "não suportada",
        GraphicsInvestigationStatus.Running => "em execução",
        _ => status.ToString()
    };

    private static string GraphicsCoverageName(GraphicsCoverageStatus status) => status switch
    {
        GraphicsCoverageStatus.Complete => "completa",
        GraphicsCoverageStatus.Partial => "parcial",
        GraphicsCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    };

    private static (GraphicsInvestigation Investigation, string PayloadHash) ReadGraphicsRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Investigação gráfica excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var investigation = JsonSerializer.Deserialize<GraphicsInvestigation>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de investigação gráfica nulo.");
        ValidateGraphicsInvestigation(investigation);
        return (investigation, hash);
    }

    private static void ValidateGraphicsInvestigation(GraphicsInvestigation investigation)
    {
        if (investigation is null) throw new InvalidDataException("Investigação gráfica nula.");
        var sources = investigation.Sources;
        var adapters = investigation.Adapters;
        var monitors = investigation.Monitors;
        var drivers = investigation.Drivers;
        var events = investigation.Events;
        var werReports = investigation.WerReports;
        var observations = investigation.Observations;
        var notes = investigation.AnalysisNotes;
        var changes = investigation.Changes;

        if (investigation.FormatVersion != 1 || investigation.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(investigation.SoftwareVersion) ||
            string.IsNullOrWhiteSpace(investigation.SchemaVersion) ||
            !Enum.IsDefined(investigation.Window) || !Enum.IsDefined(investigation.Status) ||
            investigation.Status == GraphicsInvestigationStatus.Running ||
            investigation.PeriodFromUtc.Offset != TimeSpan.Zero ||
            investigation.PeriodToUtc.Offset != TimeSpan.Zero ||
            investigation.StartedUtc.Offset != TimeSpan.Zero ||
            investigation.CompletedUtc.Offset != TimeSpan.Zero ||
            investigation.PeriodFromUtc >= investigation.PeriodToUtc ||
            investigation.StartedUtc > investigation.CompletedUtc || investigation.Duration < TimeSpan.Zero ||
            sources is null || adapters is null || monitors is null || drivers is null || events is null ||
            werReports is null || observations is null || notes is null || changes is null ||
            investigation.ComparisonLimitations is null || investigation.Limitations is null ||
            investigation.Coverage is null)
            throw new InvalidDataException("Registro de investigação gráfica inválido ou versão incompatível.");

        var zeroSourcesAllowed = investigation.Status == GraphicsInvestigationStatus.Cancelled &&
            investigation.Coverage.Execution == GraphicsExecutionStatus.Cancelled;
        if (sources.Count == 0 && !zeroSourcesAllowed ||
            sources.Select(source => source?.CollectorId).Distinct(StringComparer.Ordinal).Count() != sources.Count ||
            sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.CollectorId) ||
                string.IsNullOrWhiteSpace(source.CollectorVersion) || string.IsNullOrWhiteSpace(source.Source) ||
                !Enum.IsDefined(source.Category) || !Enum.IsDefined(source.Status) ||
                source.Status == GraphicsSourceStatus.Running || source.StartedUtc > source.CompletedUtc ||
                source.Duration < TimeSpan.Zero || source.RecordsRead < 0 || source.EvidenceCount < 0 ||
                source.Limitations is null))
            throw new InvalidDataException("Fontes da investigação gráfica inválidas.");

        var coverage = investigation.Coverage;
        bool SourceCategoryAvailable(GraphicsSourceCategory category) => sources.Any(source =>
            source.Category == category && source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial);
        var completedSourceCount = sources.Count(source => source.Status == GraphicsSourceStatus.Completed);
        var unavailableSourceIds = sources.Where(source => source.Status is GraphicsSourceStatus.Failed
                or GraphicsSourceStatus.Unsupported)
            .Select(source => source.CollectorId).ToHashSet(StringComparer.Ordinal);
        if (coverage.UnavailableSources is null ||
            coverage.CompletedSources != completedSourceCount ||
            !coverage.UnavailableSources.ToHashSet(StringComparer.Ordinal).SetEquals(unavailableSourceIds) ||
            investigation.Status != GraphicsInvestigationStatus.Cancelled &&
            coverage.ExpectedSources != sources.Count)
            throw new InvalidDataException("Contadores ou fontes indisponíveis da cobertura gráfica inconsistentes.");
        if (!Enum.IsDefined(coverage.Execution) || coverage.Execution == GraphicsExecutionStatus.Running ||
            !Enum.IsDefined(coverage.Status) || coverage.ExpectedSources <= 0 ||
            coverage.CompletedSources < 0 || coverage.CompletedSources > coverage.ExpectedSources ||
            coverage.UnavailableSources is null || coverage.Limitations is null ||
            coverage.UnavailableSources.Distinct(StringComparer.Ordinal).Count() != coverage.UnavailableSources.Count ||
            coverage.UnavailableSources.Any(string.IsNullOrWhiteSpace) ||
            coverage.AdaptersUnavailable != !SourceCategoryAvailable(GraphicsSourceCategory.Adapters) ||
            coverage.MonitorsUnavailable != !SourceCategoryAvailable(GraphicsSourceCategory.Monitors) ||
            coverage.DriversUnavailable != !SourceCategoryAvailable(GraphicsSourceCategory.Drivers) ||
            coverage.EventsUnavailable != !sources.Any(source =>
                source.Category is GraphicsSourceCategory.Events or GraphicsSourceCategory.Wer &&
                source.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial) ||
            coverage.Status == GraphicsCoverageStatus.Complete &&
            (coverage.AdaptersUnavailable || coverage.MonitorsUnavailable || coverage.DriversUnavailable ||
             coverage.EventsUnavailable || sources.Any(source => source.LimitHit)) ||
            coverage.Execution == GraphicsExecutionStatus.Cancelled &&
            investigation.Status != GraphicsInvestigationStatus.Cancelled ||
            coverage.Execution == GraphicsExecutionStatus.Failed &&
            investigation.Status != GraphicsInvestigationStatus.Failed ||
            coverage.Execution == GraphicsExecutionStatus.Unsupported &&
            investigation.Status != GraphicsInvestigationStatus.Unsupported ||
            coverage.Execution == GraphicsExecutionStatus.Completed &&
            investigation.Status is GraphicsInvestigationStatus.Cancelled or GraphicsInvestigationStatus.Failed
                or GraphicsInvestigationStatus.Unsupported ||
            coverage.Status == GraphicsCoverageStatus.Complete &&
            (coverage.Execution != GraphicsExecutionStatus.Completed ||
             coverage.CompletedSources != coverage.ExpectedSources || coverage.UnavailableSources.Count != 0 ||
             sources.Count != coverage.ExpectedSources ||
             sources.Any(source => source.Status != GraphicsSourceStatus.Completed)))
            throw new InvalidDataException("Cobertura da investigação gráfica inválida.");

        if (adapters.Select(item => item?.StableId).Distinct(StringComparer.Ordinal).Count() != adapters.Count ||
            adapters.Any(item => item is null || string.IsNullOrWhiteSpace(item.StableId) ||
                string.IsNullOrWhiteSpace(item.Source) || item.Properties is null || item.Limitations is null ||
                item.DriverDateUtc?.Offset is { } driverOffset && driverOffset != TimeSpan.Zero))
            throw new InvalidDataException("Adaptadores da investigação gráfica inválidos.");
        if (monitors.Select(item => item?.StableId).Distinct(StringComparer.Ordinal).Count() != monitors.Count ||
            monitors.Any(item => item is null || string.IsNullOrWhiteSpace(item.StableId) ||
                string.IsNullOrWhiteSpace(item.Source) || item.Properties is null || item.Limitations is null ||
                item.SupportedModes is null))
            throw new InvalidDataException("Monitores da investigação gráfica inválidos.");
        if (drivers.Select(item => item?.StableId).Distinct(StringComparer.Ordinal).Count() != drivers.Count ||
            drivers.Any(item => item is null || string.IsNullOrWhiteSpace(item.StableId) ||
                string.IsNullOrWhiteSpace(item.Source) || item.Limitations is null ||
                item.DateUtc?.Offset is { } driverDateOffset && driverDateOffset != TimeSpan.Zero))
            throw new InvalidDataException("Drivers da investigação gráfica inválidos.");

        if (events.Select(item => item?.Id).Distinct(StringComparer.Ordinal).Count() != events.Count ||
            events.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Channel) || string.IsNullOrWhiteSpace(item.Provider) ||
                item.EventId <= 0 || item.OccurredUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(item.Kind) ||
                !Enum.IsDefined(item.Level) || item.Data is null || item.Limitations is null))
            throw new InvalidDataException("Eventos da investigação gráfica inválidos.");
        if (werReports.Select(item => item?.Id).Distinct(StringComparer.Ordinal).Count() != werReports.Count ||
            werReports.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.ReportId) || string.IsNullOrWhiteSpace(item.Source) ||
                !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Confidence) ||
                item.EventTimeUtc?.Offset is { } eventOffset && eventOffset != TimeSpan.Zero ||
                item.CreationTimeUtc?.Offset is { } creationOffset && creationOffset != TimeSpan.Zero ||
                item.Data is null || item.Limitations is null))
            throw new InvalidDataException("Relatórios WER da investigação gráfica inválidos.");

        if (observations.Select(item => item?.Id).Distinct(StringComparer.Ordinal).Count() != observations.Count ||
            observations.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.RuleId) || string.IsNullOrWhiteSpace(item.Title) ||
                string.IsNullOrWhiteSpace(item.Statement) || string.IsNullOrWhiteSpace(item.Metric) ||
                string.IsNullOrWhiteSpace(item.Unit) || string.IsNullOrWhiteSpace(item.Source) ||
                !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Severity) ||
                !Enum.IsDefined(item.Confidence) || !Enum.IsDefined(item.Causality) ||
                item.Causality != CausalityStatus.NotAssessed ||
                item.WindowFromUtc.Offset != TimeSpan.Zero || item.WindowToUtc.Offset != TimeSpan.Zero ||
                item.WindowFromUtc > item.WindowToUtc ||
                item.FirstObservedUtc?.Offset is { } firstOffset && firstOffset != TimeSpan.Zero ||
                item.LastObservedUtc?.Offset is { } lastOffset && lastOffset != TimeSpan.Zero ||
                item.FirstObservedUtc > item.LastObservedUtc || item.EvidenceCount < 0 ||
                item.Limitations is null))
            throw new InvalidDataException("Observações da investigação gráfica inválidas.");
        if (notes.Any(note => note is null || string.IsNullOrWhiteSpace(note.RuleId) ||
                string.IsNullOrWhiteSpace(note.RuleVersion) || note.Detail is null ||
                !Enum.IsDefined(note.Reason)))
            throw new InvalidDataException("Notas de análise da investigação gráfica inválidas.");
        if (changes.Any(change => change is null || string.IsNullOrWhiteSpace(change.Subject) ||
                !Enum.IsDefined(change.Type)) ||
            !investigation.ChangesComparable && changes.Count > 0)
            throw new InvalidDataException("Alterações da investigação gráfica inválidas.");
        if (!investigation.Limitations.Contains(GraphicsEvidenceClassification.ExtraScopeLimitation,
                StringComparer.Ordinal) ||
            !investigation.Limitations.Contains(GraphicsEvidenceClassification.CausalityLimitation,
                StringComparer.Ordinal))
            throw new InvalidDataException(
                "Limitações obrigatórias de escopo/causalidade ausentes na investigação gráfica.");
    }

    private static string CanonicalJson(GraphicsInvestigation investigation) =>
        JsonSerializer.Serialize(investigation, AuditJson.Options).Replace("\r\n", "\n");

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

    public Task<CorrelationStoreSnapshot> LoadCorrelationInvestigationsAsync(
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var investigations = ReconcileCorrelations(database, warnings, cancellationToken);
        WriteCorrelationProjection(investigations);
        var byId = investigations.ToDictionary(investigation => investigation.Id);
        var ordered = new List<CorrelationInvestigation>();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Id FROM CorrelationInvestigations ORDER BY CompletedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (Guid.TryParse(reader.GetString(0), out var id) && byId.TryGetValue(id, out var investigation))
                ordered.Add(investigation);
        return new CorrelationStoreSnapshot(ordered, warnings);
    }, cancellationToken);

    public Task<long> SaveCorrelationInvestigationAsync(CorrelationInvestigation investigation,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateCorrelationInvestigation(investigation);
        EnsureNoMonitoringRecord(investigation.Id);
        EnsureDirectories();
        using var storeLock = Lock();
        foreach (var kind in Enum.GetValues<AuditKind>())
            if (File.Exists(Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant(),
                    $"{investigation.Id:D}.md")))
                throw new InvalidDataException("ID já existe em uma auditoria; histórico preservado.");
        if (File.Exists(HardwareRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em um inventário; histórico preservado.");
        if (File.Exists(EventRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma investigação; histórico preservado.");
        if (File.Exists(PerformanceRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de desempenho; histórico preservado.");
        if (File.Exists(NetworkRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma sessão de rede; histórico preservado.");
        if (File.Exists(GraphicsRecordPath(investigation.Id)))
            throw new InvalidDataException("ID já existe em uma investigação gráfica; histórico preservado.");
        var json = CanonicalJson(investigation);
        var path = CorrelationRecordPath(investigation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long bytes;
        if (File.Exists(path))
        {
            var (existing, existingHash) = ReadCorrelationRecord(path);
            if (existingHash != RecordEnvelope.Hash(json) && CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
            bytes = new FileInfo(path).Length;
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = CorrelationReport.Render(investigation);
            var document = RecordEnvelope.Wrap(report, json);
            bytes = Encoding.UTF8.GetByteCount(document);
            if (bytes > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException(
                    "Investigação de correlação excede 4 MiB e não pode ser persistida neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
            RecordEnvelope.AtomicWrite(GetCorrelationReportPath(investigation.Id), report, overwrite: false);
        }
        using var database = OpenDatabase([]);
        var investigations = ReconcileCorrelations(database, [], CancellationToken.None);
        WriteCorrelationProjection(investigations);
        if (!investigations.Any(item => item.Id == investigation.Id) ||
            !File.Exists(GetCorrelationReportPath(investigation.Id)))
            throw new IOException("A gravação da investigação de correlação não foi concluída. O registro durável, " +
                "se existente, será recuperado na próxima tentativa.");
        return bytes;
    }, cancellationToken);

    private List<CorrelationInvestigation> ReconcileCorrelations(SqliteConnection database,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var investigations = new List<CorrelationInvestigation>();
        var hashes = new Dictionary<Guid, string>();
        var seen = new HashSet<Guid>();
        var directory = Path.Combine(root, "memory", "correlation");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (investigation, payloadHash) = ReadCorrelationRecord(path);
                    if (Path.GetFileName(path) != $"{investigation.Id:D}.md" || !seen.Add(investigation.Id))
                        throw new InvalidDataException("Identidade ou localização de correlação inválida.");
                    investigations.Add(investigation);
                    hashes[investigation.Id] = payloadHash;
                    RecordEnvelope.AtomicWrite(GetCorrelationReportPath(investigation.Id),
                        CorrelationReport.Render(investigation), overwrite: true);
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                    or UnauthorizedAccessException or ArgumentException or NullReferenceException)
                {
                    warnings.Add($"Investigação de correlação {Path.GetFileName(path)} não recuperada " +
                        $"({error.GetType().Name}): {error.Message}. Original preservado.");
                }
            }
        }
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM CorrelationInvestigations";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seen.Contains(id))
                    warnings.Add($"Investigação de correlação indexada {reader.GetString(0)} sem registro Markdown " +
                        "válido; excluída da validade.");
        }
        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM CorrelationRelations; DELETE FROM CorrelationEvidence; " +
                "DELETE FROM CorrelationSources; DELETE FROM CorrelationInvestigations";
            clear.ExecuteNonQuery();
        }
        foreach (var investigation in investigations)
            IndexCorrelation(database, transaction, investigation, hashes.GetValueOrDefault(investigation.Id));
        transaction.Commit();
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return investigations;
    }

    private void IndexCorrelation(SqliteConnection database, SqliteTransaction transaction,
        CorrelationInvestigation investigation, string? payloadHash = null)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO CorrelationInvestigations
                (Id, FormatVersion, SchemaVersion, SoftwareVersion, WindowPreset, PeriodFromUtc, PeriodToUtc,
                 StartedUtc, CompletedUtc, DurationSeconds, Status, ExecutionStatus, CoverageStatus,
                 RequestedSources, LastBootUtc, BootAuditId, EvidenceCount, TimelineCount, RelationCount,
                 UndatedCount, MergedReferenceCount, LimitHit, MarkdownPath, PayloadHash, SourcesJson, BootJson,
                 CoverageJson, NotesJson, LimitationsJson)
            VALUES ($id,$format,$schema,$software,$window,$from,$to,$start,$end,$duration,$status,$execution,
                 $coverage,$sources,$boot,$bootAudit,$evidence,$timeline,$relations,$undated,$merged,$limit,
                 $path,$hash,$sourcesJson,$bootJson,$coverageJson,$notesJson,$limits)
            """;
        command.Parameters.AddWithValue("$id", investigation.Id.ToString("D"));
        command.Parameters.AddWithValue("$format", investigation.FormatVersion);
        command.Parameters.AddWithValue("$schema", investigation.SchemaVersion);
        command.Parameters.AddWithValue("$software", investigation.SoftwareVersion);
        command.Parameters.AddWithValue("$window", investigation.Window.ToString());
        command.Parameters.AddWithValue("$from", investigation.PeriodFromUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", investigation.PeriodToUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$start", investigation.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", investigation.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$duration", investigation.Duration.TotalSeconds);
        command.Parameters.AddWithValue("$status", investigation.Status.ToString());
        command.Parameters.AddWithValue("$execution", investigation.Execution.ToString());
        command.Parameters.AddWithValue("$coverage", investigation.Coverage.Status.ToString());
        command.Parameters.AddWithValue("$sources", (int)investigation.RequestedSources);
        command.Parameters.AddWithValue("$boot",
            (object?)investigation.Boot.LastBootUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$bootAudit",
            (object?)investigation.Boot.AuditId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", investigation.Evidence.Count);
        command.Parameters.AddWithValue("$timeline", investigation.Timeline.Count);
        command.Parameters.AddWithValue("$relations", investigation.Relations.Count);
        command.Parameters.AddWithValue("$undated", investigation.Coverage.UndatedEvidence);
        command.Parameters.AddWithValue("$merged", investigation.Coverage.MergedReferences);
        command.Parameters.AddWithValue("$limit", investigation.Coverage.LimitHit ? 1 : 0);
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, CorrelationRecordPath(investigation.Id)));
        command.Parameters.AddWithValue("$hash", payloadHash ?? RecordEnvelope.Hash(CanonicalJson(investigation)));
        command.Parameters.AddWithValue("$sourcesJson",
            JsonSerializer.Serialize(investigation.Sources, AuditJson.Options));
        command.Parameters.AddWithValue("$bootJson",
            JsonSerializer.Serialize(investigation.Boot, AuditJson.Options));
        command.Parameters.AddWithValue("$coverageJson",
            JsonSerializer.Serialize(investigation.Coverage, AuditJson.Options));
        command.Parameters.AddWithValue("$notesJson",
            JsonSerializer.Serialize(investigation.AnalysisNotes, AuditJson.Options));
        command.Parameters.AddWithValue("$limits",
            JsonSerializer.Serialize(investigation.Limitations, AuditJson.Options));
        command.ExecuteNonQuery();

        foreach (var source in investigation.Sources)
        {
            using var child = database.CreateCommand(); child.Transaction = transaction;
            child.CommandText = """
                INSERT INTO CorrelationSources
                    (InvestigationId, Source, Status, RecordsRead, RecordsUsed, EvidenceCount, LimitHit, Note,
                     LimitationsJson)
                VALUES ($investigation,$source,$status,$read,$used,$evidence,$limit,$note,$limits)
                """;
            child.Parameters.AddWithValue("$investigation", investigation.Id.ToString("D"));
            child.Parameters.AddWithValue("$source", source.Source.ToString());
            child.Parameters.AddWithValue("$status", source.Status.ToString());
            child.Parameters.AddWithValue("$read", source.RecordsRead);
            child.Parameters.AddWithValue("$used", source.RecordsUsed);
            child.Parameters.AddWithValue("$evidence", source.EvidenceCount);
            child.Parameters.AddWithValue("$limit", source.LimitHit ? 1 : 0);
            child.Parameters.AddWithValue("$note", (object?)source.Note ?? DBNull.Value);
            child.Parameters.AddWithValue("$limits",
                JsonSerializer.Serialize(source.Limitations, AuditJson.Options));
            child.ExecuteNonQuery();
        }

        foreach (var evidence in investigation.Evidence)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = """
                INSERT INTO CorrelationEvidence
                    (InvestigationId, EvidenceId, Source, Kind, SourceRecordId, EffectiveTimestampUtc, Precision,
                     IdentityKey, IdentityKind, Severity, Confidence, Title, OriginsJson, LimitationsJson)
                VALUES ($investigation,$id,$source,$kind,$record,$timestamp,$precision,$identity,$identityKind,
                     $severity,$confidence,$title,$origins,$limits)
                """;
            row.Parameters.AddWithValue("$investigation", investigation.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", evidence.Id);
            row.Parameters.AddWithValue("$source", evidence.Source.ToString());
            row.Parameters.AddWithValue("$kind", evidence.Kind.ToString());
            row.Parameters.AddWithValue("$record", evidence.SourceRecordId);
            row.Parameters.AddWithValue("$timestamp",
                (object?)evidence.EffectiveTimestampUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            row.Parameters.AddWithValue("$precision", evidence.Precision.ToString());
            row.Parameters.AddWithValue("$identity", (object?)evidence.IdentityKey ?? DBNull.Value);
            row.Parameters.AddWithValue("$identityKind", evidence.IdentityKind.ToString());
            row.Parameters.AddWithValue("$severity", evidence.Severity.ToString());
            row.Parameters.AddWithValue("$confidence", evidence.Confidence.ToString());
            row.Parameters.AddWithValue("$title", evidence.Title);
            row.Parameters.AddWithValue("$origins",
                JsonSerializer.Serialize(evidence.Origins, AuditJson.Options));
            row.Parameters.AddWithValue("$limits",
                JsonSerializer.Serialize(evidence.Limitations, AuditJson.Options));
            row.ExecuteNonQuery();
        }

        foreach (var relation in investigation.Relations)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = """
                INSERT INTO CorrelationRelations
                    (InvestigationId, RelationId, RuleId, Kind, Severity, Confidence, Causality,
                     WindowFromUtc, WindowToUtc, MaximumGapSeconds, EvidenceIdsJson, LimitationsJson)
                VALUES ($investigation,$id,$rule,$kind,$severity,$confidence,$causality,$from,$to,$gap,$evidence,
                     $limits)
                """;
            row.Parameters.AddWithValue("$investigation", investigation.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", relation.Id);
            row.Parameters.AddWithValue("$rule", relation.RuleId);
            row.Parameters.AddWithValue("$kind", relation.Kind.ToString());
            row.Parameters.AddWithValue("$severity", relation.Severity.ToString());
            row.Parameters.AddWithValue("$confidence", relation.Confidence.ToString());
            row.Parameters.AddWithValue("$causality", relation.Causality.ToString());
            row.Parameters.AddWithValue("$from",
                (object?)relation.WindowFromUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            row.Parameters.AddWithValue("$to",
                (object?)relation.WindowToUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            row.Parameters.AddWithValue("$gap", (object?)relation.MaximumGapSeconds ?? DBNull.Value);
            row.Parameters.AddWithValue("$evidence",
                JsonSerializer.Serialize(relation.EvidenceIds, AuditJson.Options));
            row.Parameters.AddWithValue("$limits",
                JsonSerializer.Serialize(relation.Limitations, AuditJson.Options));
            row.ExecuteNonQuery();
        }
    }

    private void WriteCorrelationProjection(IReadOnlyList<CorrelationInvestigation> investigations)
    {
        var latest = investigations.OrderByDescending(item => item.CompletedUtc).ToArray();
        var lastComplete = latest.FirstOrDefault(item => item.Coverage.Status == CorrelationCoverageStatus.Complete);
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Investigações de correlação");
        text.AppendLine();
        text.AppendLine("Formato: 1");
        text.AppendLine();
        text.AppendLine("Projeção derivada dos registros Markdown imutáveis; não é fonte de verdade.");
        text.AppendLine("Correlação não é causalidade e não renova a validade dos registros de origem.");
        text.AppendLine();
        text.AppendLine("## Última investigação com cobertura completa");
        text.AppendLine();
        text.AppendLine(lastComplete is null ? "Nenhuma investigação com cobertura completa."
            : $"{lastComplete.Id:D} — {lastComplete.CompletedUtc:O} — {lastComplete.Evidence.Count} evidência(s) — " +
              $"{lastComplete.Relations.Count(relation => relation.IsHypothesis)} relação(ões)");
        text.AppendLine();
        text.AppendLine("## Histórico");
        text.AppendLine();
        if (latest.Length == 0) text.AppendLine("Nenhuma investigação anterior.");
        else
            foreach (var investigation in latest)
                text.AppendLine($"- [{investigation.Id:D}](correlation/{investigation.Id:D}.md) — " +
                    $"{investigation.CompletedUtc:O} — situação {CorrelationStatusName(investigation.Status)} — " +
                    $"cobertura {CorrelationCoverageName(investigation.Coverage.Status)} — " +
                    $"{investigation.Coverage.EvidenceFound} referência(s) — " +
                    $"{investigation.Coverage.DistinctOccurrences} ocorrência(s) — " +
                    $"{investigation.Relations.Count(relation => relation.IsHypothesis)} relação(ões)");
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "CORRELATION.md"), text.ToString(),
            overwrite: true);
    }

    private static string CorrelationStatusName(CorrelationStatus status) => status switch
    {
        CorrelationStatus.Completed => "concluída",
        CorrelationStatus.Partial => "parcial",
        CorrelationStatus.Cancelled => "cancelada",
        CorrelationStatus.Failed => "falhou",
        CorrelationStatus.Running => "em execução",
        _ => status.ToString()
    };

    private static string CorrelationCoverageName(CorrelationCoverageStatus status) => status switch
    {
        CorrelationCoverageStatus.Complete => "completa",
        CorrelationCoverageStatus.Partial => "parcial",
        CorrelationCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    };

    private static (CorrelationInvestigation Investigation, string PayloadHash) ReadCorrelationRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Investigação de correlação excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var investigation = JsonSerializer.Deserialize<CorrelationInvestigation>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de correlação nulo.");
        ValidateCorrelationInvestigation(investigation);
        return (investigation, hash);
    }

    private static void ValidateCorrelationInvestigation(CorrelationInvestigation investigation)
    {
        if (investigation is null) throw new InvalidDataException("Investigação de correlação nula.");
        var sources = investigation.Sources;
        var evidence = investigation.Evidence;
        var timeline = investigation.Timeline;
        var relations = investigation.Relations;
        var notes = investigation.AnalysisNotes;
        var coverage = investigation.Coverage;

        if (investigation.FormatVersion != 1 || investigation.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(investigation.SoftwareVersion) ||
            string.IsNullOrWhiteSpace(investigation.SchemaVersion) ||
            !Enum.IsDefined(investigation.Window) || !Enum.IsDefined(investigation.Status) ||
            investigation.Status == CorrelationStatus.Running ||
            investigation.RequestedSources == CorrelationSources.None ||
            (investigation.RequestedSources & ~CorrelationSources.All) != 0 ||
            investigation.PeriodFromUtc.Offset != TimeSpan.Zero ||
            investigation.PeriodToUtc.Offset != TimeSpan.Zero ||
            investigation.StartedUtc.Offset != TimeSpan.Zero ||
            investigation.CompletedUtc.Offset != TimeSpan.Zero ||
            investigation.PeriodFromUtc >= investigation.PeriodToUtc ||
            investigation.StartedUtc > investigation.CompletedUtc || investigation.Duration < TimeSpan.Zero ||
            sources is null || evidence is null || timeline is null || relations is null || notes is null ||
            coverage is null || investigation.Boot is null || investigation.Limitations is null)
            throw new InvalidDataException("Registro de investigação de correlação inválido ou versão incompatível.");

        var requested = Enum.GetValues<CorrelationSource>()
            .Where(source => investigation.RequestedSources.HasFlag(FlagFor(source))).ToArray();
        if (sources.Select(source => source?.Source).Distinct().Count() != sources.Count ||
            sources.Any(source => source is null || !Enum.IsDefined(source.Source) ||
                !Enum.IsDefined(source.Status) || source.RecordsRead < 0 || source.RecordsUsed < 0 ||
                source.EvidenceCount < 0 || source.Limitations is null ||
                source.Status == CorrelationSourceStatus.Skipped &&
                investigation.RequestedSources.HasFlag(FlagFor(source.Source))) ||
            requested.Any(source => !sources.Any(item => item.Source == source)))
            throw new InvalidDataException("Fontes da investigação de correlação inválidas.");

        if (evidence.Select(item => item?.Id).Distinct(StringComparer.Ordinal).Count() != evidence.Count ||
            evidence.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.SourceRecordId) || string.IsNullOrWhiteSpace(item.Title) ||
                !Enum.IsDefined(item.Source) || !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Precision) ||
                !Enum.IsDefined(item.IdentityKind) || !Enum.IsDefined(item.Severity) ||
                !Enum.IsDefined(item.Confidence) || item.Origins is null || item.Origins.Count == 0 ||
                item.Limitations is null ||
                item.EffectiveTimestampUtc?.Offset is { } effectiveOffset && effectiveOffset != TimeSpan.Zero ||
                item.OriginalTimestampUtc?.Offset is { } originalOffset && originalOffset != TimeSpan.Zero ||
                item.WindowFromUtc?.Offset is { } windowFromOffset && windowFromOffset != TimeSpan.Zero ||
                item.WindowToUtc?.Offset is { } windowToOffset && windowToOffset != TimeSpan.Zero ||
                item.WindowFromUtc > item.WindowToUtc ||
                item.Precision == CorrelationTimestampPrecision.Unknown && item.EffectiveTimestampUtc is not null ||
                item.Precision != CorrelationTimestampPrecision.Unknown && item.EffectiveTimestampUtc is null ||
                item.Origins.Any(origin => origin is null || !Enum.IsDefined(origin.Source) ||
                    string.IsNullOrWhiteSpace(origin.SourceRecordId) ||
                    string.IsNullOrWhiteSpace(origin.OriginReference))))
            throw new InvalidDataException("Evidências da investigação de correlação inválidas.");

        var evidenceIds = evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (timeline.Select(item => item?.EvidenceId).Distinct(StringComparer.Ordinal).Count() != timeline.Count ||
            timeline.Any(item => item is null || !evidenceIds.Contains(item.EvidenceId) ||
                item.TimestampUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(item.Precision) ||
                !Enum.IsDefined(item.Source) || !Enum.IsDefined(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.SourceRecordId) ||
                !Enum.IsDefined(item.Severity) || !Enum.IsDefined(item.Confidence)) ||
            !timeline.SequenceEqual(timeline.OrderBy(item => item.TimestampUtc)
                .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)))
            throw new InvalidDataException("Linha do tempo da investigação de correlação inválida.");

        if (relations.Select(item => item?.Id).Distinct(StringComparer.Ordinal).Count() != relations.Count ||
            relations.Any(relation => relation is null || string.IsNullOrWhiteSpace(relation.Id) ||
                string.IsNullOrWhiteSpace(relation.RuleId) || string.IsNullOrWhiteSpace(relation.RuleVersion) ||
                string.IsNullOrWhiteSpace(relation.Title) || string.IsNullOrWhiteSpace(relation.Statement) ||
                string.IsNullOrWhiteSpace(relation.Justification) ||
                !Enum.IsDefined(relation.Kind) || !Enum.IsDefined(relation.Severity) ||
                !Enum.IsDefined(relation.Confidence) || !Enum.IsDefined(relation.Causality) ||
                relation.Causality != CausalityStatus.NotAssessed ||
                relation.EvidenceIds is null || relation.Limitations is null ||
                relation.WindowFromUtc?.Offset is { } relationFromOffset && relationFromOffset != TimeSpan.Zero ||
                relation.WindowToUtc?.Offset is { } relationToOffset && relationToOffset != TimeSpan.Zero ||
                relation.WindowFromUtc > relation.WindowToUtc ||
                relation.MaximumGapSeconds is < 0 ||
                relation.Kind != CorrelationRelationKind.InsufficientEvidence && relation.EvidenceIds.Count < 2 ||
                relation.EvidenceIds.Any(id => !evidenceIds.Contains(id))) ||
            relations.Count(relation => relation.Kind == CorrelationRelationKind.InsufficientEvidence) > 1)
            throw new InvalidDataException("Relações da investigação de correlação inválidas.");

        if (notes.Any(note => note is null || string.IsNullOrWhiteSpace(note.RuleId) ||
                string.IsNullOrWhiteSpace(note.RuleVersion) || note.Detail is null ||
                !Enum.IsDefined(note.Reason)))
            throw new InvalidDataException("Notas da investigação de correlação inválidas.");

        if (investigation.Boot.LastBootUtc?.Offset is { } bootOffset && bootOffset != TimeSpan.Zero)
            throw new InvalidDataException("Horário de inicialização da correlação inválido.");

        var completed = sources.Count(source => source.Status == CorrelationSourceStatus.Completed);
        var partial = sources.Count(source => source.Status == CorrelationSourceStatus.Partial);
        var unavailable = sources.Count(source => source.Status == CorrelationSourceStatus.Unavailable);
        var cancelled = sources.Count(source => source.Status == CorrelationSourceStatus.Cancelled);
        var undated = evidence.Count(item => !item.HasReliableTimestamp);
        var mergedOriginCount = evidence.Sum(item => item.Origins.Count - 1);

        if (!Enum.IsDefined(coverage.Status) || coverage.ExpectedSources != requested.Length ||
            coverage.CompletedSources != completed || coverage.PartialSources != partial ||
            coverage.UnavailableSources != unavailable || coverage.CancelledSources != cancelled ||
            coverage.CompletedSources + coverage.PartialSources + coverage.UnavailableSources +
            coverage.CancelledSources != coverage.ExpectedSources ||
            coverage.DistinctOccurrences != evidence.Count || coverage.UndatedEvidence != undated ||
            coverage.MergedReferences != mergedOriginCount ||
            coverage.EvidenceFound != coverage.DistinctOccurrences + coverage.MergedReferences ||
            coverage.EvidenceFound != sources.Sum(source => source.EvidenceCount) ||
            sources.Any(source => source.RecordsUsed > source.RecordsRead) ||
            coverage.Limitations is null ||
            coverage.Status == CorrelationCoverageStatus.Complete &&
            (coverage.LimitHit || undated > 0 || completed != coverage.ExpectedSources ||
             coverage.PartialSources > 0 || coverage.UnavailableSources > 0 || coverage.CancelledSources > 0) ||
            investigation.Status == CorrelationStatus.Completed &&
            coverage.Status != CorrelationCoverageStatus.Complete ||
            investigation.Status == CorrelationStatus.Failed && unavailable != coverage.ExpectedSources ||
            // Cancelamento após a leitura preserva as evidências já consolidadas: nenhuma fonte fica
            // marcada como cancelada, mas a cobertura não pode ser declarada completa.
            investigation.Status == CorrelationStatus.Cancelled && cancelled == 0 &&
            coverage.Status == CorrelationCoverageStatus.Complete ||
            investigation.Status == CorrelationStatus.Partial &&
            coverage.Status == CorrelationCoverageStatus.Unavailable && unavailable < coverage.ExpectedSources)
            throw new InvalidDataException("Cobertura da investigação de correlação inválida.");

        if (!investigation.Limitations.Contains(CorrelationGuidance.CausalityLimitation, StringComparer.Ordinal) ||
            !investigation.Limitations.Contains(CorrelationGuidance.ScopeLimitation, StringComparer.Ordinal) ||
            !investigation.Limitations.Contains(CorrelationGuidance.NonRenewalLimitation, StringComparer.Ordinal))
            throw new InvalidDataException(
                "Limitações obrigatórias de causalidade/escopo ausentes na investigação de correlação.");

        foreach (var relation in relations)
            if (!relation.Limitations.Contains(CorrelationGuidance.CausalityLimitation, StringComparer.Ordinal) &&
                relation.Kind != CorrelationRelationKind.InsufficientEvidence)
                throw new InvalidDataException(
                    "Relação de correlação sem a limitação obrigatória de causalidade.");
    }

    private static CorrelationSources FlagFor(CorrelationSource source) => source switch
    {
        CorrelationSource.Audits => CorrelationSources.Audits,
        CorrelationSource.Hardware => CorrelationSources.Hardware,
        CorrelationSource.Events => CorrelationSources.Events,
        CorrelationSource.Performance => CorrelationSources.Performance,
        CorrelationSource.Network => CorrelationSources.Network,
        CorrelationSource.Graphics => CorrelationSources.Graphics,
        CorrelationSource.Monitoring => CorrelationSources.Monitoring,
        _ => CorrelationSources.None
    };

    private static string CanonicalJson(CorrelationInvestigation investigation) =>
        JsonSerializer.Serialize(investigation, AuditJson.Options).Replace("\r\n", "\n");

    public Task<MonitoringStoreSnapshot> LoadMonitoringSessionsAsync(
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var (sessions, segments, recoveries) = ReconcileMonitoring(database, warnings, cancellationToken);
        WriteMonitoringProjection(sessions);
        var byId = sessions.ToDictionary(session => session.Id);
        var ordered = new List<MonitoringSession>();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Id FROM MonitoringSessions ORDER BY CompletedUtc DESC, Id";
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (Guid.TryParse(reader.GetString(0), out var id) && byId.TryGetValue(id, out var session))
                ordered.Add(session);
        foreach (var recovered in sessions.Where(session => !ordered.Any(item => item.Id == session.Id))
                     .OrderByDescending(session => session.CompletedUtc))
            ordered.Add(recovered);
        ordered = ordered.OrderByDescending(session => session.CompletedUtc)
            .ThenBy(session => session.Id)
            .ToList();
        return new MonitoringStoreSnapshot(ordered, segments, recoveries, warnings);
    }, cancellationToken);

    public Task<long> SaveMonitoringSegmentAsync(MonitoringSegment segment,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateMonitoringSegment(segment);
        EnsureDirectories();
        using var storeLock = Lock();
        EnsureNoOtherRecord(segment.SessionId);
        var path = MonitoringSegmentPath(segment.SessionId, segment.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var finalSessionPath = MonitoringRecordPath(segment.SessionId);
        if (File.Exists(finalSessionPath))
        {
            var (finalSession, _) = ReadMonitoringSession(finalSessionPath);
            if (!finalSession.Segments.Any(reference => reference.SegmentId == segment.Id))
                throw new InvalidDataException("A sessão já possui registro final; um novo checkpoint não é aceito " +
                    "para não divergir do registro imutável.");
        }
        var json = CanonicalJson(segment);
        if (File.Exists(path))
        {
            var (existing, existingHash) = ReadMonitoringSegment(path);
            if (existingHash != RecordEnvelope.Hash(json) && CanonicalJson(existing) != json)
                throw new InvalidDataException("Segmento já existe com conteúdo diferente; histórico preservado.");
            return new FileInfo(path).Length;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var report = MonitoringReport.RenderSegment(segment);
        var document = RecordEnvelope.Wrap(report, json);
        var bytes = Encoding.UTF8.GetByteCount(document);
        if (bytes > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Checkpoint de monitoramento excede 4 MiB e não pode ser persistido.");
        RecordEnvelope.AtomicWrite(path, document, overwrite: false);
        return bytes;
    }, cancellationToken);

    public Task<long> SaveMonitoringSessionAsync(MonitoringSession session,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateMonitoringSession(session);
        EnsureDirectories();
        using var storeLock = Lock();
        EnsureNoOtherRecord(session.Id);
        var availableSegments = new Dictionary<Guid, MonitoringSegment>();
        foreach (var reference in session.Segments)
        {
            var segmentPath = MonitoringSegmentPath(session.Id, reference.SegmentId);
            if (!File.Exists(segmentPath))
                throw new InvalidDataException("Segmento referenciado pela sessão não está confirmado em disco; " +
                    "o registro final não foi gravado.");
            var (segment, _) = ReadMonitoringSegment(segmentPath);
            availableSegments[segment.Id] = segment;
        }
        ValidateSegmentReferences(session, availableSegments);
        var json = CanonicalJson(session);
        var path = MonitoringRecordPath(session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long bytes;
        if (File.Exists(path))
        {
            var (existing, existingHash) = ReadMonitoringSession(path);
            if (existingHash != RecordEnvelope.Hash(json) && CanonicalJson(existing) != json)
                throw new InvalidDataException("ID já existe com conteúdo diferente; histórico preservado.");
            RecordEnvelope.AtomicWrite(GetMonitoringReportPath(session.Id), MonitoringReport.Render(session),
                overwrite: true);
            bytes = new FileInfo(path).Length;
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = MonitoringReport.Render(session);
            var document = RecordEnvelope.Wrap(report, json);
            bytes = Encoding.UTF8.GetByteCount(document);
            if (bytes > RecordEnvelope.MaximumRecordBytes)
                throw new InvalidDataException(
                    "Sessão de monitoramento excede 4 MiB e não pode ser persistida neste formato.");
            RecordEnvelope.AtomicWrite(path, document, overwrite: false);
            RecordEnvelope.AtomicWrite(GetMonitoringReportPath(session.Id), report, overwrite: false);
        }

        using var database = OpenDatabase([]);
        var (sessions, _, _) = ReconcileMonitoring(database, [], CancellationToken.None);
        WriteMonitoringProjection(sessions);
        if (!sessions.Any(item => item.Id == session.Id) || !File.Exists(GetMonitoringReportPath(session.Id)))
            throw new IOException("A gravação da sessão de monitoramento não foi concluída. O registro durável, " +
                "se existente, será recuperado na próxima tentativa.");
        return bytes;
    }, cancellationToken);

    private (List<MonitoringSession> Sessions, List<MonitoringSegment> Segments,
        List<MonitoringRecovery> Recoveries) ReconcileMonitoring(SqliteConnection database,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var segments = new List<MonitoringSegment>();
        var segmentHashes = new Dictionary<Guid, string>();
        var segmentsBySession = new Dictionary<Guid, List<MonitoringSegment>>();
        var seenSegments = new HashSet<Guid>();
        var segmentsRoot = Path.Combine(root, "memory", "monitoring");
        if (Directory.Exists(segmentsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(segmentsRoot)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(Path.GetFileName(directory), out var sessionId))
                {
                    warnings.Add($"Pasta de monitoramento {Path.GetFileName(directory)} sem identificador válido; " +
                        "preservada e não indexada.");
                    continue;
                }

                var segmentsDirectory = Path.Combine(directory, "segments");
                if (!Directory.Exists(segmentsDirectory)) continue;
                foreach (var path in Directory.EnumerateFiles(segmentsDirectory, "*.md",
                             SearchOption.TopDirectoryOnly).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var (segment, hash) = ReadMonitoringSegment(path);
                        if (segment.SessionId != sessionId)
                            throw new InvalidDataException("Segmento fora da pasta da sessão informada.");
                        if (Path.GetFileName(path) != $"{segment.Id:D}.md" || !seenSegments.Add(segment.Id))
                            throw new InvalidDataException("Identidade ou localização de segmento inválida.");
                        segments.Add(segment);
                        segmentHashes[segment.Id] = hash;
                        if (!segmentsBySession.TryGetValue(sessionId, out var list))
                            segmentsBySession[sessionId] = list = [];
                        list.Add(segment);
                    }
                    catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                        or UnauthorizedAccessException or ArgumentException or NullReferenceException)
                    {
                        warnings.Add($"Checkpoint de monitoramento {Path.GetFileName(path)} não recuperado " +
                            $"({error.GetType().Name}): {error.Message}. Original preservado.");
                    }
                }
            }
        }

        var sessions = new List<MonitoringSession>();
        var sessionHashes = new Dictionary<Guid, string>();
        var seenSessions = new HashSet<Guid>();
        var rejectedFinalSessions = new HashSet<Guid>();
        if (Directory.Exists(segmentsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(segmentsRoot, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MonitoringSession? accepted = null;
                try
                {
                    var (session, hash) = ReadMonitoringSession(path);
                    if (Path.GetFileName(path) != $"{session.Id:D}.md" || !seenSessions.Add(session.Id))
                        throw new InvalidDataException("Identidade ou localização de sessão inválida.");
                    var available = segmentsBySession.TryGetValue(session.Id, out var list)
                        ? list.ToDictionary(segment => segment.Id)
                        : [];
                    ValidateSegmentReferences(session, available);
                    foreach (var segment in available.Values)
                        if (!session.Segments.Any(reference => reference.SegmentId == segment.Id))
                            warnings.Add($"Checkpoint {segment.Id:D} da sessão {session.Id:D} não é referenciado " +
                                "pelo registro final; preservado e indexado fora da contagem da sessão.");
                    sessions.Add(session);
                    sessionHashes[session.Id] = hash;
                    accepted = session;
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException
                    or UnauthorizedAccessException or ArgumentException or NullReferenceException)
                {
                    if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var rejectedId))
                        rejectedFinalSessions.Add(rejectedId);
                    warnings.Add($"Sessão de monitoramento {Path.GetFileName(path)} não recuperada " +
                        $"({error.GetType().Name}): {error.Message}. Original preservado; o registro final existe, " +
                        "mas está ilegível e não é tratado como ausente.");
                }

                if (accepted is not null)
                {
                    try
                    {
                        RecordEnvelope.AtomicWrite(GetMonitoringReportPath(accepted.Id),
                            MonitoringReport.Render(accepted), overwrite: true);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"O registro final da sessão {accepted.Id:D} é válido, mas o relatório " +
                            $"derivado não pôde ser regenerado ({error.GetType().Name}): {error.Message}. " +
                            "Isso não invalida a sessão nem indica registro ilegível.");
                    }
                }
            }
        }

        var recoveries = new List<MonitoringRecovery>();
        var recoveredSessions = new List<MonitoringSession>();
        foreach (var pair in segmentsBySession.OrderBy(item => item.Key))
        {
            if (seenSessions.Contains(pair.Key)) continue;
            if (rejectedFinalSessions.Contains(pair.Key))
            {
                warnings.Add($"Sessão de monitoramento {pair.Key:D} possui registro final ilegível; os checkpoints " +
                    "foram preservados, mas nenhuma sessão foi derivada como se o registro estivesse ausente.");
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = pair.Value.OrderBy(segment => segment.Source).ThenBy(segment => segment.Sequence).ToArray();
            var derived = BuildRecoveredSession(pair.Key, ordered);
            recoveredSessions.Add(derived);
            recoveries.Add(new MonitoringRecovery(pair.Key, DateTimeOffset.UtcNow,
                ordered.Min(segment => segment.FromUtc), ordered.Max(segment => segment.ToUtc), ordered.Length,
                ordered.Sum(segment => segment.Performance.Count),
                ordered.Sum(segment => segment.Events.Count),
                ordered.Sum(segment => segment.Network.Count),
                "Registro final ausente: sessão identificada como interrompida apenas pelos checkpoints imutáveis, " +
                "sem atribuir conclusão normal."));
            warnings.Add($"Sessão de monitoramento {pair.Key:D} sem registro final; recuperada como interrompida " +
                $"a partir de {ordered.Length} checkpoint(s). Nenhum registro foi gravado ou alterado.");
        }

        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM MonitoringSessions";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (!Guid.TryParse(reader.GetString(0), out var id) || !seenSessions.Contains(id))
                    warnings.Add($"Sessão de monitoramento indexada {reader.GetString(0)} sem registro Markdown " +
                        "válido; excluída da validade.");
        }

        using var transaction = database.BeginTransaction();
        using (var clear = database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM MonitoringIncidents; DELETE FROM MonitoringSegments; " +
                "DELETE FROM MonitoringRecoveries; DELETE FROM MonitoringSessions";
            clear.ExecuteNonQuery();
        }

        foreach (var session in sessions)
        {
            var sessionSegments = segmentsBySession.TryGetValue(session.Id, out var list) ? list : [];
            IndexMonitoring(database, transaction, session, sessionHashes.GetValueOrDefault(session.Id),
                sessionSegments, segmentHashes);
        }

        foreach (var recovery in recoveries)
        {
            using var insert = database.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO MonitoringRecoveries
                    (SessionId, DetectedUtc, FirstSegmentUtc, LastSegmentUtc, SegmentCount,
                     PerformanceObservations, EventObservations, NetworkObservations, Detail)
                VALUES ($id,$detected,$first,$last,$count,$performance,$events,$network,$detail)
                """;
            insert.Parameters.AddWithValue("$id", recovery.SessionId.ToString("D"));
            insert.Parameters.AddWithValue("$detected", recovery.DetectedUtc.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$first", recovery.FirstSegmentUtc.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$last", recovery.LastSegmentUtc.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$count", recovery.SegmentCount);
            insert.Parameters.AddWithValue("$performance", recovery.PerformanceObservations);
            insert.Parameters.AddWithValue("$events", recovery.EventObservations);
            insert.Parameters.AddWithValue("$network", recovery.NetworkObservations);
            insert.Parameters.AddWithValue("$detail", recovery.Detail);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        var all = sessions.Concat(recoveredSessions)
            .OrderByDescending(session => session.CompletedUtc)
            .ThenBy(session => session.Id)
            .ToList();
        return (all, segments, recoveries);
    }

    private static MonitoringSession BuildRecoveredSession(Guid sessionId,
        IReadOnlyList<MonitoringSegment> segments)
    {
        var ordered = segments.OrderBy(segment => segment.Source).ThenBy(segment => segment.Sequence).ToArray();
        var first = ordered.Min(segment => segment.FromUtc);
        var last = ordered.Max(segment => segment.ToUtc);
        var sources = ordered.GroupBy(segment => segment.Source).Select(group => new MonitoringSourceSummary(
            group.Key, group.First().CollectorId, group.First().CollectorVersion,
            MonitoringSourceStatus.Partial, TimeSpan.Zero, group.Count(), group.Count(), 0, 0,
            group.Sum(segment => segment.ObservationCount), false, false,
            [
                MonitoringGuidance.RecoveryLimitation,
                "Contagem derivada dos checkpoints confirmados; o número de ciclos esperados e a cadência " +
                "original são desconhecidos."
            ])).ToArray();
        var performance = ordered.Sum(segment => segment.Performance.Count);
        var events = ordered.Sum(segment => segment.Events.Count);
        var network = ordered.Sum(segment => segment.Network.Count);
        MonitoringSources requested = MonitoringSources.None;
        foreach (var source in sources) requested |= MonitoringGuidance.Flag(source.Source);
        var observations = performance + events + network;
        var budget = new MonitoringBudgetReport(TimeSpan.Zero, TimeSpan.Zero, ordered.Length, ordered.Length, 0,
            0, 0, performance, events, 0, 0, false, false, false, false,
            "desconhecida (sessão interrompida)", [MonitoringGuidance.RecoveryLimitation]);
        var recovery = new MonitoringRecoveryInfo(true, last, ordered.Length,
            "Registro final ausente: a sessão foi interrompida e o estado foi derivado somente dos checkpoints " +
            "confirmados. Nenhum estado Completed foi atribuído, nenhum horário ausente foi inventado e os " +
            "incidentes registrados em memória não puderam ser recuperados.");
        return new MonitoringSession(1, sessionId, "desconhecida (registro final ausente)",
            SoftwareInfo.MonitoringSchemaVersion, MonitoringDurationPreset.Unknown, TimeSpan.Zero,
            first, last, last - first, MonitoringExecutionStatus.Interrupted,
            observations == 0 ? MonitoringCoverageStatus.Unavailable : MonitoringCoverageStatus.Partial,
            requested, sources, ordered.Select(segment => segment.Reference).ToArray(), [], budget, recovery,
            [
                MonitoringGuidance.AuthorizationLimitation,
                MonitoringGuidance.ScopeLimitation,
                MonitoringGuidance.CausalityLimitation,
                MonitoringGuidance.BudgetLimitation,
                MonitoringGuidance.RecoveryLimitation,
                "O registro final que confirmaria autorização, duração e incidentes está ausente; a recuperação " +
                "não reconstitui esses dados e não atribui conclusão normal."
            ]);
    }

    private void IndexMonitoring(SqliteConnection database, SqliteTransaction transaction,
        MonitoringSession session, string? payloadHash, IReadOnlyList<MonitoringSegment> sessionSegments,
        IReadOnlyDictionary<Guid, string> segmentHashes)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO MonitoringSessions
                (Id, FormatVersion, SchemaVersion, SoftwareVersion, Preset, AuthorizedDurationSeconds,
                 StartedUtc, CompletedUtc, DurationSeconds, Status, CoverageStatus, RequestedSources,
                 SegmentCount, IncidentCount, Recovered, BudgetJson, SourcesJson, RecoveryJson,
                 LimitationsJson, MarkdownPath, PayloadHash)
            VALUES ($id,$format,$schema,$software,$preset,$authorized,$start,$end,$duration,$status,$coverage,
                 $sources,$segments,$incidents,$recovered,$budget,$sourcesJson,$recovery,$limits,$path,$hash)
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$format", session.FormatVersion);
        command.Parameters.AddWithValue("$schema", session.SchemaVersion);
        command.Parameters.AddWithValue("$software", session.SoftwareVersion);
        command.Parameters.AddWithValue("$preset", session.Preset.ToString());
        command.Parameters.AddWithValue("$authorized", session.AuthorizedDuration.TotalSeconds);
        command.Parameters.AddWithValue("$start", session.StartedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", session.CompletedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$duration", session.Duration.TotalSeconds);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$coverage", session.CoverageStatus.ToString());
        command.Parameters.AddWithValue("$sources", (int)session.RequestedSources);
        command.Parameters.AddWithValue("$segments", session.Segments.Count);
        command.Parameters.AddWithValue("$incidents", session.Incidents.Count);
        command.Parameters.AddWithValue("$recovered", session.Recovery.Recovered ? 1 : 0);
        command.Parameters.AddWithValue("$budget",
            JsonSerializer.Serialize(session.Budget, AuditJson.Options));
        command.Parameters.AddWithValue("$sourcesJson",
            JsonSerializer.Serialize(session.Sources, AuditJson.Options));
        command.Parameters.AddWithValue("$recovery",
            JsonSerializer.Serialize(session.Recovery, AuditJson.Options));
        command.Parameters.AddWithValue("$limits",
            JsonSerializer.Serialize(session.Limitations, AuditJson.Options));
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, MonitoringRecordPath(session.Id)));
        command.Parameters.AddWithValue("$hash",
            payloadHash ?? RecordEnvelope.Hash(CanonicalJson(session)));
        command.ExecuteNonQuery();

        foreach (var segment in sessionSegments)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = """
                INSERT INTO MonitoringSegments
                    (SessionId, SegmentId, Sequence, Source, CollectorId, CollectorVersion,
                     FromUtc, ToUtc, Status, ObservationCount, MarkdownPath, PayloadHash)
                VALUES ($session,$id,$sequence,$source,$collector,$version,$from,$to,$status,$count,$path,$hash)
                """;
            row.Parameters.AddWithValue("$session", session.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", segment.Id.ToString("D"));
            row.Parameters.AddWithValue("$sequence", segment.Sequence);
            row.Parameters.AddWithValue("$source", segment.Source.ToString());
            row.Parameters.AddWithValue("$collector", segment.CollectorId);
            row.Parameters.AddWithValue("$version", segment.CollectorVersion);
            row.Parameters.AddWithValue("$from", segment.FromUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$to", segment.ToUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$status", segment.Status.ToString());
            row.Parameters.AddWithValue("$count", segment.ObservationCount);
            row.Parameters.AddWithValue("$path",
                Path.GetRelativePath(root, MonitoringSegmentPath(segment.SessionId, segment.Id)));
            row.Parameters.AddWithValue("$hash", segmentHashes.GetValueOrDefault(segment.Id) ??
                RecordEnvelope.Hash(CanonicalJson(segment)));
            row.ExecuteNonQuery();
        }

        foreach (var incident in session.Incidents)
        {
            using var row = database.CreateCommand(); row.Transaction = transaction;
            row.CommandText = """
                INSERT INTO MonitoringIncidents
                    (SessionId, IncidentId, RecordedUtc, OccurredUtc, Precision, Category, Description,
                     SessionElapsedSeconds, EvidenceJson, LimitationsJson)
                VALUES ($session,$id,$recorded,$occurred,$precision,$category,$description,$elapsed,$evidence,$limits)
                """;
            row.Parameters.AddWithValue("$session", session.Id.ToString("D"));
            row.Parameters.AddWithValue("$id", incident.Id.ToString("D"));
            row.Parameters.AddWithValue("$recorded", incident.RecordedUtc.ToUniversalTime().ToString("O"));
            row.Parameters.AddWithValue("$occurred",
                (object?)incident.OccurredUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            row.Parameters.AddWithValue("$precision", incident.Precision.ToString());
            row.Parameters.AddWithValue("$category", incident.Category.ToString());
            row.Parameters.AddWithValue("$description", (object?)incident.Description ?? DBNull.Value);
            row.Parameters.AddWithValue("$elapsed", incident.SessionElapsedSeconds);
            row.Parameters.AddWithValue("$evidence",
                JsonSerializer.Serialize(incident.Evidence, AuditJson.Options));
            row.Parameters.AddWithValue("$limits",
                JsonSerializer.Serialize(incident.Limitations, AuditJson.Options));
            row.ExecuteNonQuery();
        }
    }

    private void WriteMonitoringProjection(IReadOnlyList<MonitoringSession> sessions)
    {
        var latest = sessions.OrderByDescending(item => item.CompletedUtc).ToArray();
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Sessões de monitoramento");
        text.AppendLine();
        text.AppendLine("Formato: 1");
        text.AppendLine();
        text.AppendLine("Projeção derivada dos registros Markdown imutáveis; não é fonte de verdade.");
        text.AppendLine("O histórico preserva as sessões; a observação não permanece ativa após o encerramento.");
        text.AppendLine();
        text.AppendLine("## Histórico");
        text.AppendLine();
        if (latest.Length == 0) text.AppendLine("Nenhuma sessão anterior.");
        else
            foreach (var session in latest)
            {
                var label = session.Recovery.Recovered
                    ? $"{session.Id:D} (interrompida, derivada de checkpoints)"
                    : $"[{session.Id:D}](monitoring/{session.Id:D}.md)";
                text.AppendLine($"- {label} — {session.CompletedUtc:O} — " +
                    $"{MonitoringGuidance.StatusName(session.Status)} — " +
                    $"cobertura {MonitoringGuidance.CoverageName(session.CoverageStatus)} — " +
                    $"{session.Incidents.Count} incidente(s) — {session.Segments.Count} checkpoint(s)");
            }
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "MONITORING.md"), text.ToString(),
            overwrite: true);
    }

    private static (MonitoringSession Session, string PayloadHash) ReadMonitoringSession(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Sessão de monitoramento excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var session = JsonSerializer.Deserialize<MonitoringSession>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de monitoramento nulo.");
        ValidateMonitoringSession(session);
        return (session, hash);
    }

    private static (MonitoringSegment Segment, string PayloadHash) ReadMonitoringSegment(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Checkpoint de monitoramento excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var segment = JsonSerializer.Deserialize<MonitoringSegment>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de checkpoint nulo.");
        ValidateMonitoringSegment(segment);
        return (segment, hash);
    }

    private static void ValidateMonitoringSegment(MonitoringSegment segment)
    {
        if (segment is null) throw new InvalidDataException("Checkpoint de monitoramento nulo.");
        if (segment.FormatVersion != 1 || segment.Id == Guid.Empty || segment.SessionId == Guid.Empty ||
            segment.Sequence < 0 || !Enum.IsDefined(segment.Source) || !Enum.IsDefined(segment.Status) ||
            string.IsNullOrWhiteSpace(segment.CollectorId) ||
            string.IsNullOrWhiteSpace(segment.CollectorVersion) ||
            segment.FromUtc.Offset != TimeSpan.Zero || segment.ToUtc.Offset != TimeSpan.Zero ||
            segment.FromUtc > segment.ToUtc || segment.Performance is null || segment.Events is null ||
            segment.Network is null || segment.Limitations is null)
            throw new InvalidDataException("Checkpoint de monitoramento inválido ou versão incompatível.");

        if (segment.Performance.Select(item => item.Index).Distinct().Count() != segment.Performance.Count ||
            segment.Performance.Any(item => item is null || item.Index < 0 ||
                item.TimestampUtc.Offset != TimeSpan.Zero ||
                item.TimestampUtc < segment.FromUtc || item.TimestampUtc > segment.ToUtc ||
                item.ElapsedSeconds < 0 || item.Limitations is null))
            throw new InvalidDataException("Amostras de desempenho do checkpoint inválidas.");

        if (segment.Events.Select(item => item.Index).Distinct().Count() != segment.Events.Count ||
            segment.Events.Any(item => item is null || item.Index < 0 ||
                item.TimestampUtc.Offset != TimeSpan.Zero || item.OccurredUtc.Offset != TimeSpan.Zero ||
                item.TimestampUtc < segment.FromUtc || item.TimestampUtc > segment.ToUtc ||
                string.IsNullOrWhiteSpace(item.Channel) || string.IsNullOrWhiteSpace(item.Provider) ||
                string.IsNullOrWhiteSpace(item.GroupId) || !Enum.IsDefined(item.Level) ||
                !Enum.IsDefined(item.Category) || !Enum.IsDefined(item.Severity)))
            throw new InvalidDataException("Eventos do checkpoint inválidos.");

        if (segment.Network.Select(item => item.Index).Distinct().Count() != segment.Network.Count ||
            segment.Network.Any(item => item is null || item.Index < 0 ||
                item.TimestampUtc.Offset != TimeSpan.Zero ||
                item.TimestampUtc < segment.FromUtc || item.TimestampUtc > segment.ToUtc ||
                item.TcpConnections < 0 || item.UdpEndpoints < 0 || item.ListeningTcp < 0 ||
                item.EstablishedTcp < 0 || item.DistinctLocalPorts < 0 || item.ProcessesObserved < 0 ||
                item.Limitations is null))
            throw new InvalidDataException("Observações de rede do checkpoint inválidas.");

        if (segment.ObservationCount != segment.Performance.Count + segment.Events.Count + segment.Network.Count)
            throw new InvalidDataException("Contagem de observações do checkpoint inconsistente.");
    }

    private static void ValidateSegmentReferences(MonitoringSession session,
        IReadOnlyDictionary<Guid, MonitoringSegment> available)
    {
        foreach (var reference in session.Segments)
            if (!available.TryGetValue(reference.SegmentId, out var segment) ||
                segment.Source != reference.Source || segment.Sequence != reference.Sequence ||
                segment.FromUtc != reference.FromUtc || segment.ToUtc != reference.ToUtc ||
                segment.Status != reference.Status ||
                segment.ObservationCount != reference.ObservationCount)
                throw new InvalidDataException(
                    $"Segmento referenciado {reference.SegmentId:D} ausente ou incompatível com o registro final.");
    }

    private static void ValidateMonitoringSession(MonitoringSession session)
    {
        if (session is null) throw new InvalidDataException("Sessão de monitoramento nula.");
        var sources = session.Sources;
        var segments = session.Segments;
        var incidents = session.Incidents;
        var budget = session.Budget;

        if (session.FormatVersion != 1 || session.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(session.SoftwareVersion) ||
            string.IsNullOrWhiteSpace(session.SchemaVersion) || !Enum.IsDefined(session.Preset) ||
            !Enum.IsDefined(session.Status) || !Enum.IsDefined(session.CoverageStatus) ||
            session.Status is MonitoringExecutionStatus.Running ||
            session.Status == MonitoringExecutionStatus.Interrupted && !(session.Recovery?.Recovered ?? false) ||
            session.RequestedSources == MonitoringSources.None ||
            (session.RequestedSources & ~MonitoringSources.All) != 0 ||
            session.StartedUtc.Offset != TimeSpan.Zero || session.CompletedUtc.Offset != TimeSpan.Zero ||
            session.StartedUtc > session.CompletedUtc || session.Duration < TimeSpan.Zero ||
            session.AuthorizedDuration < TimeSpan.Zero ||
            sources is null || segments is null || incidents is null || budget is null ||
            session.Recovery is null || session.Limitations is null)
            throw new InvalidDataException("Registro de sessão de monitoramento inválido ou versão incompatível.");

        var requested = Enum.GetValues<MonitoringSource>()
            .Where(source => session.RequestedSources.HasFlag(MonitoringGuidance.Flag(source))).ToArray();
        if (!session.Recovery!.Recovered)
        {
            TimeSpan? expected = session.Preset switch
            {
                MonitoringDurationPreset.Minutes5 => TimeSpan.FromMinutes(5),
                MonitoringDurationPreset.Minutes15 => TimeSpan.FromMinutes(15),
                MonitoringDurationPreset.Minutes30 => TimeSpan.FromMinutes(30),
                MonitoringDurationPreset.Custom => session.AuthorizedDuration,
                _ => null
            };
            if (expected is null || session.AuthorizedDuration != expected ||
                session.AuthorizedDuration <= TimeSpan.Zero ||
                session.AuthorizedDuration > TimeSpan.FromMinutes(30) ||
                session.Duration > session.AuthorizedDuration + TimeSpan.FromSeconds(10))
                throw new InvalidDataException("Duração autorizada incompatível com o preset ou ultrapassada.");
        }

        if (session.Recovery.Recovered && session.Status == MonitoringExecutionStatus.Completed)
            throw new InvalidDataException("Sessão recuperada não pode ser declarada concluída.");

        if (sources.Select(source => source?.Source).Distinct().Count() != sources.Count ||
            sources.Any(source => source is null || !Enum.IsDefined(source.Source) ||
                !Enum.IsDefined(source.Status) || source.ExpectedTicks < 0 || source.CollectedTicks < 0 ||
                source.MissedOpportunities < 0 || source.TruncatedTicks < 0 || source.ObservationCount < 0 ||
                source.Interval < TimeSpan.Zero || source.Limitations is null ||
                source.Status == MonitoringSourceStatus.Completed && source.CollectedTicks < source.ExpectedTicks ||
                !session.RequestedSources.HasFlag(MonitoringGuidance.Flag(source.Source))) ||
            requested.Any(source => !sources.Any(item => item.Source == source)))
            throw new InvalidDataException("Fontes da sessão de monitoramento inválidas.");

        if (segments.Select(reference => reference?.SegmentId).Distinct().Count() != segments.Count ||
            segments.Select(reference => $"{reference?.Source}:{reference?.Sequence}")
                .Distinct(StringComparer.Ordinal).Count() != segments.Count ||
            segments.Any(reference => reference is null || reference.SegmentId == Guid.Empty ||
                reference.Sequence < 0 || !Enum.IsDefined(reference.Source) ||
                !Enum.IsDefined(reference.Status) || reference.FromUtc.Offset != TimeSpan.Zero ||
                reference.ToUtc.Offset != TimeSpan.Zero || reference.FromUtc > reference.ToUtc ||
                reference.ObservationCount < 0 ||
                !session.RequestedSources.HasFlag(MonitoringGuidance.Flag(reference.Source))))
            throw new InvalidDataException("Checkpoints referenciados pela sessão inválidos.");

        if (incidents.Select(incident => incident?.Id).Distinct().Count() != incidents.Count ||
            incidents.Any(incident => incident is null || incident.Id == Guid.Empty ||
                incident.SessionId != session.Id || !Enum.IsDefined(incident.Category) ||
                !Enum.IsDefined(incident.Precision) || incident.RecordedUtc.Offset != TimeSpan.Zero ||
                (incident.OccurredUtc is { } occurred && occurred.Offset != TimeSpan.Zero) ||
                incident.SessionElapsedSeconds < 0 ||
                incident.Description is { Length: > 280 } ||
                incident.Evidence is null || incident.Limitations is null ||
                !incident.Limitations.Contains(MonitoringGuidance.IncidentLimitation, StringComparer.Ordinal)))
            throw new InvalidDataException("Incidentes da sessão de monitoramento inválidos.");

        var totalObservations = sources.Sum(source => source.ObservationCount);
        if (session.CoverageStatus == MonitoringCoverageStatus.Complete &&
            (session.Status != MonitoringExecutionStatus.Completed ||
             sources.Any(source => source.Status != MonitoringSourceStatus.Completed) ||
             budget.StorageBudgetReached || budget.TickBudgetExceeded || budget.EventLimitReached ||
             budget.MissedOpportunities > 0))
            throw new InvalidDataException("Cobertura completa declarada sem cumprir os requisitos da sessão.");
        if (session.CoverageStatus == MonitoringCoverageStatus.Unavailable && totalObservations != 0)
            throw new InvalidDataException("Cobertura indisponível declarada com observações registradas.");
        if (session.Status == MonitoringExecutionStatus.Failed &&
            session.CoverageStatus == MonitoringCoverageStatus.Complete)
            throw new InvalidDataException("Execução com falha não pode declarar cobertura completa.");
        if (session.Status == MonitoringExecutionStatus.Interrupted && !session.Recovery.Recovered)
            throw new InvalidDataException("Sessão interrompida sem informação de recuperação.");
        if (!session.Recovery.Recovered && session.Recovery.LastSegmentUtc is { } last &&
            last.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Horário do último checkpoint inválido.");

        if (!session.Limitations.Contains(MonitoringGuidance.CausalityLimitation, StringComparer.Ordinal) ||
            !session.Limitations.Contains(MonitoringGuidance.ScopeLimitation, StringComparer.Ordinal) ||
            !session.Recovery.Recovered &&
            (!session.Limitations.Contains(MonitoringGuidance.AuthorizationLimitation, StringComparer.Ordinal) ||
             !session.Limitations.Contains(MonitoringGuidance.BudgetLimitation, StringComparer.Ordinal)))
            throw new InvalidDataException(
                "Limitações obrigatórias de autorização, escopo, orçamento ou causalidade ausentes.");
    }

    private void EnsureNoMonitoringRecord(Guid id)
    {
        if (File.Exists(MonitoringRecordPath(id)) || Directory.Exists(MonitoringSegmentDirectory(id)))
            throw new InvalidDataException("ID já existe em uma sessão de monitoramento; histórico preservado.");
    }

    private void EnsureNoOtherRecord(Guid id)
    {
        foreach (var kind in Enum.GetValues<AuditKind>())
            if (File.Exists(Path.Combine(root, "memory", "audits", kind.ToString().ToLowerInvariant(),
                    $"{id:D}.md")))
                throw new InvalidDataException("ID já existe em uma auditoria; histórico preservado.");
        if (File.Exists(HardwareRecordPath(id)) || File.Exists(EventRecordPath(id)) ||
            File.Exists(PerformanceRecordPath(id)) || File.Exists(NetworkRecordPath(id)) ||
            File.Exists(GraphicsRecordPath(id)) || File.Exists(CorrelationRecordPath(id)))
            throw new InvalidDataException("ID já existe em outro histórico; histórico preservado.");
    }

    private static string CanonicalJson(MonitoringSession session) =>
        JsonSerializer.Serialize(session, AuditJson.Options).Replace("\r\n", "\n");

    private static string CanonicalJson(MonitoringSegment segment) =>
        JsonSerializer.Serialize(segment, AuditJson.Options).Replace("\r\n", "\n");

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
