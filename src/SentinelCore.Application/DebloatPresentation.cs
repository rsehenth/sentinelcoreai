using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Labels that keep catalog presence, inventory observation and eligibility explicitly distinct.</summary>
public static class DebloatPresentation
{
    public const string CatalogOrigin = "Catálogo de aplicativos";
    public const string InventoryOrigin = "Inventário de aplicativos";

    public static string Presence(DebloatPresence value) => value switch
    {
        DebloatPresence.NotVerified => "Não verificado — inventário real indisponível.",
        DebloatPresence.PresentInSyntheticInventory => "Presente no inventário sintético.",
        DebloatPresence.AbsentInConsultedInventory => "Ausente no inventário efetivamente consultado.",
        DebloatPresence.IdentityDivergent => "Identidade divergente do catálogo.",
        DebloatPresence.Indeterminate => "Observação indeterminada — estado não confirmado.",
        _ => "Presença não reconhecida (bloqueado)."
    };

    public static string Origin(DebloatPresence value) =>
        value == DebloatPresence.NotVerified ? CatalogOrigin : InventoryOrigin;

    public static string Eligibility(DebloatEligibility value) => value switch
    {
        DebloatEligibility.SimulationAvailable => "Simulação disponível; execução real bloqueada.",
        DebloatEligibility.UnavailableForSimulation => "Indisponível para simulação; execução real bloqueada.",
        DebloatEligibility.ProtectedOrBlocked => "Protegido ou bloqueado; não elegível.",
        _ => "Elegibilidade não reconhecida (bloqueado)."
    };
}
