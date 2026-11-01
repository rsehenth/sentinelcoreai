using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Persistence;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("SENTINELCORE AI — VALIDAÇÃO REAL DA FASE 3.3B (somente leitura)");
Console.WriteLine($"Raiz de dados: {MemoryStore.DefaultRoot}");

var store = new MemoryStore();
var runner = new GraphicsInvestigationRunner(
    WindowsHardwareCollectors.Create(), WindowsEventEvidenceCollectors.Create());
var previous = (await store.LoadGraphicsInvestigationsAsync(CancellationToken.None)).Investigations
    .FirstOrDefault();

GraphicsInvestigation? investigation = null;
var started = DateTimeOffset.UtcNow;
var progress = new Progress<GraphicsProgress>(update =>
    Console.WriteLine($"  {update.Phase}: {update.SourceId} — {update.Status} ({update.Finished}/{update.Total})"));
investigation = await runner.RunAsync(new GraphicsInvestigationRequest(EventWindow.Last7Days),
    previous, progress, CancellationToken.None);
Console.WriteLine($"Investigação concluída em {(DateTimeOffset.UtcNow - started).TotalSeconds:F2}s.");
Console.WriteLine($"Comparação: {(investigation.ChangesComparable ? "avaliada" : "não avaliada")} · " +
    $"{investigation.Changes.Count} alteração(ões)");
Console.WriteLine($"ID: {investigation.Id:D}");
Console.WriteLine($"Situação: {investigation.Status} · execução {investigation.Coverage.Execution} · " +
    $"cobertura {investigation.Coverage.Status} ({investigation.Coverage.CompletedSources}/" +
    $"{investigation.Coverage.ExpectedSources})");
Console.WriteLine($"Adaptadores: {investigation.Adapters.Count} · monitores: {investigation.Monitors.Count} · " +
    $"drivers: {investigation.Drivers.Count} · eventos gráficos: {investigation.Events.Count} · " +
    $"WER: {investigation.WerReports.Count} · observações: {investigation.Observations.Count}");

foreach (var adapter in investigation.Adapters)
    Console.WriteLine($"  GPU: {adapter.Name} · {adapter.DriverProvider} {adapter.DriverVersion} · " +
        $"estado {adapter.WindowsState} · presença {adapter.Presence} · " +
        $"AdapterRAM {adapter.ReportedAdapterRamBytes?.ToString() ?? "indisponível"}");
foreach (var monitor in investigation.Monitors)
    Console.WriteLine($"  Monitor: {monitor.Name} · {monitor.Width}x{monitor.Height} @ {monitor.RefreshHz} Hz · " +
        $"HDR suportado {monitor.HdrSupported} · ativo {monitor.HdrEnabled}");
foreach (var driver in investigation.Drivers)
    Console.WriteLine($"  Driver: {driver.Name} · {driver.Provider} {driver.Version} · {driver.DeviceClass} · " +
        $"{driver.InfName}");
foreach (var item in investigation.Events)
    Console.WriteLine($"  Evento: {item.OccurredUtc:O} · {item.Provider} {item.EventId} · " +
        $"{item.Channel} · {item.Kind}");
foreach (var report in investigation.WerReports)
    Console.WriteLine($"  WER: {report.ReportId} · {report.EventType} {report.ProblemCode} · " +
        $"{report.Kind} · {report.Application}");
foreach (var observation in investigation.Observations)
    Console.WriteLine($"  Observação: [{observation.RuleId}] {observation.Title} · " +
        $"{observation.EvidenceCount} evidência(s) · causalidade {observation.Causality}");
Console.WriteLine("Limitações:");
foreach (var limitation in investigation.Limitations) Console.WriteLine($"  - {limitation}");

var bytes = await store.SaveGraphicsInvestigationAsync(investigation, CancellationToken.None);
Console.WriteLine($"Registro salvo: {bytes} bytes; relatório: {store.GetGraphicsReportPath(investigation.Id)}");
var loaded = await store.LoadGraphicsInvestigationsAsync(CancellationToken.None);
Console.WriteLine($"Investigação recarregada: {loaded.Warnings.Count} aviso(s), " +
    $"{loaded.Investigations.Count} registro(s) no histórico.");

var recordPath = Path.Combine(MemoryStore.DefaultRoot, "memory", "graphics", $"{investigation.Id:D}.md");
var text = await File.ReadAllTextAsync(recordPath, Encoding.UTF8);
const string marker = "\n<!-- sentinelcore:payload:v1 -->\n```json\n";
const string endMarker = "\n```\n<!-- sha256:";
var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
var payload = text[start..end];
var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
var declared = text.Substring(end + endMarker.Length, 64);
Console.WriteLine($"SHA-256 do envelope confere: {hash == declared}.");

using var database = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(MemoryStore.DefaultRoot, "data", "sentinelcore.db"),
    Mode = SqliteOpenMode.ReadOnly, Pooling = false
}.ToString());
database.Open();
object? Scalar(string sql)
{
    using var command = database.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}
Console.WriteLine($"SQLite user_version={Scalar("PRAGMA user_version")} · quick_check={Scalar("PRAGMA quick_check")} · " +
    $"GraphicsInvestigations={Scalar("SELECT COUNT(*) FROM GraphicsInvestigations")} · " +
    $"GraphicsSources={Scalar("SELECT COUNT(*) FROM GraphicsSources")} · " +
    $"GraphicsObservations={Scalar("SELECT COUNT(*) FROM GraphicsObservations")}");
Console.WriteLine("VALIDAÇÃO REAL CONCLUÍDA.");
