using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Labels that keep catalog presence, inventory observation and eligibility explicitly distinct.</summary>
public static class DebloatPresentation
{
    public const string CatalogOrigin = "Catálogo de aplicativos";
    public const string InventoryOrigin = "Inventário de aplicativos";
    public const string NativeInventoryOrigin = "Inventário nativo Appx/MSIX";
    public const string NativeInventoryStatus =
        "Consulta ainda não executada. O inventário nativo é somente leitura.";
    public const string NativeInventoryStates =
        "Estados distintos: consulta não executada; concluída sem resultados; parcial; negada; interrompida; falha de enumeração; indisponível. A ausência só é afirmada após consulta completa.";

    public static string Scope(DebloatScope scope) => scope switch
    {
        DebloatScope.CurrentUser => "Usuário atual",
        DebloatScope.OtherUser => "Outros usuários",
        DebloatScope.Provisioned => "Provisionado para novas contas",
        _ => "Escopo indisponível"
    };

    public static string Category(DebloatNativeCategory category) => category switch
    {
        DebloatNativeCategory.Essential => "Essencial do Windows",
        DebloatNativeCategory.SystemComponent => "Componente de sistema",
        DebloatNativeCategory.Dependency => "Framework ou dependência",
        DebloatNativeCategory.Protected => "Protegido",
        DebloatNativeCategory.Optional => "Opcional",
        DebloatNativeCategory.ThirdParty => "Terceiros",
        _ => "Não classificado"
    };

    public static string Presence(DebloatPresence value) => value switch
    {
        DebloatPresence.NotVerified => "Não verificado — inventário real indisponível.",
        DebloatPresence.PresentInSyntheticInventory => "Presente no inventário sintético.",
        DebloatPresence.PresentInNativeInventory => "Presente no inventário nativo (não homologado).",
        DebloatPresence.AbsentInConsultedInventory => "Ausente no inventário efetivamente consultado.",
        DebloatPresence.IdentityDivergent => "Identidade divergente do catálogo.",
        DebloatPresence.Indeterminate => "Observação indeterminada — estado não confirmado.",
        _ => "Presença não reconhecida (bloqueado)."
    };

    public static string Origin(DebloatPresence value) => value switch
    {
        DebloatPresence.NotVerified => CatalogOrigin,
        DebloatPresence.PresentInNativeInventory => NativeInventoryOrigin,
        _ => InventoryOrigin
    };

    public static string Eligibility(DebloatEligibility value) => value switch
    {
        DebloatEligibility.SimulationAvailable => "Simulação disponível; execução real bloqueada.",
        DebloatEligibility.UnavailableForSimulation => "Indisponível para simulação; execução real bloqueada.",
        DebloatEligibility.ProtectedOrBlocked => "Protegido ou bloqueado; não elegível.",
        _ => "Elegibilidade não reconhecida (bloqueado)."
    };

    public static string InventorySource(DebloatInventoryOrigin value) => value switch
    {
        DebloatInventoryOrigin.Synthetic => InventoryOrigin,
        DebloatInventoryOrigin.NativeAppx => NativeInventoryOrigin,
        _ => "Origem de inventário não reconhecida (bloqueada)."
    };

    public static string QueryState(DebloatInventoryQueryState value) => value switch
    {
        DebloatInventoryQueryState.NotExecuted => "Consulta não executada — inventário não consultado.",
        DebloatInventoryQueryState.Completed => "Consulta concluída com resultados observados.",
        DebloatInventoryQueryState.CompletedNoResults => "Consulta concluída sem resultados — inventário vazio observado.",
        DebloatInventoryQueryState.Partial => "Consulta parcialmente concluída — ausência não confirmada.",
        DebloatInventoryQueryState.AccessDenied => "Consulta negada — acesso não autorizado; ausência não confirmada.",
        DebloatInventoryQueryState.Interrupted => "Consulta interrompida — resultado incompleto.",
        DebloatInventoryQueryState.Failed => "Falha de enumeração — nenhum resultado válido.",
        DebloatInventoryQueryState.Unavailable => "Consulta indisponível — API nativa ausente ou não suportada.",
        _ => "Estado de consulta não reconhecido (bloqueado)."
    };

    public static string Completeness(DebloatObservationCompleteness value) => value switch
    {
        DebloatObservationCompleteness.NotObserved => "Não observado.",
        DebloatObservationCompleteness.Complete => "Observação completa.",
        DebloatObservationCompleteness.Partial => "Observação parcial — ausência não confirmada.",
        DebloatObservationCompleteness.Unavailable => "Observação indisponível.",
        _ => "Completude não reconhecida (bloqueada)."
    };

    public static string CatalogMatch(DebloatCatalogMatchKind value) => value switch
    {
        DebloatCatalogMatchKind.Exact => "Correspondência exata com o catálogo documental.",
        DebloatCatalogMatchKind.NotInCatalog => DebloatNativeMessages.OutOfCatalog,
        DebloatCatalogMatchKind.Ambiguous => "Identidade ambígua — operação bloqueada.",
        DebloatCatalogMatchKind.CatalogOnly => "Somente catálogo — sem observação de inventário.",
        DebloatCatalogMatchKind.IdentityDivergent => "Identidade divergente — operação bloqueada.",
        _ => "Correspondência não reconhecida (bloqueada)."
    };
}
