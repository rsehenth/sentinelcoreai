using SentinelCore.Domain;

namespace SentinelCore.Application;

public static class SafetyPresentation
{
    public static SafeOperationPresentation Build(SimulationResult simulation, string? resultSummary = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var recovery = simulation.Operation.Protection.RequiresFullTarget
            ? "Recuperação não representável na T1."
            : "Rollback pelo estado observado, com detecção de conflito.";
        return new SafeOperationPresentation(
            simulation.Operation.Id,
            simulation.Operation.DisplayName,
            simulation.Operation.Risk,
            simulation.Consequences,
            simulation.Protections,
            simulation.Plan.PlanHash,
            simulation.CanExecute,
            ["Recusar", "Autorizar plano simulado", "Solicitar recuperação após execução"],
            resultSummary ?? "Nenhum efeito real executado; resultado depende de autorização explícita.",
            recovery);
    }
}
