using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed record RepairOperationView(
    string OperationId,
    string DisplayName,
    string Category,
    string Description,
    RepairOperationKind Kind,
    string KindName,
    RiskLevel Risk,
    string RiskName,
    string OfficialCommand,
    bool RequiresElevation,
    bool RequiresRestart,
    bool MayChangeSystem,
    bool CanCancelSafely,
    string Availability,
    bool SimulationAllowed,
    bool RealExecutionEnabled,
    IReadOnlyList<RepairBlocker> Blockers,
    IReadOnlyList<string> Consequences,
    IReadOnlyList<string> PossibleChanges,
    IReadOnlyList<string> Limitations,
    string ExpectedResult,
    string DocumentationReference,
    string? DocumentedOnlyReason);

public sealed record RepairCenterView(
    IReadOnlyList<RepairOperationView> Operations,
    IReadOnlyList<RepairVolume> Volumes,
    IReadOnlyList<RepairOperationDescriptor> DocumentedOperations,
    RepairExecutionMode Mode,
    bool RealExecutionEnabled,
    string ModeText,
    string ModeDetail);

public sealed record RepairScenarioOption(RepairFixtureScenario Scenario, string Label);

public sealed record RepairCategoryGroup(string Category, IReadOnlyList<RepairOperationView> Operations);

/// <summary>
/// Fachada de apresentação do Repair Center. Não executa nada: preparar, autorizar e executar passam pelo
/// orquestrador, e a execução disponível é somente o adaptador sintético.
/// </summary>
public sealed class RepairManager
{
    private readonly IRepairCatalog catalog;
    private readonly RepairCenter center;
    private readonly IRepairVolumeProvider volumes;
    private readonly RepairExecutionMode mode;

    public RepairManager(IRepairCatalog catalog, RepairCenter center, IRepairVolumeProvider volumes,
        RepairExecutionMode mode = RepairExecutionMode.Simulation)
    {
        this.catalog = catalog;
        this.center = center;
        this.volumes = volumes;
        this.mode = mode;
    }

    public RepairExecutionMode Mode => mode;
    public bool RealExecutionEnabled => false;
    public bool RealExecutionCompiled => RealRepairBuildGate.RealExecutionCompiled;
    public bool RealExecutionImplemented => RealRepairBuildGate.RealExecutionImplemented;
    public IReadOnlyList<RepairOperationDescriptor> DocumentedOperations => catalog.DocumentedOnly;

    public async Task<RepairCenterView> LoadAsync(CancellationToken cancellationToken = default)
    {
        var available = await LoadVolumesAsync(cancellationToken);
        var views = new List<RepairOperationView>();
        foreach (var operation in catalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var representativeVolume = operation.RequiresVolume
                    ? available.FirstOrDefault(volume => volume.IsReady && volume.IsLocalFixed &&
                        (!operation.FixedArguments.Any(argument =>
                                string.Equals(argument, "/scan", StringComparison.OrdinalIgnoreCase)) ||
                         volume.SupportsChkdskScan))?.Id
                    : null;
                var preflight = await center.EvaluateAsync(operation.Id, representativeVolume,
                    cancellationToken);
                views.Add(BuildView(operation, preflight));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                views.Add(BuildFailedView(operation, error.Message));
            }
        }

        var modeText = mode == RepairExecutionMode.Simulation
            ? RepairGuidance.RealExecutionUnavailable
            : "Modo de execução real não implementado nesta fase.";
        var modeDetail = "Catálogo fechado com executáveis oficiais e argumentos fixos. " +
            "A execução real não é composta nesta distribuição; os cartões demonstram o fluxo com fixtures " +
            "sintéticas explicitamente marcadas. Nenhuma elevação é solicitada.";
        return new RepairCenterView(views, available, catalog.DocumentedOnly, mode, false, modeText,
            modeDetail);
    }

    public async Task<IReadOnlyList<RepairVolume>> LoadVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await volumes.GetVolumesAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return [];
        }
    }

    public Task<RepairPlan> PrepareAsync(string operationId, string? volumeId,
        CancellationToken cancellationToken = default) =>
        center.PrepareAsync(operationId, volumeId, cancellationToken);

    public RepairAuthorizationToken Authorize(RepairPlan plan, string confirmation) =>
        center.Authorize(plan, confirmation);

    public Task<RepairExecutionResult> ExecuteAsync(RepairPlan plan, RepairAuthorizationToken token,
        RepairFixtureScenario scenario, IProgress<RepairProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        center.ExecuteAsync(plan, token, scenario, progress, cancellationToken);

    public Task<RepairRecord> RetrySaveAsync(RepairRecord record,
        CancellationToken cancellationToken = default) =>
        center.SaveAsync(record, cancellationToken);

    public Task<RepairHistory> LoadHistoryAsync(CancellationToken cancellationToken = default) =>
        center.LoadHistoryAsync(cancellationToken);

    public string GetReportPath(Guid repairId) => center.GetReportPath(repairId);

    public static string BuildConfirmationText(RepairPlan plan) => RepairConfirmationPolicy.Build(plan);

    private static RepairOperationView BuildView(RepairOperationDescriptor operation,
        RepairPreflightResult preflight) =>
        new(operation.Id, operation.DisplayName, operation.Category, operation.Description, operation.Kind,
            operation.KindName, operation.Risk, operation.RiskName, operation.DisplayCommand,
            operation.RequiresElevation, operation.RequiresRestart, operation.MayChangeSystem,
            operation.CanCancelSafely, AvailabilityName(operation.Availability), preflight.SimulationAllowed,
            false, preflight.SimulationBlockers, operation.Consequences, operation.PossibleChanges,
            operation.Limitations.Concat(preflight.Limitations).Distinct().ToList(),
            operation.ExpectedResult, operation.DocumentationReference, operation.DocumentedOnlyReason);

    private static RepairOperationView BuildFailedView(RepairOperationDescriptor operation, string error) =>
        new(operation.Id, operation.DisplayName, operation.Category, operation.Description, operation.Kind,
            operation.KindName, operation.Risk, operation.RiskName, operation.DisplayCommand,
            operation.RequiresElevation, operation.RequiresRestart, operation.MayChangeSystem,
            operation.CanCancelSafely, AvailabilityName(operation.Availability), false, false,
            [RepairBlocker.ExecutionFailed], operation.Consequences, operation.PossibleChanges,
            operation.Limitations.Concat(["Leitura do preflight indisponível: " + error]).Distinct().ToList(),
            operation.ExpectedResult, operation.DocumentationReference, operation.DocumentedOnlyReason);

    private static string AvailabilityName(RepairAvailability availability) => availability switch
    {
        RepairAvailability.Available => "Disponível para simulação",
        RepairAvailability.DocumentedOnly => "Documentada, indisponível",
        _ => "Indisponível"
    };
}

public static class RepairPresentation
{
    public static IReadOnlyList<RepairCategoryGroup> GroupByCategory(
        IEnumerable<RepairOperationView> operations) =>
        operations.GroupBy(item => item.Category)
            .OrderBy(group => RepairCategories.Rank(group.Key))
            .Select(group => new RepairCategoryGroup(group.Key,
                group.OrderBy(item => item.OperationId, StringComparer.Ordinal).ToList()))
            .ToList();

    public static IReadOnlyList<RepairScenarioOption> Scenarios { get; } =
    [
        new(RepairFixtureScenario.SuccessNoIssues, "Sucesso — nenhum problema"),
        new(RepairFixtureScenario.SuccessRepairsCompleted, "Sucesso — reparação concluída"),
        new(RepairFixtureScenario.CorruptionFound, "Corrupção encontrada"),
        new(RepairFixtureScenario.CorruptionNotFound, "Corrupção não encontrada"),
        new(RepairFixtureScenario.RepairPartial, "Reparação parcial"),
        new(RepairFixtureScenario.RepairFailed, "Falha da ferramenta"),
        new(RepairFixtureScenario.AccessDenied, "Acesso negado"),
        new(RepairFixtureScenario.UnsupportedSystem, "Sistema incompatível"),
        new(RepairFixtureScenario.RepairSourceUnavailable, "Fonte de reparação ausente"),
        new(RepairFixtureScenario.RestartRequired, "Reinicialização necessária"),
        new(RepairFixtureScenario.VolumeUnavailable, "Volume indisponível"),
        new(RepairFixtureScenario.IncompatibleFileSystem, "Sistema de arquivos incompatível"),
        new(RepairFixtureScenario.IncompleteOutput, "Saída incompleta"),
        new(RepairFixtureScenario.Interrupted, "Interrupção"),
        new(RepairFixtureScenario.Cancelled, "Cancelamento"),
        new(RepairFixtureScenario.ToolError, "Erro da ferramenta"),
        new(RepairFixtureScenario.WinReEnabled, "WinRE habilitado"),
        new(RepairFixtureScenario.WinReDisabled, "WinRE desabilitado")
    ];

    public static RepairFixtureScenario DefaultScenario(RepairOperationDescriptor operation) =>
        operation.Tool switch
        {
            RepairTool.Reagentc => RepairFixtureScenario.WinReEnabled,
            _ when operation.Kind == RepairOperationKind.Repair => RepairFixtureScenario.SuccessRepairsCompleted,
            RepairTool.Dism => RepairFixtureScenario.CorruptionNotFound,
            _ => RepairFixtureScenario.SuccessNoIssues
        };

    public static string StateName(RepairState state) => state switch
    {
        RepairState.Planned => "Planejada",
        RepairState.PreflightPassed => "Preflight aprovado",
        RepairState.Authorized => "Autorizada",
        RepairState.Running => "Em execução",
        RepairState.Completed => "Concluída",
        RepairState.Failed => "Falhou",
        RepairState.Blocked => "Bloqueada",
        RepairState.Cancelled => "Cancelada",
        RepairState.Interrupted => "Interrompida",
        RepairState.Indeterminate => "Indeterminada",
        _ => state.ToString()
    };

    public static string ResultName(RepairResultClassification result) => result switch
    {
        RepairResultClassification.NotEvaluated => "Não avaliado",
        RepairResultClassification.NoIssuesReported => "Nenhum problema relatado",
        RepairResultClassification.NoCorruptionDetected => "Nenhuma corrupção detectada",
        RepairResultClassification.CorruptionDetected => "Corrupção detectada",
        RepairResultClassification.RepairCompleted => "Reparação relatada como concluída",
        RepairResultClassification.RepairPartial => "Reparação parcial",
        RepairResultClassification.RepairFailed => "Falha de reparação",
        RepairResultClassification.RepairSourceUnavailable => "Fonte de reparação indisponível",
        RepairResultClassification.AccessDenied => "Acesso negado",
        RepairResultClassification.UnsupportedSystem => "Sistema incompatível",
        RepairResultClassification.VolumeUnavailable => "Volume indisponível",
        RepairResultClassification.IncompatibleFileSystem => "Sistema de arquivos incompatível",
        RepairResultClassification.RestartRequired => "Reinicialização necessária",
        RepairResultClassification.OutputIncomplete => "Saída incompleta",
        RepairResultClassification.Interrupted => "Interrompido",
        RepairResultClassification.Cancelled => "Cancelado",
        RepairResultClassification.UnknownExitCode => "Código de saída não catalogado",
        RepairResultClassification.InformationRetrieved => "Informação recuperada",
        _ => result.ToString()
    };

    public static string ConfidenceName(RepairConfidence confidence) => confidence switch
    {
        RepairConfidence.Low => "Baixa",
        RepairConfidence.Medium => "Média",
        RepairConfidence.High => "Alta",
        _ => confidence.ToString()
    };

    public static string RiskName(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => "Baixo",
        RiskLevel.Medium => "Médio",
        RiskLevel.High => "Alto",
        RiskLevel.NotValidated => "Não validado",
        _ => risk.ToString()
    };

    public static string KindName(RepairOperationKind kind) =>
        kind == RepairOperationKind.Diagnostic ? "Diagnóstico" : "Reparação";

    public static string BlockerName(RepairBlocker blocker) => blocker switch
    {
        RepairBlocker.UnsupportedWindows => "Windows 11 obrigatório",
        RepairBlocker.OperationUnavailable => "Operação indisponível",
        RepairBlocker.RiskBlocked => "Risco bloqueado",
        RepairBlocker.RealExecutionDisabled => "Execução real desabilitada",
        RepairBlocker.ElevationBrokerUnavailable => "Broker de elevação indisponível",
        RepairBlocker.ToolNotFound => "Executável oficial não encontrado",
        RepairBlocker.ToolPathRejected => "Caminho do executável recusado",
        RepairBlocker.VolumeRequired => "Volume obrigatório",
        RepairBlocker.VolumeUnavailable => "Volume indisponível",
        RepairBlocker.VolumeNotLocal => "Volume não local",
        RepairBlocker.IncompatibleFileSystem => "Sistema de arquivos incompatível",
        RepairBlocker.CatalogMismatch => "Catálogo divergente",
        RepairBlocker.AuthorizationMissing => "Autorização ausente",
        RepairBlocker.AuthorizationExpired => "Autorização expirada",
        RepairBlocker.AuthorizationReused => "Autorização reutilizada",
        RepairBlocker.PlanMismatch => "Plano divergente",
        RepairBlocker.ConfirmationMismatch => "Confirmação divergente",
        RepairBlocker.ExecutionFailed => "Falha de execução",
        RepairBlocker.PersistenceFailed => "Falha de persistência",
        RepairBlocker.Cancelled => "Cancelado",
        RepairBlocker.Interrupted => "Interrompido",
        RepairBlocker.OutputIncomplete => "Saída incompleta",
        _ => blocker.ToString()
    };
}
