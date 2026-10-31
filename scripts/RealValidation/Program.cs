using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Infrastructure.Windows.Performance;
using SentinelCore.Persistence;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=================================================");
Console.WriteLine("SENTINELCORE AI — FASE 3.2.2 REAL VALIDATION RUN");
Console.WriteLine("=================================================");

var store = new MemoryStore();
Console.WriteLine($"Store Root: {MemoryStore.DefaultRoot}");

// 1. Basic Audit
Console.WriteLine("\n[1/5] Executando Auditoria Básica...");
var auditRunner = new AuditRunner(WindowsCollectors.CreateBasic());
var auditSw = Stopwatch.StartNew();
var audit = await auditRunner.RunBasicAsync(new Progress<AuditProgress>(p =>
{
    Console.Write($"\r  Progresso: {p.Finished}/{p.Total} ({p.CollectorId})        ");
}), CancellationToken.None);
auditSw.Stop();
Console.WriteLine($"\n  Auditoria Básica concluída em {auditSw.Elapsed.TotalSeconds:F2}s. Status: {audit.Status}");
await store.SaveAsync(audit);
Console.WriteLine($"  Auditoria salva com ID: {audit.Id}");

// 2. Hardware Snapshot
Console.WriteLine("\n[2/5] Executando Inventário de Hardware...");
var hwRunner = new HardwareRunner(WindowsHardwareCollectors.Create());
var hwSw = Stopwatch.StartNew();
var hwSnapshot = await hwRunner.RunAsync(new Progress<HardwareProgress>(p =>
{
    Console.Write($"\r  Progresso: {p.Finished}/{p.Total} ({p.CollectorId})        ");
}), CancellationToken.None);
hwSw.Stop();
Console.WriteLine($"\n  Inventário de Hardware concluído em {hwSw.Elapsed.TotalSeconds:F2}s. Status: {hwSnapshot.Status}, Itens: {hwSnapshot.Results.Sum(r => r.Items.Count)}");
await store.SaveSnapshotAsync(hwSnapshot);
Console.WriteLine($"  Hardware snapshot salvo com ID: {hwSnapshot.Id}");

// 3. Event Investigation
Console.WriteLine("\n[3/5] Executando Investigação de Eventos (janela 24h)...");
var eventRunner = new EventInvestigationRunner(WindowsEventEvidenceCollectors.Create());
var eventSw = Stopwatch.StartNew();
var eventInvestigation = await eventRunner.RunAsync(EventWindow.Last24Hours, progress: new Progress<EventInvestigationProgress>(p =>
{
    Console.Write($"\r  Progresso: {p.Finished}/{p.Total} ({p.CollectorId})        ");
}), cancellationToken: CancellationToken.None);
eventSw.Stop();
Console.WriteLine($"\n  Investigação de Eventos concluída em {eventSw.Elapsed.TotalSeconds:F2}s. Status: {eventInvestigation.Status}, Grupos: {eventInvestigation.Groups.Count}");
await store.SaveInvestigationAsync(eventInvestigation);
Console.WriteLine($"  Investigação salva com ID: {eventInvestigation.Id}");

// Performance Runner
var perfRunner = new PerformanceSessionRunner(WindowsPerformanceCollectors.Create());

// 4. Performance Session 30s
Console.WriteLine("\n[4/5] Executando Sessão de Desempenho 30s...");
var p30Sw = Stopwatch.StartNew();
var p30 = await perfRunner.RunAsync(PerformanceSessionPreset.Seconds30, hwSnapshot, new Progress<PerformanceProgress>(p =>
{
    Console.Write($"\r  Amostras: {p.SamplesCollected}/{p.ExpectedSamples} | Decorrido: {p.Elapsed.TotalSeconds:F1}s / {p.Requested.TotalSeconds:F0}s      ");
}), CancellationToken.None);
p30Sw.Stop();
Console.WriteLine($"\n  Sessão 30s concluída em {p30Sw.Elapsed.TotalSeconds:F2}s.");
await store.SaveSessionAsync(p30);
Console.WriteLine($"  Sessão 30s salva com ID: {p30.Id}");

// 5. Performance Session 1m
Console.WriteLine("\n[5/5] Executando Sessão de Desempenho 1m...");
var p60Sw = Stopwatch.StartNew();
var p60 = await perfRunner.RunAsync(PerformanceSessionPreset.Minute1, hwSnapshot, new Progress<PerformanceProgress>(p =>
{
    Console.Write($"\r  Amostras: {p.SamplesCollected}/{p.ExpectedSamples} | Decorrido: {p.Elapsed.TotalSeconds:F1}s / {p.Requested.TotalSeconds:F0}s      ");
}), CancellationToken.None);
p60Sw.Stop();
Console.WriteLine($"\n  Sessão 1m concluída em {p60Sw.Elapsed.TotalSeconds:F2}s.");
await store.SaveSessionAsync(p60);
Console.WriteLine($"  Sessão 1m salva com ID: {p60.Id}");

void ReportSession(string label, PerformanceSession s)
{
    Console.WriteLine($"\n--- DETALHES DA SESSÃO: {label} ---");
    Console.WriteLine($"  ID: {s.Id}");
    Console.WriteLine($"  Preset: {s.Preset}");
    Console.WriteLine($"  Status Legado: {s.Status}");
    Console.WriteLine($"  ExecutionStatus: {s.Coverage?.Execution}");
    Console.WriteLine($"  CoverageStatus: {s.Coverage?.Status}");
    var nominal = s.Preset switch
    {
        PerformanceSessionPreset.Seconds30 => "30s",
        PerformanceSessionPreset.Minute1 => "60s",
        _ => "180s"
    };
    Console.WriteLine($"  Duração Nominal: {nominal}");
    Console.WriteLine($"  Duração Real: {s.Duration.TotalSeconds:F3}s");
    Console.WriteLine($"  Total Amostras: {s.Samples.Count}");
    Console.WriteLine($"  Amostras Esperadas: {s.Coverage?.ExpectedSamples}");
    Console.WriteLine($"  Amostras Coletadas: {s.Coverage?.CollectedSamples}");
    Console.WriteLine($"  Oportunidades Perdidas: {s.Coverage?.MissedOpportunities}");
    Console.WriteLine($"  Limite de Processos Atingido: {s.Coverage?.ProcessLimitReached}");
    Console.WriteLine($"  Limite de Armazenamento Atingido: {s.Coverage?.StorageLimitReached}");
    
    // Intervalos e Jitter
    var intervals = new List<double>();
    for (int i = 1; i < s.Samples.Count; i++)
    {
        var dt = (s.Samples[i].ElapsedSeconds - s.Samples[i - 1].ElapsedSeconds);
        intervals.Add(dt);
    }
    if (intervals.Count > 0)
    {
        var meanInt = intervals.Average();
        var maxInt = intervals.Max();
        var minInt = intervals.Min();
        var stdDev = Math.Sqrt(intervals.Select(v => Math.Pow(v - meanInt, 2)).Average());
        Console.WriteLine($"  Intervalo Médio: {meanInt:F3}s (mín: {minInt:F3}s, máx: {maxInt:F3}s, desvio/jitter: {stdDev:F4}s)");
    }

    Console.WriteLine("  Fontes Executadas:");
    foreach (var src in s.Sources)
    {
        Console.WriteLine($"    - {src.CollectorId} v{src.CollectorVersion}: {src.Status} (Amostras lidas: {src.SamplesRead}, Limite: {src.LimitHit}, Duração: {src.Duration.TotalSeconds:F2}s, Média: {src.MeanReadMilliseconds:F1}ms, Máx: {src.MaxReadMilliseconds:F1}ms)");
    }

    Console.WriteLine("  Limitações / Contadores Ausentes:");
    if (s.Coverage?.Limitations.Count > 0)
    {
        foreach (var lim in s.Coverage.Limitations)
            Console.WriteLine($"    * {lim}");
    }
    else
    {
        Console.WriteLine("    (nenhuma limitação de cobertura)");
    }

    Console.WriteLine($"  Estatísticas CPU Total: Válidas={s.Statistics.CpuTotalPercent.ValidSamples}/{s.Statistics.CpuTotalPercent.TotalSamples}, Média={s.Statistics.CpuTotalPercent.Mean:F2}%, P95={s.Statistics.CpuTotalPercent.P95:F2}%, Máx={s.Statistics.CpuTotalPercent.Max:F2}%");
    Console.WriteLine($"  Estatísticas Memória Disponível: Válidas={s.Statistics.MemoryAvailablePercent.ValidSamples}/{s.Statistics.MemoryAvailablePercent.TotalSamples}, Média={s.Statistics.MemoryAvailablePercent.Mean:F2}%, Mín={s.Statistics.MemoryAvailablePercent.Min:F2}%");
    Console.WriteLine($"  Processos Acompanhados: {s.Statistics.Processes.Count}");
    Console.WriteLine($"  Dispositivos de Armazenamento: {s.Statistics.Storage.Count}");
    Console.WriteLine($"  Observações Determinísticas Geradas: {s.Observations.Count}");
    foreach (var obs in s.Observations)
    {
        Console.WriteLine($"    ! [{obs.RuleId}] {obs.Title}: {obs.Statement}");
        Console.WriteLine($"      Causalidade: {obs.Causality}");
    }
}

ReportSession("SESSÃO 30 SEGUNDOS", p30);
ReportSession("SESSÃO 1 MINUTO", p60);

// Database and File checks
Console.WriteLine("\n--- VERIFICAÇÃO DE PERSISTÊNCIA E INTEGRIDADE ---");
using var db = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(MemoryStore.DefaultRoot, "data", "sentinelcore.db"),
    Mode = SqliteOpenMode.ReadOnly,
    Pooling = false
}.ToString());
db.Open();

object? Scalar(string sql)
{
    using var cmd = db.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar();
}

var quickCheck = Scalar("PRAGMA quick_check");
var userVersion = Scalar("PRAGMA user_version");
var auditCount = Scalar("SELECT COUNT(*) FROM Audits");
var hwCount = Scalar("SELECT COUNT(*) FROM HardwareSnapshots");
var eventCount = Scalar("SELECT COUNT(*) FROM EventInvestigations");
var perfCount = Scalar("SELECT COUNT(*) FROM PerformanceSessions");
var obsCount = Scalar("SELECT COUNT(*) FROM PerformanceObservations");

Console.WriteLine($"  SQLite PRAGMA quick_check: {quickCheck}");
Console.WriteLine($"  SQLite user_version (Schema): {userVersion}");
Console.WriteLine($"  Total Audits no SQLite: {auditCount}");
Console.WriteLine($"  Total Hardware Snapshots no SQLite: {hwCount}");
Console.WriteLine($"  Total Event Investigations no SQLite: {eventCount}");
Console.WriteLine($"  Total Performance Sessions no SQLite: {perfCount}");
Console.WriteLine($"  Total Performance Observations no SQLite: {obsCount}");

// Verify reload through MemoryStore
Console.WriteLine("\n--- VERIFICAÇÃO DE REABERTURA (MemoryStore) ---");
var loadedSessions = await store.LoadSessionsAsync();
Console.WriteLine($"  Sessões recarregadas via MemoryStore: {loadedSessions.Sessions.Count}");
Console.WriteLine($"  Avisos de corrupção ou problemas: {loadedSessions.Warnings.Count}");
foreach (var w in loadedSessions.Warnings) Console.WriteLine($"    Aviso: {w}");

var loadedAudits = await store.LoadAsync();
Console.WriteLine($"  Auditorias recarregadas: {loadedAudits.Audits.Count}, Avisos: {loadedAudits.Warnings.Count}");

var loadedHw = await store.LoadSnapshotsAsync();
Console.WriteLine($"  Hardware recarregado: {loadedHw.Snapshots.Count}, Avisos: {loadedHw.Warnings.Count}");

var loadedEv = await store.LoadInvestigationsAsync();
Console.WriteLine($"  Eventos recarregados: {loadedEv.Investigations.Count}, Avisos: {loadedEv.Warnings.Count}");

Console.WriteLine("\nVALIDAÇÃO REAL CONCLUÍDA COM SUCESSO.");
