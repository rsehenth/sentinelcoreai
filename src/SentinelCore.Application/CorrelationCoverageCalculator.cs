using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Calcula cobertura e status de uma investigação de correlação. É a única fonte da regra usada pelo runner e pelos
/// testes, evitando que uma cópia divergente nos testes aprove um comportamento que a produção não tem.
/// </summary>
public static class CorrelationCoverageCalculator
{
    public static CorrelationCoverage Build(CorrelationNormalizationResult normalized,
        CorrelationAnalysisResult analysis, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(analysis);
        var selected = normalized.Sources
            .Where(source => source.Status != CorrelationSourceStatus.Skipped).ToArray();
        var completed = selected.Count(source => source.Status == CorrelationSourceStatus.Completed);
        var partial = selected.Count(source => source.Status == CorrelationSourceStatus.Partial);
        var unavailable = selected.Count(source => source.Status == CorrelationSourceStatus.Unavailable);
        var cancelledSources = selected.Count(source => source.Status == CorrelationSourceStatus.Cancelled);
        var limitHit = normalized.LimitHit || analysis.LimitHit;
        var allUnavailable = selected.Length > 0 && unavailable == selected.Length;
        var complete = !cancelled && completed == selected.Length && !limitHit &&
            normalized.UndatedEvidence == 0;
        var status = cancelled
            ? completed > 0 ? CorrelationCoverageStatus.Partial : CorrelationCoverageStatus.Unavailable
            : allUnavailable ? CorrelationCoverageStatus.Unavailable
            : complete ? CorrelationCoverageStatus.Complete
            : CorrelationCoverageStatus.Partial;

        var limitations = new List<string>();
        if (partial > 0) limitations.Add($"{partial} fonte(s) parcial(is) limitaram a cobertura.");
        if (unavailable > 0) limitations.Add($"{unavailable} fonte(s) indisponível(is).");
        if (cancelledSources > 0) limitations.Add($"{cancelledSources} fonte(s) cancelada(s).");
        if (limitHit) limitations.Add(CorrelationGuidance.LimitLimitation);

        return new(status, selected.Length, completed, partial, unavailable, cancelledSources,
            normalized.EvidenceFound, normalized.Evidence.Count, normalized.MergedReferences,
            normalized.UndatedEvidence, limitHit, limitations);
    }
}
