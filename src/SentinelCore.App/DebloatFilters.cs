using SentinelCore.Domain;

namespace SentinelCore.App;

// Canonical filter keys are independent of display language.
internal static class DebloatFilters
{
    public static bool Matches(DebloatAssessment assessment, string filter)
    {
        var package = assessment.Package;
        return filter switch
        {
            "Opcionais" => assessment.CanSimulate,
            "Baixo" => assessment.EffectiveRisk == RiskLevel.Low,
            "Médio" => assessment.EffectiveRisk == RiskLevel.Medium,
            "Alto" => assessment.EffectiveRisk == RiskLevel.High,
            "Não validados" => assessment.EffectiveRisk == RiskLevel.NotValidated,
            "Presentes no inventário" => assessment.Presence == DebloatPresence.PresentInSyntheticInventory,
            "Ausentes no inventário" => assessment.Presence == DebloatPresence.AbsentInConsultedInventory,
            "Não verificados" => assessment.Presence == DebloatPresence.NotVerified,
            "Protegidos" => assessment.Eligibility == DebloatEligibility.ProtectedOrBlocked,
            "Microsoft" => IsMicrosoft(assessment.CatalogEntry?.TechnicalName ?? package.TechnicalName),
            "Terceiros/OEM" => !IsMicrosoft(assessment.CatalogEntry?.TechnicalName ?? package.TechnicalName),
            _ => true
        };
    }

    private static bool IsMicrosoft(string technicalName) =>
        technicalName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        technicalName.StartsWith("MicrosoftCorporationII.", StringComparison.OrdinalIgnoreCase) ||
        technicalName.Equals("Clipchamp.Clipchamp", StringComparison.OrdinalIgnoreCase);

}
