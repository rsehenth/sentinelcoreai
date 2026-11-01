using System.Text.Json;
using Microsoft.Data.Sqlite;

// Explicit root required. This validation tool only reads an existing database and files.
if (args.Length != 1 || !Directory.Exists(args[0]))
    throw new ArgumentException("Uso: StoreInspector <diretório de dados existente>");
var root = Path.GetFullPath(args[0]);
using var database = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(root, "data", "sentinelcore.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
}.ToString());
database.Open();
object? Scalar(string sql)
{
    using var command = database.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
}
var audits = new List<object>();
using (var command = database.CreateCommand())
{
    command.CommandText = "SELECT Id,Kind,Status,CompletedUtc,ExpiresUtc,MarkdownPath FROM Audits ORDER BY CompletedUtc DESC";
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        var id = reader.GetString(0);
        audits.Add(new
        {
            Id = id, Kind = reader.GetString(1), Status = reader.GetString(2), CompletedUtc = reader.GetString(3),
            ExpiresUtc = reader.IsDBNull(4) ? null : reader.GetString(4),
            MarkdownExists = File.Exists(Path.Combine(root, reader.GetString(5))),
            ReportExists = File.Exists(Path.Combine(root, "reports", id + ".md"))
        });
    }
}
bool HasTable(string name) =>
    Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{name}'")) > 0;
var hasEventTables = HasTable("EventInvestigations") && HasTable("EventSources") && HasTable("EventGroups");
var hasPerformanceTables = HasTable("PerformanceSessions") && HasTable("PerformanceSources") &&
    HasTable("PerformanceObservations");
var hasGraphicsTables = HasTable("GraphicsInvestigations") && HasTable("GraphicsSources") &&
    HasTable("GraphicsObservations");
bool ForeignKeysOk()
{
    using var command = database.CreateCommand();
    command.CommandText = "PRAGMA foreign_key_check";
    using var reader = command.ExecuteReader();
    return !reader.Read();
}
var performance = new List<object>();
if (hasPerformanceTables)
{
    using var command = database.CreateCommand();
    command.CommandText = """
        SELECT Id,Preset,Status,StartedUtc,CompletedUtc,MarkdownPath,
               (SELECT COUNT(*) FROM PerformanceSources s WHERE s.SessionId = p.Id),
               (SELECT COUNT(*) FROM PerformanceObservations o WHERE o.SessionId = p.Id)
        FROM PerformanceSessions p ORDER BY CompletedUtc DESC
        """;
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        var id = reader.GetString(0);
        performance.Add(new
        {
            Id = id, Preset = reader.GetString(1), Status = reader.GetString(2),
            StartedUtc = reader.GetString(3), CompletedUtc = reader.GetString(4),
            MarkdownExists = File.Exists(Path.Combine(root, reader.GetString(5))),
            ReportExists = File.Exists(Path.Combine(root, "reports", "performance", id + ".md")),
            SourceCount = reader.GetInt64(6), ObservationCount = reader.GetInt64(7)
        });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    Schema = Scalar("PRAGMA user_version"), Integrity = Scalar("PRAGMA quick_check"),
    FullIntegrity = Scalar("PRAGMA integrity_check"), ForeignKeysOk = ForeignKeysOk(),
    AuditCount = Scalar("SELECT COUNT(*) FROM Audits"), CollectorCount = Scalar("SELECT COUNT(*) FROM CollectorResults"),
    CompletedCollectors = Scalar("SELECT COUNT(*) FROM CollectorResults WHERE Status='Completed'"),
    HardwareSnapshotCount = HasTable("HardwareSnapshots") ? Scalar("SELECT COUNT(*) FROM HardwareSnapshots") : null,
    HardwareItemCount = HasTable("HardwareItems") ? Scalar("SELECT COUNT(*) FROM HardwareItems") : null,
    EventInvestigationCount = hasEventTables ? Scalar("SELECT COUNT(*) FROM EventInvestigations") : null,
    EventSourceCount = hasEventTables ? Scalar("SELECT COUNT(*) FROM EventSources") : null,
    EventGroupCount = hasEventTables ? Scalar("SELECT COUNT(*) FROM EventGroups") : null,
    PerformanceSessionCount = hasPerformanceTables ? Scalar("SELECT COUNT(*) FROM PerformanceSessions") : null,
    PerformanceSourceCount = hasPerformanceTables ? Scalar("SELECT COUNT(*) FROM PerformanceSources") : null,
    PerformanceObservationCount = hasPerformanceTables ? Scalar("SELECT COUNT(*) FROM PerformanceObservations") : null,
    GraphicsInvestigationCount = hasGraphicsTables ? Scalar("SELECT COUNT(*) FROM GraphicsInvestigations") : null,
    GraphicsSourceCount = hasGraphicsTables ? Scalar("SELECT COUNT(*) FROM GraphicsSources") : null,
    GraphicsObservationCount = hasGraphicsTables ? Scalar("SELECT COUNT(*) FROM GraphicsObservations") : null,
    Audits = audits,
    PerformanceRecords = performance
}, new JsonSerializerOptions { WriteIndented = true }));
