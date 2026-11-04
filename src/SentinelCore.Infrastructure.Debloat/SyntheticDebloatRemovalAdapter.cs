using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Debloat;

/// <summary>Entirely in-memory T3C observations and effects. No OS, package or process access.</summary>
public sealed class SyntheticDebloatRemovalAdapter(DebloatRemovalObservation observation)
    : IDebloatRemovalObservationSource, IDebloatRemovalExecutor
{
    public bool SyntheticOnly => true;
    public bool Available { get; set; } = true;
    public DebloatRemovalObservation Observation { get; set; } = DebloatRemovalHashing.Copy(observation);
    public int ExecutionCount { get; private set; }
    public Action? AfterStart { get; set; }
    public DebloatRemovalPostObservation? OverridePostObservation { get; set; }
    private DebloatRemovalRequest? executed;
    public DebloatRemovalRequest? LastRequest => executed;

    public Task<DebloatRemovalObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DebloatRemovalHashing.Copy(Observation));
    }

    public Task<DebloatRemovalReceipt> ExecuteAsync(DebloatRemovalRequest request,
        IProgress<DebloatRemovalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Available || !request.Synthetic || !Observation.Synthetic ||
            !Enum.IsDefined(request.Scenario) || !DebloatRemovalIdentityPolicy.IsValid(request.Package) ||
            request.Package != Observation.SelectedPackage || request.InventoryRevision != Observation.InventoryRevision)
            throw new InvalidOperationException("Requisição de fixture inválida.");
        ExecutionCount++;
        executed = request;
        var updates = new List<DebloatRemovalProgress>();
        void Report(DebloatRemovalPhase phase)
        {
            var update = new DebloatRemovalProgress(request.OperationId, phase, null,
                "FIXTURE T3C — sem percentual nativo observado.");
            updates.Add(update);
            try { progress?.Report(update); } catch { /* Presentation failure cannot interrupt an initiated effect. */ }
        }
        Report(DebloatRemovalPhase.Initial);
        Report(DebloatRemovalPhase.Started);
        AfterStart?.Invoke();
        Report(DebloatRemovalPhase.ProgressReported);
        var scenario = request.Scenario;
        var phase = scenario switch
        {
            DebloatRemovalScenario.Success or DebloatRemovalScenario.Unverifiable or
                DebloatRemovalScenario.PostVerificationFailed => DebloatRemovalPhase.Completed,
            DebloatRemovalScenario.AccessDenied => DebloatRemovalPhase.Failed,
            _ => DebloatRemovalPhase.Indeterminate
        };
        Report(phase);
        var error = scenario switch
        {
            DebloatRemovalScenario.AccessDenied => "Acesso negado — fixture.",
            DebloatRemovalScenario.PartialFailure => "Falha parcial após possível efeito indireto — fixture.",
            DebloatRemovalScenario.Interrupted => "Interrupção após início; cancelamento não confirmado — fixture.",
            DebloatRemovalScenario.Indeterminate => "Resultado indeterminado — fixture.",
            _ => null
        };
        return Task.FromResult(new DebloatRemovalReceipt(request.OperationId, request.Package, true,
            phase, scenario != DebloatRemovalScenario.AccessDenied,
            scenario == DebloatRemovalScenario.PartialFailure,
            phase == DebloatRemovalPhase.Completed ? 0 : unchecked((int)0x80004005),
            scenario == DebloatRemovalScenario.AccessDenied ? unchecked((int)0x80070005) : null,
            error, null, updates, DebloatRemovalMessages.Limitations));
    }

    public Task<DebloatRemovalPostObservation> VerifyAsync(DebloatRemovalRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OverridePostObservation is not null) return Task.FromResult(OverridePostObservation);
        var matched = executed == request;
        return Task.FromResult(new DebloatRemovalPostObservation(
            matched && request.Scenario != DebloatRemovalScenario.Unverifiable,
            matched && request.Scenario == DebloatRemovalScenario.Success,
            matched && request.Scenario == DebloatRemovalScenario.Success,
            "Verificação somente da fixture em memória; não descreve remoção ou arquivos do Windows."));
    }
}
