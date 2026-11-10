using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SentinelCore.Domain;

/// <summary>
/// Contratos do R1 — Windows Repair Center. O catálogo é fechado e independente do catálogo de tweaks:
/// cada operação referencia um executável oficial e um conjunto fixo de argumentos. Não existe contrato para
/// comando livre, script, shell, caminho configurável ou execução solicitada por IA.
/// </summary>
public enum RepairTool { Sfc, Dism, Chkdsk, Reagentc }

public enum RepairOperationKind { Diagnostic, Repair }

public enum RepairAvailability { Available, Unavailable, DocumentedOnly }

public enum RepairState
{
    Planned,
    PreflightPassed,
    Authorized,
    Running,
    Completed,
    Failed,
    Blocked,
    Cancelled,
    Interrupted,
    Indeterminate
}

public enum RepairResultClassification
{
    NotEvaluated,
    NoIssuesReported,
    NoCorruptionDetected,
    CorruptionDetected,
    RepairCompleted,
    RepairPartial,
    RepairFailed,
    RepairSourceUnavailable,
    AccessDenied,
    UnsupportedSystem,
    VolumeUnavailable,
    IncompatibleFileSystem,
    RestartRequired,
    OutputIncomplete,
    Interrupted,
    Cancelled,
    UnknownExitCode,
    InformationRetrieved,
    ElevationDeclined,
    StartFailed,
    BrokerRefused,
    ExecutionConflict
}

public enum RepairConfidence { Low, Medium, High }

public enum RepairExecutionMode { Simulation, Real }

public enum RepairBrokerStatus
{
    Refused, Conflict, ElevationDeclined, CancelledBeforeStart, StartDenied, StartFailed,
    ProcessExited, Indeterminate
}

public sealed record RepairNativeExecution(RepairBrokerStatus Status, bool ElevationRequested,
    DateTimeOffset? ProcessStartedUtc, DateTimeOffset? ProcessExitedUtc);

public enum RepairBlocker
{
    None,
    UnsupportedWindows,
    OperationUnavailable,
    RiskBlocked,
    RealExecutionDisabled,
    ElevationBrokerUnavailable,
    ToolNotFound,
    ToolPathRejected,
    VolumeRequired,
    VolumeUnavailable,
    VolumeNotLocal,
    IncompatibleFileSystem,
    CatalogMismatch,
    AuthorizationMissing,
    AuthorizationExpired,
    AuthorizationReused,
    PlanMismatch,
    ConfirmationMismatch,
    ExecutionFailed,
    PersistenceFailed,
    Cancelled,
    Interrupted,
    OutputIncomplete
}

public enum RepairFixtureScenario
{
    SuccessNoIssues,
    SuccessRepairsCompleted,
    CorruptionFound,
    CorruptionNotFound,
    RepairPartial,
    RepairFailed,
    AccessDenied,
    UnsupportedSystem,
    RepairSourceUnavailable,
    RestartRequired,
    VolumeUnavailable,
    IncompatibleFileSystem,
    IncompleteOutput,
    Interrupted,
    Cancelled,
    ToolError,
    WinReEnabled,
    WinReDisabled
}

public static class RepairToolNames
{
    public static string Executable(RepairTool tool) => tool switch
    {
        RepairTool.Sfc => "sfc.exe",
        RepairTool.Dism => "dism.exe",
        RepairTool.Chkdsk => "chkdsk.exe",
        RepairTool.Reagentc => "reagentc.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };

    public static string DisplayName(RepairTool tool) => tool switch
    {
        RepairTool.Sfc => "System File Checker",
        RepairTool.Dism => "Deployment Image Servicing and Management",
        RepairTool.Chkdsk => "Check Disk",
        RepairTool.Reagentc => "Windows Recovery Environment",
        _ => tool.ToString()
    };
}

public sealed record RepairVolume(
    string Id,
    string RootPath,
    string FileSystem,
    string DriveType,
    bool IsReady,
    long? TotalBytes,
    long? FreeBytes,
    bool IsLocalFixed,
    bool SupportsChkdskScan,
    string DisplayName,
    IReadOnlyList<string> Limitations)
{
    public bool IsNtfs => string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VolumeIdentity { get; init; }
}

/// <summary>
/// Aceita somente uma letra de unidade seguida de dois-pontos. Caminhos, mount points, UNC, curingas,
/// sequências de shell e texto livre são recusados antes de qualquer composição de argumento.
/// </summary>
public static class RepairVolumeIdPolicy
{
    public static bool TryNormalize(string? value, out string volumeId)
    {
        volumeId = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length != 2) return false;
        var letter = char.ToUpperInvariant(trimmed[0]);
        if (letter is < 'A' or > 'Z' || trimmed[1] != ':') return false;
        volumeId = $"{letter}:";
        return true;
    }

    public static bool IsWellFormed(string? value) => TryNormalize(value, out _);
}

public sealed record RepairOperationDescriptor(
    string Id,
    int Version,
    string Category,
    string DisplayName,
    string Description,
    RepairOperationKind Kind,
    RiskLevel Risk,
    RepairAvailability Availability,
    RepairTool Tool,
    IReadOnlyList<string> FixedArguments,
    bool RequiresVolume,
    bool RequiresElevation,
    bool RequiresRestart,
    bool MayChangeSystem,
    bool CanCancelSafely,
    bool SupportsProgress,
    bool IsDocumentedOnly,
    string? DocumentedOnlyReason,
    IReadOnlyList<string> Consequences,
    IReadOnlyList<string> PossibleChanges,
    IReadOnlyList<string> Limitations,
    string ExpectedResult,
    string DocumentationReference)
{
    public const string VolumePlaceholder = "{volume}";

    public string ExecutableName => RepairToolNames.Executable(Tool);

    public string DisplayCommand
    {
        get
        {
            var arguments = FixedArguments.Select(argument =>
                argument == VolumePlaceholder ? "<volume>" : argument);
            return $"{ExecutableName} {string.Join(" ", arguments)}".TrimEnd();
        }
    }

    public string KindName => Kind == RepairOperationKind.Diagnostic ? "Diagnóstico" : "Reparação";

    public string RiskName => Risk switch
    {
        RiskLevel.Low => "Baixo",
        RiskLevel.Medium => "Médio",
        RiskLevel.High => "Alto",
        RiskLevel.NotValidated => "Não validado",
        _ => Risk.ToString()
    };
}

public interface IRepairCatalog
{
    string Version { get; }
    string ContentHash { get; }
    IReadOnlyList<RepairOperationDescriptor> All { get; }
    IReadOnlyList<RepairOperationDescriptor> DocumentedOnly { get; }
    RepairOperationDescriptor Get(string operationId);
    bool TryGet(string operationId, out RepairOperationDescriptor? descriptor);
}

public sealed record RepairSystemProfile(
    string ProductName,
    int Build,
    bool IsWindows11,
    bool IsElevated,
    bool Is64Bit,
    string Source,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<string> Limitations);

public sealed record RepairToolLocation(
    RepairTool Tool,
    bool Available,
    string? Path,
    string? FileName,
    string? Error,
    IReadOnlyList<string> Limitations)
{
    public static RepairToolLocation Missing(RepairTool tool, string error) =>
        new(tool, false, null, null, error, [error]);
}

public sealed record RepairPreflightResult(
    bool SimulationAllowed,
    bool RealExecutionAllowed,
    IReadOnlyList<RepairBlocker> Blockers,
    IReadOnlyList<RepairBlocker> SimulationBlockers,
    IReadOnlyList<string> Details,
    RepairSystemProfile Profile,
    RepairVolume? Volume,
    RepairToolLocation Tool,
    string ObservedStateHash,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<string> Limitations)
{
    public bool Contains(RepairBlocker blocker) => Blockers.Contains(blocker);
}

public sealed record RepairInvocation(
    RepairTool Tool,
    string ExecutableName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments)
{
    public string DisplayCommand
    {
        get
        {
            var arguments = Arguments.Select(argument =>
                argument == RepairOperationDescriptor.VolumePlaceholder ? "<volume>" : argument);
            return $"{ExecutableName} {string.Join(" ", arguments)}".TrimEnd();
        }
    }
}

public sealed record RepairPlan(
    Guid PlanId,
    Guid ExecutionId,
    string OperationId,
    int OperationVersion,
    string CatalogVersion,
    string CatalogContentHash,
    string DisplayName,
    string Category,
    RepairOperationKind Kind,
    RiskLevel Risk,
    RepairAvailability Availability,
    RepairVolume? Volume,
    RepairInvocation Invocation,
    RepairPreflightResult Preflight,
    string PlanHash,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Consequences,
    IReadOnlyList<string> PossibleChanges,
    IReadOnlyList<string> Limitations)
{
    public bool SimulationAllowed => Preflight.SimulationAllowed;
    public bool RealExecutionAllowed => Preflight.RealExecutionAllowed;
}

public sealed record RepairAuthorizationToken(
    Guid AuthorizationId,
    Guid ExecutionId,
    Guid PlanId,
    string OperationId,
    int OperationVersion,
    string PlanHash,
    string ObservedStateHash,
    string? VolumeId,
    string UserConfirmationId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool Consumed);

public sealed record RepairAuthorizationValidation(bool Valid, RepairBlocker Blocker, string? Reason);

public sealed record RepairExecutionOptions(RepairFixtureScenario? SyntheticScenario);

public enum RepairOutputChannel { Stdout, Stderr }

public sealed record RepairOutputLine(long Sequence, string Text)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepairOutputChannel? Channel { get; init; }
}

public sealed record RepairProgress(string Phase, TimeSpan Elapsed, string? Detail)
{
    public RepairOutputLine? Output { get; init; }
    public bool ExecutionCommitted { get; init; }
    public DateTimeOffset? ProcessStartedUtc { get; init; }
}

public sealed record RepairExecutionRequest(
    Guid ExecutionId,
    Guid PlanId,
    RepairOperationDescriptor Operation,
    RepairInvocation Invocation,
    RepairVolume? Volume);

public sealed record RepairExecutionOutcome(
    bool Started,
    bool Completed,
    bool Cancelled,
    bool Interrupted,
    int? ExitCode,
    string? Error,
    bool OutputTruncated,
    IReadOnlyList<RepairOutputLine> Output,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan Duration,
    bool IsSynthetic,
    IReadOnlyList<string> Limitations)
{
    public RepairNativeExecution? Native { get; init; }
}

public sealed record RepairEvidence(
    bool CorruptionDetected,
    bool NoCorruptionDetected,
    bool RepairCompleted,
    bool RepairPartial,
    bool RepairSourceUnavailable,
    bool AccessDenied,
    bool RestartRequired,
    bool? WinReEnabled)
{
    public static RepairEvidence Empty { get; } =
        new(false, false, false, false, false, false, false, null);
}

public sealed record RepairStep(
    int Sequence,
    DateTimeOffset AtUtc,
    RepairState State,
    string Detail,
    int? ExitCode);

public static class RepairStateMachine
{
    public static bool IsAllowedTransition(RepairState from, RepairState to) => (from, to) switch
    {
        (RepairState.Planned, RepairState.PreflightPassed) => true,
        (RepairState.PreflightPassed, RepairState.Authorized) => true,
        (RepairState.Authorized, RepairState.Running) => true,
        (RepairState.Authorized, RepairState.Failed) => true,
        (RepairState.Authorized, RepairState.Indeterminate) => true,
        (RepairState.Running, RepairState.Completed) => true,
        (RepairState.Running, RepairState.Failed) => true,
        (RepairState.Running, RepairState.Cancelled) => true,
        (RepairState.Running, RepairState.Interrupted) => true,
        (RepairState.Running, RepairState.Indeterminate) => true,
        (RepairState.Planned, RepairState.Blocked) => true,
        (RepairState.PreflightPassed, RepairState.Blocked) => true,
        (RepairState.Authorized, RepairState.Blocked) => true,
        (RepairState.Planned, RepairState.Cancelled) => true,
        (RepairState.PreflightPassed, RepairState.Cancelled) => true,
        (RepairState.Authorized, RepairState.Cancelled) => true,
        _ => false
    };

    public static bool Validate(IReadOnlyList<RepairStep> steps, out string reason)
    {
        if (steps.Count == 0)
        {
            reason = "Nenhum passo registrado.";
            return false;
        }
        if (steps.Select(step => step.Sequence).Distinct().Count() != steps.Count ||
            steps.Select((step, index) => step.Sequence == index + 1).Any(value => !value))
        {
            reason = "Sequência de passos inválida.";
            return false;
        }
        if (steps[0].State != RepairState.Planned)
        {
            reason = "A execução deve iniciar em Planned.";
            return false;
        }
        for (var index = 1; index < steps.Count; index++)
        {
            if (!IsAllowedTransition(steps[index - 1].State, steps[index].State))
            {
                reason = $"Transição inválida: {steps[index - 1].State} → {steps[index].State}.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
}

public sealed record RepairRecord(
    int FormatVersion,
    Guid Id,
    string SchemaVersion,
    string SoftwareVersion,
    Guid PlanId,
    string OperationId,
    int OperationVersion,
    string CatalogVersion,
    string CatalogContentHash,
    string DisplayName,
    string Category,
    RepairOperationKind Kind,
    RiskLevel Risk,
    RepairExecutionMode ExecutionMode,
    bool IsSynthetic,
    RepairVolume? Volume,
    string InvocationDisplay,
    RepairState State,
    RepairResultClassification Result,
    RepairConfidence Confidence,
    int? ExitCode,
    string? ExitCodeText,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    Guid AuthorizationId,
    string ConfirmationId,
    RepairPreflightResult Preflight,
    RepairEvidence Evidence,
    IReadOnlyList<RepairOutputLine> Output,
    string OutputSummary,
    int OutputBytes,
    bool OutputTruncated,
    string ResultSummary,
    bool RequiresRestart,
    bool RequiresIntervention,
    IReadOnlyList<string> PossibleChanges,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<RepairStep> Steps)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepairNativeExecution? Native { get; init; }

    public bool VerifiedSuccess =>
        IsSynthetic && State == RepairState.Completed &&
        (Result is RepairResultClassification.NoIssuesReported
             or RepairResultClassification.NoCorruptionDetected
             or RepairResultClassification.InformationRetrieved ||
         Result == RepairResultClassification.RepairCompleted &&
         Confidence == RepairConfidence.High) &&
        !OutputTruncated;

    /// <summary>
    /// Ações adicionais derivadas dos campos persistidos. Não são executadas automaticamente e não alteram o
    /// registro; servem para orientar a leitura humana do resultado.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> AdditionalActions
    {
        get
        {
            var actions = new List<string>();
            if (RequiresRestart)
                actions.Add("Reiniciar o computador quando a ferramenta solicitar e confirmar o estado depois.");
            if (Result == RepairResultClassification.RepairSourceUnavailable)
                actions.Add("Avaliar uma fonte de reparação em etapa futura, com autorização específica.");
            if (Result == RepairResultClassification.AccessDenied)
                actions.Add(IsSynthetic
                    ? "A execução real exigirá elevação temporária por uma fronteira privilegiada futura."
                    : "Verificar o contexto de privilégios antes de uma nova autorização.");
            if (Native?.Status == RepairBrokerStatus.Indeterminate)
                actions.Add("Confirme se a ferramenta ainda está ativa e revise o resultado antes de autorizar outra operação.");
            if (Result == RepairResultClassification.OutputIncomplete || OutputTruncated)
                actions.Add("Revisar a saída incompleta antes de repetir; repetição automática não é permitida.");
            if (Result == RepairResultClassification.RepairPartial)
                actions.Add("Verificar novamente por outra etapa autorizada; a correção não é presumida.");
            if (RequiresIntervention || State is RepairState.Failed or RepairState.Interrupted
                or RepairState.Cancelled or RepairState.Indeterminate)
                actions.Add("Revisar as limitações e o histórico antes de qualquer nova tentativa.");
            actions.Add("Nenhuma ação é executada automaticamente; este registro é somente evidência.");
            return actions;
        }
    }
}

public sealed record RepairIntent(
    int FormatVersion,
    Guid Id,
    Guid PlanId,
    string OperationId,
    int OperationVersion,
    string CatalogVersion,
    string CatalogContentHash,
    string DisplayName,
    RepairOperationKind Kind,
    RiskLevel Risk,
    RepairExecutionMode ExecutionMode,
    bool IsSynthetic,
    RepairVolume? Volume,
    string InvocationDisplay,
    Guid AuthorizationId,
    string ConfirmationId,
    DateTimeOffset CreatedAtUtc,
    RepairPlan Plan,
    IReadOnlyList<string> Limitations);

public sealed record RepairHistory(
    IReadOnlyList<RepairRecord> Records,
    IReadOnlyList<RepairIntent> UnfinishedIntents,
    IReadOnlyList<string> Warnings);

public interface IRepairRecordStore
{
    string GetRepairReportPath(Guid repairId);
    Task SaveRepairIntentAsync(RepairIntent intent, CancellationToken cancellationToken = default);
    Task<RepairRecord> SaveRepairRecordAsync(RepairRecord record, CancellationToken cancellationToken = default);
    Task<RepairHistory> LoadRepairsAsync(CancellationToken cancellationToken = default);
    Task<RepairRecord?> FindRepairAsync(Guid repairId, CancellationToken cancellationToken = default);
}

public interface IRepairSystemProfileReader
{
    RepairSystemProfile Read();
}

public interface IRepairVolumeProvider
{
    Task<IReadOnlyList<RepairVolume>> GetVolumesAsync(CancellationToken cancellationToken = default);
}

public interface IRepairToolLocator
{
    RepairToolLocation Resolve(RepairTool tool);
}

public interface IRepairExecutor
{
    Task<RepairExecutionOutcome> ExecuteAsync(RepairExecutionRequest request,
        RepairExecutionOptions options, IProgress<RepairProgress>? progress,
        CancellationToken cancellationToken = default);
}

public interface IRepairBrokerExecutor
{
    Task<RepairExecutionOutcome> ExecuteAsync(RepairIntent persistedIntent, RepairAuthorizationToken consumedToken,
        IProgress<RepairProgress>? progress, CancellationToken cancellationToken);
}

public static class RepairExitCodes
{
    public const int DismSuccess = 0;
    public const int DismSourceNotFound = unchecked((int)0x800F081F);
    public const int DismSourceFilesNotFound = unchecked((int)0x800F0906);
    public const int DismAccessDenied = unchecked((int)0x80070005);
    public const int DismNotSupported = unchecked((int)0x80070032);
    public const int DismGenericFailure = unchecked((int)0x80004005);

    public static string Display(int code) =>
        code < 0 ? $"0x{unchecked((uint)code):X8}" : code.ToString();
}

public static class RepairLimits
{
    public const int MaxOutputLines = 200;
    public const int MaxOutputBytes = 32 * 1024;
    public const int MaxOutputLineChars = 512;
    public const int MaxOutputSummaryChars = 2000;
    public const int MaxArgumentCount = 6;
    public const int MaxArgumentChars = 64;
    public static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(2);
}

public static class RepairGuidance
{
    public const string RealExecutionUnavailable =
        "Execução real indisponível — modo de simulação.";
    public const string SyntheticDisclaimer =
        "FIXTURE SINTÉTICA — saída de teste, não é resultado do Windows real.";
    public const string NoRollback =
        "SFC, DISM e CHKDSK não possuem rollback equivalente ao dos Tweaks. Um ponto de restauração não garante recuperação.";
    public const string NoAutomaticRepair =
        "Nenhuma reparação é executada automaticamente após uma auditoria, correlação ou decisão de IA.";
    public const string LocalOnly =
        "A saída permanece local; nenhum dado é transmitido para servidores externos.";
    public const string CancellationLimitation =
        "Cancelamento solicitado não garante interrupção segura da ferramenta oficial.";
    public const string ElevationLimitation =
        "A execução real exigirá elevação temporária por uma fronteira privilegiada futura; esta versão não solicita elevação.";
    public const string OutputLocalization =
        "A interpretação usa código de saída e marcadores estruturados; mensagens localizadas são apenas evidência auxiliar.";
    public const string ExitCodeLimitation =
        "Código de saída zero não prova que todas as falhas foram corrigidas nem que a imagem está íntegra.";
    public const string OnlineChkdskLimitation =
        "A verificação online do NTFS não substitui uma verificação offline e pode depender do volume estar montado.";
    public const string AdvancedOperationsBlocked =
        "Operações avançadas de chkdsk, alterações de BCD, WinRE e redefinição do Windows estão documentadas, mas indisponíveis nesta fase.";
}

public static class RepairHashing
{
    public static string CatalogContentHash(IEnumerable<RepairOperationDescriptor> operations)
    {
        var builder = new StringBuilder();
        Append(builder, "repair-catalog-v1");
        foreach (var operation in operations.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Append(builder, operation.Id);
            Append(builder, operation.Version.ToString());
            Append(builder, operation.Category);
            Append(builder, operation.DisplayName);
            Append(builder, operation.Description);
            Append(builder, operation.Kind.ToString());
            Append(builder, operation.Risk.ToString());
            Append(builder, operation.Availability.ToString());
            Append(builder, operation.Tool.ToString());
            foreach (var argument in operation.FixedArguments) Append(builder, argument);
            Append(builder, operation.RequiresVolume ? "1" : "0");
            Append(builder, operation.RequiresElevation ? "1" : "0");
            Append(builder, operation.RequiresRestart ? "1" : "0");
            Append(builder, operation.MayChangeSystem ? "1" : "0");
            Append(builder, operation.CanCancelSafely ? "1" : "0");
            Append(builder, operation.SupportsProgress ? "1" : "0");
            Append(builder, operation.IsDocumentedOnly ? "1" : "0");
            Append(builder, operation.DocumentedOnlyReason);
            foreach (var consequence in operation.Consequences) Append(builder, consequence);
            foreach (var change in operation.PossibleChanges) Append(builder, change);
            foreach (var limitation in operation.Limitations) Append(builder, limitation);
            Append(builder, operation.ExpectedResult);
            Append(builder, operation.DocumentationReference);
        }
        return Sha256(builder.ToString());
    }

    public static string PreflightHash(RepairSystemProfile profile, RepairToolLocation tool,
        RepairVolume? volume, string catalogVersion, string catalogHash, string operationId,
        int operationVersion)
    {
        var builder = new StringBuilder();
        Append(builder, "repair-preflight-v1");
        Append(builder, profile.ProductName);
        Append(builder, profile.Build.ToString());
        Append(builder, profile.IsWindows11 ? "1" : "0");
        Append(builder, profile.IsElevated ? "1" : "0");
        Append(builder, profile.Is64Bit ? "1" : "0");
        Append(builder, tool.Tool.ToString());
        Append(builder, tool.Available ? "1" : "0");
        Append(builder, tool.Path);
        Append(builder, catalogVersion);
        Append(builder, catalogHash);
        Append(builder, operationId);
        Append(builder, operationVersion.ToString());
        if (volume is null) Append(builder, "no-volume");
        else
        {
            Append(builder, volume.Id);
            Append(builder, volume.RootPath);
            Append(builder, volume.FileSystem);
            Append(builder, volume.IsReady ? "1" : "0");
            Append(builder, volume.IsLocalFixed ? "1" : "0");
            Append(builder, volume.SupportsChkdskScan ? "1" : "0");
            if (volume.VolumeIdentity is not null) Append(builder, volume.VolumeIdentity);
        }
        return Sha256(builder.ToString());
    }

    public static string PlanHash(Guid planId, Guid executionId, string operationId,
        int operationVersion, string catalogVersion, string catalogHash, RepairOperationKind kind,
        RiskLevel risk, RepairVolume? volume, RepairInvocation invocation, string preflightHash)
    {
        var builder = new StringBuilder();
        Append(builder, "repair-plan-v1");
        Append(builder, planId.ToString("D"));
        Append(builder, executionId.ToString("D"));
        Append(builder, operationId);
        Append(builder, operationVersion.ToString());
        Append(builder, catalogVersion);
        Append(builder, catalogHash);
        Append(builder, kind.ToString());
        Append(builder, risk.ToString());
        Append(builder, volume?.Id);
        Append(builder, invocation.Tool.ToString());
        Append(builder, invocation.ExecutableName);
        Append(builder, invocation.ExecutablePath);
        foreach (var argument in invocation.Arguments) Append(builder, argument);
        Append(builder, preflightHash);
        return Sha256(builder.ToString());
    }

    public static string OutputHash(IEnumerable<RepairOutputLine> lines)
    {
        var builder = new StringBuilder();
        Append(builder, "repair-output-v1");
        foreach (var line in lines.OrderBy(item => item.Sequence))
        {
            Append(builder, line.Sequence.ToString());
            Append(builder, line.Text);
        }
        return Sha256(builder.ToString());
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
