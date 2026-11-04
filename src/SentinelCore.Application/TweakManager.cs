using SentinelCore.Domain;

namespace SentinelCore.Application;

public enum TweakExecutionMode
{
    /// <summary>Somente adaptadores sintéticos; usado pelos testes automatizados.</summary>
    Validation,
    /// <summary>Adaptador HKCU real habilitado explicitamente para uma máquina de validação.</summary>
    RealControlled,
    /// <summary>Leitura real e simulação disponíveis; qualquer escrita permanece bloqueada.</summary>
    RealDisabled
}

public sealed record TweakOperationView(
    string OperationId,
    string DisplayName,
    string Category,
    string Description,
    RiskLevel Risk,
    string Compatibility,
    string CurrentState,
    string DesiredState,
    IReadOnlyList<string> Protections,
    bool CanExecute,
    bool RealExecutionEnabled,
    IReadOnlyList<BlockerCode> Blockers,
    IReadOnlyList<string> Limitations,
    string Availability,
    string? LoadError);

/// <summary>
/// Fachada de apresentação/orquestração da T2A. Não escreve no Windows: toda execução e recuperação passam
/// pelo Safety Engine e pelo <see cref="RecoveryService"/> da T1.
/// </summary>
public sealed class TweakManager
{
    private readonly IOperationCatalog catalog;
    private readonly SafetyEngine engine;
    private readonly RecoveryService recovery;
    private readonly ITransactionJournalStore journal;
    private readonly TweakExecutionMode mode;

    public TweakManager(IOperationCatalog catalog, SafetyEngine engine, RecoveryService recovery,
        ITransactionJournalStore journal, TweakExecutionMode mode)
    {
        this.catalog = catalog;
        this.engine = engine;
        this.recovery = recovery;
        this.journal = journal;
        this.mode = mode;
    }

    public TweakExecutionMode Mode => mode;
    public bool RealExecutionEnabled => mode == TweakExecutionMode.RealControlled;
    public IReadOnlyList<OperationDescriptor> Operations => catalog.All;

    public async Task<IReadOnlyList<TweakOperationView>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var views = new List<TweakOperationView>();
        foreach (var operation in catalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var simulation = await engine.SimulateAsync(operation.Id, cancellationToken);
                views.Add(BuildView(operation, simulation));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                views.Add(new TweakOperationView(operation.Id, operation.DisplayName, operation.Category,
                    string.Join(" ", operation.Consequences), operation.Risk, Compatibility(operation),
                    "indisponível", "indisponível", [], false, RealExecutionEnabled, [],
                    operation.Limitations,
                    "Estado atual não pôde ser lido com segurança.", error.Message));
            }
        }
        return views;
    }

    public Task<SimulationResult> SimulateAsync(string operationId,
        CancellationToken cancellationToken = default) =>
        engine.SimulateAsync(operationId, cancellationToken);

    public AuthorizationToken Authorize(SimulationResult simulation, string confirmationId) =>
        engine.Authorize(simulation, confirmationId);

    public Task<TransactionExecutionResult> ExecuteAsync(OperationPlan plan, AuthorizationToken token,
        CancellationToken cancellationToken = default) =>
        engine.ExecuteAsync(plan, token, cancellationToken);

    public Task<RecoveryPreview> PreviewRecoveryAsync(Guid transactionId,
        CancellationToken cancellationToken = default) =>
        recovery.PreviewAsync(transactionId, cancellationToken);

    public Task<RecoveryResult> RollbackAsync(Guid transactionId, RecoveryOptions options,
        CancellationToken cancellationToken = default) =>
        recovery.RollbackAsync(transactionId, options, cancellationToken);

    public async Task<IReadOnlyList<TransactionRecord>> LoadHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await journal.LoadTransactionsAsync(cancellationToken);
        return snapshot.Transactions
            .Where(item => catalog.TryGet(item.OperationId, out _))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToList();
    }

    public static string BuildConfirmationText(OperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var hash = plan.PlanHash.Length >= 12 ? plan.PlanHash[..12] : plan.PlanHash;
        return $"AUTORIZAR {plan.OperationId} {hash}";
    }

    public static string BuildRecoveryConfirmationText(Guid transactionId) =>
        $"RECUPERAR {transactionId:D}";

    private TweakOperationView BuildView(OperationDescriptor operation, SimulationResult simulation)
    {
        var plan = simulation.Plan;
        var current = plan.Changes.Count == 1
            ? VerificationEngine.Describe(plan.Changes[0].Before)
            : string.Join("; ", plan.Changes.Select(item =>
                $"{item.Target.ValueName}: {VerificationEngine.Describe(item.Before)}"));
        var desired = plan.Changes.Count == 1
            ? VerificationEngine.Describe(plan.Changes[0].After)
            : string.Join("; ", plan.Changes.Select(item =>
                $"{item.Target.ValueName}: {VerificationEngine.Describe(item.After)}"));

        var protections = new List<string>
        {
            "Backup do estado observado",
            "Simulação sem efeitos",
            "Autorização específica e de uso único",
            "Journal durável",
            "Verificação posterior"
        };
        if (operation.Protection.RequiresRestorePoint) protections.Add("Ponto de restauração");

        var availability = mode switch
        {
            TweakExecutionMode.RealDisabled => "Execução real desabilitada nesta distribuição; " +
                "validação em VM Windows 11 pendente.",
            _ when simulation.CanExecute => "Disponível para execução controlada em HKCU.",
            _ => "Indisponível: " + string.Join(", ", simulation.Blockers)
        };

        return new TweakOperationView(operation.Id, operation.DisplayName, operation.Category,
            string.Join(" ", operation.Consequences), operation.Risk, Compatibility(operation),
            current, desired, protections, simulation.CanExecute, RealExecutionEnabled,
            simulation.Blockers, simulation.Limitations, availability, null);
    }

    private static string Compatibility(OperationDescriptor operation) =>
        $"Windows 11 · build mínimo {operation.Compatibility.MinimumBuild}" +
        (operation.Compatibility.MaximumBuild is null ? "" : $" · máximo {operation.Compatibility.MaximumBuild}");
}

/// <summary>
/// Vincula a confirmação de recuperação à transação concreta, em vez de aceitar qualquer texto não vazio.
/// </summary>
public interface IRecoveryConfirmationPolicy
{
    bool IsValid(Guid transactionId, string? confirmation);
}

public sealed class TweakRecoveryConfirmationPolicy : IRecoveryConfirmationPolicy
{
    public bool IsValid(Guid transactionId, string? confirmation) =>
        SafetyText.IsValidConfirmation(confirmation) &&
        string.Equals(confirmation!.Trim(), TweakManager.BuildRecoveryConfirmationText(transactionId),
            StringComparison.Ordinal);
}

/// <summary>
/// Executor de composição usado quando a distribuição mantém a escrita real desabilitada. Ele existe para
/// que o Safety Engine continue disponível para leitura/simulação sem nunca chegar ao Registro.
/// </summary>
public sealed class DisabledOperationExecutor : IOperationExecutor
{
    public Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecutionOutcome.Failed(
            "Execução real desabilitada nesta distribuição; validação em VM Windows 11 pendente."));
    }
}
