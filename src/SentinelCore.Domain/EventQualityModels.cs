namespace SentinelCore.Domain;

public enum EventQualityState { NotApplicable, Verified, Affected, Undetermined }

public sealed record EventQualityEvidence(string ReportId, DateTimeOffset? EffectiveTimeUtc, string Observation);

public sealed record EventInvestigationQuality(
    Guid InvestigationId,
    string RuleId,
    string RuleVersion,
    EventQualityState State,
    string Summary,
    int DistinctReports,
    int ReportsOutsidePeriod,
    int ReportsWithoutEffectiveTime,
    DateTimeOffset PeriodFromUtc,
    DateTimeOffset PeriodToUtc,
    IReadOnlyList<EventQualityEvidence> Evidence,
    EvidenceConfidence Confidence,
    IReadOnlyList<string> Limitations);

public static class EventQualityRules
{
    public const string WerWindowRuleId = "EVT-WER-WINDOW";
    public const string WerWindowRuleVersion = "1.0";

    public static EventInvestigationQuality Evaluate(EventInvestigation investigation)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        var reports = investigation.Reports
            .GroupBy(report => report.ReportId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var evidence = new List<EventQualityEvidence>();
        var outside = new List<WerEvidence>();
        var unknown = new List<WerEvidence>();
        foreach (var report in reports)
        {
            var effective = report.EventTimeUtc ?? report.CreationTimeUtc;
            if (effective is null) { unknown.Add(report); continue; }
            if (effective < investigation.PeriodFromUtc || effective > investigation.PeriodToUtc)
            {
                outside.Add(report);
                evidence.Add(new(report.ReportId, effective, "Horário efetivo fora do período investigado."));
            }
        }
        var limitations = new List<string>();
        if (outside.Count > 20)
            limitations.Add($"Apenas 20 de {outside.Count} relatórios afetados são listados como evidência.");
        var state = reports.Length == 0 ? EventQualityState.NotApplicable
            : outside.Count > 0 ? EventQualityState.Affected
            : unknown.Count > 0 ? EventQualityState.Undetermined
            : EventQualityState.Verified;
        var confidence = state switch
        {
            EventQualityState.Affected => unknown.Count == 0 ? EvidenceConfidence.High : EvidenceConfidence.Medium,
            EventQualityState.Verified => EvidenceConfidence.High,
            EventQualityState.Undetermined => EvidenceConfidence.Low,
            _ => EvidenceConfidence.High
        };
        var summary = state switch
        {
            EventQualityState.Affected =>
                $"{outside.Count} de {reports.Length} relatórios WER distintos possuem horário efetivo fora do período investigado; " +
                "as contagens de relatórios podem estar superestimadas. Os eventos do log não são invalidados por esta regra.",
            EventQualityState.Verified =>
                $"Nenhum dos {reports.Length} relatórios WER distintos possui horário efetivo fora do período investigado.",
            EventQualityState.Undetermined =>
                $"{unknown.Count} de {reports.Length} relatórios WER não possuem horário efetivo; a janela não pôde ser confirmada.",
            _ => "A investigação não contém relatórios WER para avaliar."
        };
        return new(investigation.Id, WerWindowRuleId, WerWindowRuleVersion, state, summary,
            reports.Length, outside.Count, unknown.Count, investigation.PeriodFromUtc, investigation.PeriodToUtc,
            evidence.Take(20).ToArray(), confidence, limitations);
    }
}
