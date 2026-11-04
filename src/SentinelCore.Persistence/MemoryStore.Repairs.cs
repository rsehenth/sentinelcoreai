using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed partial class MemoryStore
{
    private string RepairDirectory(Guid id) => Path.Combine(root, "memory", "repairs", $"{id:D}");
    private string RepairIntentPath(Guid id) => Path.Combine(RepairDirectory(id), "intent.md");
    private string RepairRecordPath(Guid id) => Path.Combine(RepairDirectory(id), "record.md");
    public string GetRepairReportPath(Guid repairId) =>
        Path.Combine(root, "reports", "repairs", $"{repairId:D}.md");

    public Task SaveRepairIntentAsync(RepairIntent intent,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateRepairIntent(intent);
        EnsureDirectories();
        using var storeLock = Lock();
        EnsureNoOtherHistoryForRepair(intent.Id);
        var json = CanonicalJson(intent);
        var path = RepairIntentPath(intent.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = ReadRepairIntent(path).Intent;
            if (CanonicalJson(existing) != json)
                throw new InvalidDataException("Intenção de reparo já existe com conteúdo diferente; histórico preservado.");
            return;
        }
        var document = RecordEnvelope.Wrap(RepairReport.RenderIntent(intent), json);
        if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Intenção de reparo excede 4 MiB e não pode ser persistida.");
        RecordEnvelope.AtomicWrite(path, document, overwrite: false);
    }, cancellationToken);

    public Task<RepairRecord> SaveRepairRecordAsync(RepairRecord record,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateRepairRecord(record);
        EnsureDirectories();
        using var storeLock = Lock();
        var intentPath = RepairIntentPath(record.Id);
        if (!File.Exists(intentPath))
            throw new InvalidDataException("Registro final sem intenção durável; a gravação é recusada.");
        var intent = ReadRepairIntent(intentPath).Intent;
        if (!MatchesIntent(record, intent))
            throw new InvalidDataException("Registro final não corresponde à intenção persistida.");
        EnsureNoOtherHistoryForRepair(record.Id);
        var json = CanonicalJson(record);
        var path = RepairRecordPath(record.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = ReadRepairRecord(path).Record;
            if (CanonicalJson(existing) != json)
                throw new InvalidDataException("Registro de reparo já existe com conteúdo diferente; histórico preservado.");
            using var existingDatabase = OpenDatabase([]);
            var existingHistory = ReconcileRepairs(existingDatabase, [], CancellationToken.None);
            WriteRepairReport(existing);
            WriteRepairProjection(existingHistory);
            return existing;
        }

        var document = RecordEnvelope.Wrap(RepairReport.Render(record), json);
        if (Encoding.UTF8.GetByteCount(document) > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Registro de reparo excede 4 MiB e não pode ser persistido.");
        RecordEnvelope.AtomicWrite(path, document, overwrite: false);

        using var database = OpenDatabase([]);
        var history = ReconcileRepairs(database, [], CancellationToken.None);
        WriteRepairReport(record);
        WriteRepairProjection(history);
        if (!history.Records.Any(item => item.Id == record.Id) || !File.Exists(GetRepairReportPath(record.Id)))
            throw new IOException("A gravação do reparo não foi concluída; o registro durável será recuperado na próxima tentativa.");
        return record;
    }, cancellationToken);

    public Task<RepairHistory> LoadRepairsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureDirectories();
        using var storeLock = Lock();
        var warnings = new List<string>();
        using var database = OpenDatabase(warnings);
        var history = ReconcileRepairs(database, warnings, cancellationToken);
        WriteRepairProjection(history);
        if (warnings.Count > 0)
            File.AppendAllText(Path.Combine(root, "logs", "storage.log"),
                $"{DateTimeOffset.UtcNow:O}\n{string.Join("\n", warnings)}\n", Encoding.UTF8);
        return history;
    }, cancellationToken);

    public async Task<RepairRecord?> FindRepairAsync(Guid repairId,
        CancellationToken cancellationToken = default)
    {
        var history = await LoadRepairsAsync(cancellationToken);
        return history.Records.FirstOrDefault(item => item.Id == repairId);
    }

    private void EnsureNoOtherHistoryForRepair(Guid id)
    {
        if (File.Exists(Path.Combine(root, "memory", "audits", "basic", $"{id:D}.md")) ||
            File.Exists(Path.Combine(root, "memory", "audits", "full", $"{id:D}.md")) ||
            File.Exists(HardwareRecordPath(id)) || File.Exists(EventRecordPath(id)) ||
            File.Exists(PerformanceRecordPath(id)) || File.Exists(NetworkRecordPath(id)) ||
            File.Exists(GraphicsRecordPath(id)) || File.Exists(CorrelationRecordPath(id)) ||
            File.Exists(MonitoringRecordPath(id)) || Directory.Exists(MonitoringSegmentDirectory(id)) ||
            Directory.Exists(Path.Combine(root, "memory", "transactions", $"{id:D}")) ||
            Directory.Exists(Path.Combine(root, "memory", "debloat", $"{id:D}")))
            throw new InvalidDataException("ID já existe em outro histórico; histórico preservado.");
    }

    private RepairHistory ReconcileRepairs(SqliteConnection database, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var records = new List<RepairRecord>();
        var intents = new List<RepairIntent>();
        var hashes = new Dictionary<Guid, string>();
        var baseDirectory = Path.Combine(root, "memory", "repairs");
        if (Directory.Exists(baseDirectory))
        {
            foreach (var directory in Directory.GetDirectories(baseDirectory)
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!Guid.TryParse(name, out var id))
                {
                    warnings.Add($"Diretório de reparo {name} não possui GUID válido; preservado e excluído do índice.");
                    continue;
                }
                var recordPath = Path.Combine(directory, "record.md");
                var intentPath = Path.Combine(directory, "intent.md");
                try
                {
                    EnsureNoOtherHistoryForRepair(id);
                    if (File.Exists(recordPath))
                    {
                        if (!File.Exists(intentPath))
                            throw new InvalidDataException("Registro final sem intenção; o arquivo é preservado e excluído do índice.");
                        var intent = ReadRepairIntent(intentPath);
                        var record = ReadRepairRecord(recordPath);
                        if (intent.Intent.Id != id || record.Record.Id != id)
                            throw new InvalidDataException("Identidade do diretório diverge do registro.");
                        if (!MatchesIntent(record.Record, intent.Intent))
                            throw new InvalidDataException("Registro final não corresponde à intenção.");
                        records.Add(record.Record);
                        hashes[record.Record.Id] = record.Hash;
                    }
                    else if (File.Exists(intentPath))
                    {
                        var intent = ReadRepairIntent(intentPath);
                        if (intent.Intent.Id != id)
                            throw new InvalidDataException("Identidade do diretório diverge da intenção.");
                        intents.Add(intent.Intent);
                    }
                    else
                    {
                        warnings.Add($"Diretório de reparo {name} sem intent.md ou record.md; preservado.");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    warnings.Add($"Registro de reparo {name} inválido e excluído do índice: {error.Message}");
                }
            }
        }

        using (var transaction = database.BeginTransaction())
        {
            using var clear = database.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM RepairSteps; DELETE FROM Repairs;";
            clear.ExecuteNonQuery();
            transaction.Commit();
        }

        foreach (var record in records)
            IndexRepair(database, record, hashes.GetValueOrDefault(record.Id));

        return new RepairHistory(
            records.OrderByDescending(item => item.CompletedUtc).ThenBy(item => item.Id).ToList(),
            intents.OrderByDescending(item => item.CreatedAtUtc).ThenBy(item => item.Id).ToList(),
            warnings);
    }

    private void IndexRepair(SqliteConnection database, RepairRecord record, string? payloadHash)
    {
        using var transaction = database.BeginTransaction();
        using (var command = database.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Repairs
                    (Id, FormatVersion, SchemaVersion, SoftwareVersion, PlanId, OperationId,
                     OperationVersion, CatalogVersion, CatalogHash, DisplayName, Category, Kind, Risk,
                     ExecutionMode, Synthetic, VolumeId, InvocationDisplay, State, Result, Confidence,
                     ExitCode, ExitCodeText, StartedUtc, CompletedUtc, DurationSeconds, AuthorizationId,
                     ConfirmationId, EvidenceJson, OutputSummary, OutputBytes, OutputTruncated,
                     ResultSummary, RequiresRestart, RequiresIntervention, PossibleChangesJson,
                     LimitationsJson, PreflightJson, StepsJson, OutputJson, MarkdownPath, PayloadHash)
                VALUES ($id,$format,$schema,$software,$plan,$operation,$operationVersion,$catalogVersion,
                     $catalogHash,$display,$category,$kind,$risk,$mode,$synthetic,$volume,$invocation,$state,
                     $result,$confidence,$exitCode,$exitText,$start,$end,$duration,$authorization,
                     $confirmation,$evidence,$outputSummary,$outputBytes,$outputTruncated,$resultSummary,
                     $restart,$intervention,$possible,$limits,$preflight,$steps,$output,$path,$hash)
                """;
            command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
            command.Parameters.AddWithValue("$format", record.FormatVersion);
            command.Parameters.AddWithValue("$schema", record.SchemaVersion);
            command.Parameters.AddWithValue("$software", record.SoftwareVersion);
            command.Parameters.AddWithValue("$plan", record.PlanId.ToString("D"));
            command.Parameters.AddWithValue("$operation", record.OperationId);
            command.Parameters.AddWithValue("$operationVersion", record.OperationVersion);
            command.Parameters.AddWithValue("$catalogVersion", record.CatalogVersion);
            command.Parameters.AddWithValue("$catalogHash", record.CatalogContentHash);
            command.Parameters.AddWithValue("$display", record.DisplayName);
            command.Parameters.AddWithValue("$category", record.Category);
            command.Parameters.AddWithValue("$kind", record.Kind.ToString());
            command.Parameters.AddWithValue("$risk", record.Risk.ToString());
            command.Parameters.AddWithValue("$mode", record.ExecutionMode.ToString());
            command.Parameters.AddWithValue("$synthetic", record.IsSynthetic ? 1 : 0);
            command.Parameters.AddWithValue("$volume", (object?)record.Volume?.Id ?? DBNull.Value);
            command.Parameters.AddWithValue("$invocation", record.InvocationDisplay);
            command.Parameters.AddWithValue("$state", record.State.ToString());
            command.Parameters.AddWithValue("$result", record.Result.ToString());
            command.Parameters.AddWithValue("$confidence", record.Confidence.ToString());
            command.Parameters.AddWithValue("$exitCode", (object?)record.ExitCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$exitText", (object?)record.ExitCodeText ?? DBNull.Value);
            command.Parameters.AddWithValue("$start", record.StartedUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$end", record.CompletedUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$duration", record.Duration.TotalSeconds);
            command.Parameters.AddWithValue("$authorization", record.AuthorizationId.ToString("D"));
            command.Parameters.AddWithValue("$confirmation", record.ConfirmationId);
            command.Parameters.AddWithValue("$evidence",
                JsonSerializer.Serialize(record.Evidence, AuditJson.Options));
            command.Parameters.AddWithValue("$outputSummary", record.OutputSummary);
            command.Parameters.AddWithValue("$outputBytes", record.OutputBytes);
            command.Parameters.AddWithValue("$outputTruncated", record.OutputTruncated ? 1 : 0);
            command.Parameters.AddWithValue("$resultSummary", record.ResultSummary);
            command.Parameters.AddWithValue("$restart", record.RequiresRestart ? 1 : 0);
            command.Parameters.AddWithValue("$intervention", record.RequiresIntervention ? 1 : 0);
            command.Parameters.AddWithValue("$possible",
                JsonSerializer.Serialize(record.PossibleChanges, AuditJson.Options));
            command.Parameters.AddWithValue("$limits",
                JsonSerializer.Serialize(record.Limitations, AuditJson.Options));
            command.Parameters.AddWithValue("$preflight",
                JsonSerializer.Serialize(record.Preflight, AuditJson.Options));
            command.Parameters.AddWithValue("$steps",
                JsonSerializer.Serialize(record.Steps, AuditJson.Options));
            command.Parameters.AddWithValue("$output",
                JsonSerializer.Serialize(record.Output, AuditJson.Options));
            command.Parameters.AddWithValue("$path", Path.GetRelativePath(root, RepairRecordPath(record.Id)));
            command.Parameters.AddWithValue("$hash", payloadHash ?? RecordEnvelope.Hash(CanonicalJson(record)));
            command.ExecuteNonQuery();
        }

        foreach (var step in record.Steps.OrderBy(item => item.Sequence))
        {
            using var command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO RepairSteps (RepairId, Sequence, AtUtc, State, Detail, ExitCode)
                VALUES ($id,$sequence,$at,$state,$detail,$exit)
                """;
            command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
            command.Parameters.AddWithValue("$sequence", step.Sequence);
            command.Parameters.AddWithValue("$at", step.AtUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$state", step.State.ToString());
            command.Parameters.AddWithValue("$detail", step.Detail);
            command.Parameters.AddWithValue("$exit", (object?)step.ExitCode ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static (RepairIntent Intent, string Hash) ReadRepairIntent(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Intenção de reparo excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var intent = JsonSerializer.Deserialize<RepairIntent>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de intenção de reparo nulo.");
        ValidateRepairIntent(intent);
        return (intent, hash);
    }

    private static (RepairRecord Record, string Hash) ReadRepairRecord(string path)
    {
        if (new FileInfo(path).Length > RecordEnvelope.MaximumRecordBytes)
            throw new InvalidDataException("Registro de reparo excede 4 MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var json = RecordEnvelope.Unwrap(text);
        var hash = RecordEnvelope.Hash(json);
        var record = JsonSerializer.Deserialize<RepairRecord>(json, AuditJson.Options)
            ?? throw new InvalidDataException("Payload de reparo nulo.");
        ValidateRepairRecord(record);
        return (record, hash);
    }

    private static void ValidateRepairIntent(RepairIntent intent)
    {
        if (intent is null) throw new InvalidDataException("Intenção de reparo nula.");
        if (intent.FormatVersion != 1 || intent.Id == Guid.Empty || intent.PlanId == Guid.Empty ||
            string.IsNullOrWhiteSpace(intent.OperationId) || intent.OperationVersion <= 0 ||
            string.IsNullOrWhiteSpace(intent.CatalogVersion) ||
            string.IsNullOrWhiteSpace(intent.CatalogContentHash) ||
            string.IsNullOrWhiteSpace(intent.DisplayName) || !Enum.IsDefined(intent.Kind) ||
            !Enum.IsDefined(intent.Risk) || !Enum.IsDefined(intent.ExecutionMode) ||
            intent.CreatedAtUtc.Offset != TimeSpan.Zero || intent.Plan is null ||
            intent.Limitations is null || string.IsNullOrWhiteSpace(intent.InvocationDisplay) ||
            string.IsNullOrWhiteSpace(intent.ConfirmationId) ||
            !SafetyText.IsValidConfirmation(intent.ConfirmationId) ||
            intent.AuthorizationId == Guid.Empty)
            throw new InvalidDataException("Intenção de reparo inválida ou versão incompatível.");
        if (intent.Volume is not null && !RepairVolumeIdPolicy.IsWellFormed(intent.Volume.Id))
            throw new InvalidDataException("Volume da intenção de reparo inválido.");
        var plan = intent.Plan;
        if (plan.PlanId != intent.PlanId || plan.ExecutionId != intent.Id ||
            !string.Equals(plan.OperationId, intent.OperationId, StringComparison.Ordinal) ||
            plan.OperationVersion != intent.OperationVersion ||
            !string.Equals(plan.CatalogVersion, intent.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(plan.CatalogContentHash, intent.CatalogContentHash, StringComparison.Ordinal) ||
            !string.Equals(plan.Volume?.Id, intent.Volume?.Id, StringComparison.Ordinal) ||
            !string.Equals(plan.Invocation.DisplayCommand, intent.InvocationDisplay, StringComparison.Ordinal) ||
            !plan.SimulationAllowed || plan.Preflight is null)
            throw new InvalidDataException("Intenção de reparo divergente do plano.");
        var hash = RepairHashing.PlanHash(plan.PlanId, plan.ExecutionId, plan.OperationId,
            plan.OperationVersion, plan.CatalogVersion, plan.CatalogContentHash, plan.Kind, plan.Risk,
            plan.Volume, plan.Invocation, plan.Preflight.ObservedStateHash);
        if (!string.Equals(plan.PlanHash, hash, StringComparison.Ordinal))
            throw new InvalidDataException("Hash do plano da intenção não confere.");
    }

    private static void ValidateRepairRecord(RepairRecord record)
    {
        if (record is null) throw new InvalidDataException("Registro de reparo nulo.");
        if (record.FormatVersion != 1 || record.Id == Guid.Empty || record.PlanId == Guid.Empty ||
            string.IsNullOrWhiteSpace(record.SchemaVersion) || string.IsNullOrWhiteSpace(record.SoftwareVersion) ||
            string.IsNullOrWhiteSpace(record.OperationId) || record.OperationVersion <= 0 ||
            string.IsNullOrWhiteSpace(record.CatalogVersion) ||
            string.IsNullOrWhiteSpace(record.CatalogContentHash) ||
            string.IsNullOrWhiteSpace(record.DisplayName) || string.IsNullOrWhiteSpace(record.Category) ||
            !Enum.IsDefined(record.Kind) || !Enum.IsDefined(record.Risk) ||
            !Enum.IsDefined(record.ExecutionMode) || !Enum.IsDefined(record.State) ||
            !Enum.IsDefined(record.Result) || !Enum.IsDefined(record.Confidence) ||
            record.StartedUtc.Offset != TimeSpan.Zero || record.CompletedUtc.Offset != TimeSpan.Zero ||
            record.StartedUtc > record.CompletedUtc || record.Duration < TimeSpan.Zero ||
            record.AuthorizationId == Guid.Empty || string.IsNullOrWhiteSpace(record.ConfirmationId) ||
            !SafetyText.IsValidConfirmation(record.ConfirmationId) || record.Preflight is null ||
            record.Evidence is null || record.Output is null || record.PossibleChanges is null ||
            record.Limitations is null || record.Steps is null ||
            string.IsNullOrWhiteSpace(record.InvocationDisplay) ||
            string.IsNullOrWhiteSpace(record.ResultSummary) ||
            string.IsNullOrWhiteSpace(record.OutputSummary))
            throw new InvalidDataException("Registro de reparo inválido ou versão incompatível.");
        if (record.Volume is not null && !RepairVolumeIdPolicy.IsWellFormed(record.Volume.Id))
            throw new InvalidDataException("Volume do registro de reparo inválido.");
        if (record.IsSynthetic != (record.ExecutionMode == RepairExecutionMode.Simulation))
            throw new InvalidDataException("Origem sintética diverge do modo de execução.");
        if (record.State == RepairState.Completed && record.ExitCode is null)
            throw new InvalidDataException("Registro concluído sem código de saída verificável.");
        if (record.State == RepairState.Cancelled && record.Result != RepairResultClassification.Cancelled)
            throw new InvalidDataException("Cancelamento sem resultado de cancelamento.");
        if (record.State == RepairState.Interrupted &&
            record.Result is not (RepairResultClassification.Interrupted or RepairResultClassification.OutputIncomplete))
            throw new InvalidDataException("Interrupção sem resultado de interrupção.");
        if (record.Result == RepairResultClassification.Cancelled && record.State != RepairState.Cancelled)
            throw new InvalidDataException("Resultado cancelado em estado não cancelado.");
        if (record.Result == RepairResultClassification.RepairCompleted && record.State != RepairState.Completed)
            throw new InvalidDataException("Reparação concluída em estado não concluído.");
        if (record.ExitCode is null && record.ExitCodeText is not null)
            throw new InvalidDataException("Texto de código de saída sem código.");
        if (record.ExitCode is { } exitCode && record.ExitCodeText is not null &&
            !string.Equals(record.ExitCodeText, RepairExitCodes.Display(exitCode), StringComparison.Ordinal))
            throw new InvalidDataException("Texto do código de saída diverge do valor.");
        if (record.Output.Count > RepairLimits.MaxOutputLines ||
            record.Output.Select((line, index) => line.Sequence == index + 1).Any(value => !value) ||
            record.Output.Any(line => line.Text is null ||
                line.Text.Length > RepairLimits.MaxOutputLineChars))
            throw new InvalidDataException("Saída do reparo fora dos limites ou com sequência inválida.");
        var bytes = record.Output.Sum(line => Encoding.UTF8.GetByteCount(line.Text));
        if (bytes != record.OutputBytes || bytes > RepairLimits.MaxOutputBytes)
            throw new InvalidDataException("Contagem de bytes da saída do reparo inconsistente.");
        if (!RepairStateMachine.Validate(record.Steps, out var reason))
            throw new InvalidDataException("Passos do reparo inválidos: " + reason);
        if (record.Steps[^1].State != record.State)
            throw new InvalidDataException("Estado final diverge do último passo do reparo.");
    }

    private static bool MatchesIntent(RepairRecord record, RepairIntent intent) =>
        record.Id == intent.Id && record.PlanId == intent.PlanId &&
        string.Equals(record.OperationId, intent.OperationId, StringComparison.Ordinal) &&
        record.OperationVersion == intent.OperationVersion &&
        string.Equals(record.CatalogVersion, intent.CatalogVersion, StringComparison.Ordinal) &&
        string.Equals(record.CatalogContentHash, intent.CatalogContentHash, StringComparison.Ordinal) &&
        string.Equals(record.DisplayName, intent.DisplayName, StringComparison.Ordinal) &&
        record.Kind == intent.Kind && record.Risk == intent.Risk &&
        record.ExecutionMode == intent.ExecutionMode && record.IsSynthetic == intent.IsSynthetic &&
        string.Equals(record.Volume?.Id, intent.Volume?.Id, StringComparison.Ordinal) &&
        string.Equals(record.InvocationDisplay, intent.InvocationDisplay, StringComparison.Ordinal) &&
        record.AuthorizationId == intent.AuthorizationId &&
        string.Equals(record.ConfirmationId, intent.ConfirmationId, StringComparison.Ordinal) &&
        record.StartedUtc >= intent.CreatedAtUtc &&
        string.Equals(record.Preflight.ObservedStateHash, intent.Plan.Preflight.ObservedStateHash,
            StringComparison.Ordinal);

    private void WriteRepairReport(RepairRecord record) =>
        RecordEnvelope.AtomicWrite(GetRepairReportPath(record.Id), RepairReport.Render(record),
            overwrite: true);

    private void WriteRepairProjection(RepairHistory history)
    {
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Central de reparos do Windows");
        text.AppendLine();
        text.AppendLine("Formato: 1");
        text.AppendLine();
        text.AppendLine("Projeção derivada dos registros Markdown imutáveis; não é fonte de verdade.");
        text.AppendLine("A execução real permanece desabilitada na distribuição. Registros sintéticos são identificados.");
        text.AppendLine();
        text.AppendLine("## Histórico");
        if (history.Records.Count == 0) text.AppendLine("Nenhum reparo registrado.");
        else
            foreach (var record in history.Records)
                text.AppendLine($"- [{record.Id:D}](repairs/{record.Id:D}/record.md) — {record.CompletedUtc:O} — " +
                    $"{record.DisplayName} — {record.State} — {record.Result}" +
                    (record.IsSynthetic ? " — fixture sintética" : string.Empty));
        text.AppendLine();
        text.AppendLine("## Intenções sem registro final");
        if (history.UnfinishedIntents.Count == 0) text.AppendLine("Nenhuma intenção pendente.");
        else
            foreach (var intent in history.UnfinishedIntents)
                text.AppendLine($"- [{intent.Id:D}](repairs/{intent.Id:D}/intent.md) — {intent.CreatedAtUtc:O} — " +
                    $"{intent.DisplayName} — intenção sem conclusão; tratada como interrompida.");
        RecordEnvelope.AtomicWrite(Path.Combine(root, "memory", "REPAIRS.md"), text.ToString(),
            overwrite: true);
    }

    private static string CanonicalJson(RepairIntent intent) =>
        JsonSerializer.Serialize(intent, AuditJson.Options).Replace("\r\n", "\n");

    private static string CanonicalJson(RepairRecord record) =>
        JsonSerializer.Serialize(record, AuditJson.Options).Replace("\r\n", "\n");
}
