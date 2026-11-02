using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed record CorrelationAnalysisResult(
    IReadOnlyList<CorrelationRelation> Relations,
    IReadOnlyList<CorrelationAnalysisNote> Notes,
    bool LimitHit);

/// <summary>
/// Regras determinísticas de correlação. Cada regra é identificável, versionada e limitada a uma janela própria;
/// nenhuma delas declara causa, defeito ou probabilidade. Ausência de condição é registrada como nota, nunca
/// como conclusão de normalidade.
/// </summary>
public sealed class CorrelationEngine
{
    private const string RuleVersion = CorrelationGuidance.RuleVersion;

    public CorrelationAnalysisResult Analyze(IReadOnlyList<CorrelationEvidence> evidence,
        IReadOnlyList<CorrelationSourceSummary> sources, CorrelationBootReference boot, EventQueryWindow window,
        CorrelationLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(boot);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        var relations = new List<CorrelationRelation>();
        var notes = new List<CorrelationAnalysisNote>();
        var pairsPerRule = new Dictionary<string, int>(StringComparer.Ordinal);
        var limitHit = false;
        var iterations = 0;

        void CheckCancellation()
        {
            if ((++iterations & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
        }

        bool Available(CorrelationSource source) => sources.Any(item =>
            item.Source == source && item.Status is CorrelationSourceStatus.Completed
                or CorrelationSourceStatus.Partial);

        bool Skipped(CorrelationSource source) => sources.Any(item =>
            item.Source == source && item.Status == CorrelationSourceStatus.Skipped);

        static string NotObservedDetail(CorrelationNoteReason reason) => reason switch
        {
            CorrelationNoteReason.NotSelected => "Fonte necessária não selecionada; a regra não foi avaliada.",
            CorrelationNoteReason.Unavailable => "Fonte necessária indisponível; a regra não foi avaliada.",
            CorrelationNoteReason.PreconditionNotObserved =>
                "Pré-condição não observada: não há evidência dos tipos exigidos pela regra.",
            _ => "Condição não observada dentro da janela desta regra."
        };

        var graphicsEvents = evidence.Where(item =>
            item.Kind == CorrelationEvidenceKind.EventLog && IsGraphicsGroup(item.GroupId)).ToArray();
        var graphicsWer = evidence.Where(item =>
            item.Kind == CorrelationEvidenceKind.WerReport && IsGraphicsGroup(item.GroupId)).ToArray();
        var applicationFailures = evidence.Where(item =>
            IsApplicationGroup(item.GroupId)).ToArray();
        var performanceObservations = evidence
            .Where(item => item.Kind == CorrelationEvidenceKind.PerformanceObservation).ToArray();
        var networkObservations = evidence
            .Where(item => item.Kind == CorrelationEvidenceKind.NetworkObservation).ToArray();
        var windowsEvents = evidence.Where(item => item.Kind == CorrelationEvidenceKind.EventLog).ToArray();
        var networkEvents = windowsEvents.Where(item => IsNetworkGroup(item.GroupId)).ToArray();

        AddTemporalRule(CorrelationGuidance.GraphicsWerRuleId, graphicsEvents, graphicsWer,
            limits.GraphicsWerWindow,
            "Evento gráfico e relatório WER próximos no tempo",
            (left, right, gap) =>
                $"O evento gráfico \"{left.Title}\" ({When(left)}) e o relatório WER \"{right.Title}\" " +
                $"({When(right)}) foram registrados com diferença de {Seconds(gap)}. A proximidade temporal " +
                "justifica investigar a região, mas não demonstra que o evento tenha gerado o relatório, a falha " +
                "ou um defeito.",
            "Par formado por evento gráfico observado e relatório WER dentro da janela específica desta regra.",
            _ => EvidenceSeverity.PotentialImpact,
            (left, right) => TemporalConfidence(left, right),
            [
                "A ordem dos registros pode refletir atraso de gravação, não a ordem dos acontecimentos.",
                "Relatórios WER podem ser gravados depois do evento e não identificam causa."
            ],
            () => Skipped(CorrelationSource.Events) && Skipped(CorrelationSource.Graphics)
                ? CorrelationNoteReason.NotSelected
                : !Available(CorrelationSource.Events) && !Available(CorrelationSource.Graphics)
                    ? CorrelationNoteReason.Unavailable
                    : graphicsEvents.Length == 0 || graphicsWer.Length == 0
                        ? CorrelationNoteReason.PreconditionNotObserved
                        : CorrelationNoteReason.ConditionNotObserved);

        AddTemporalRule(CorrelationGuidance.GraphicsApplicationRuleId, graphicsEvents, applicationFailures,
            limits.GraphicsApplicationWindow,
            "Evento gráfico e falha de aplicativo próximos no tempo",
            (left, right, gap) =>
                $"O evento gráfico \"{left.Title}\" ({When(left)}) e a falha de aplicativo \"{right.Title}\" " +
                $"({When(right)}) foram registrados com diferença de {Seconds(gap)}. A proximidade temporal não " +
                "atribui ao driver ou ao aplicativo a causa da falha.",
            "Par formado por evento gráfico e evidência de falha de aplicativo dentro da janela desta regra.",
            _ => EvidenceSeverity.PotentialImpact,
            (left, right) => TemporalConfidence(left, right),
            [
                "Falha de aplicativo pode ocorrer por motivos independentes do subsistema gráfico.",
                "A ordem dos registros não prova precedência causal."
            ],
            () => Skipped(CorrelationSource.Events) && Skipped(CorrelationSource.Graphics)
                ? CorrelationNoteReason.NotSelected
                : !Available(CorrelationSource.Events) && !Available(CorrelationSource.Graphics)
                    ? CorrelationNoteReason.Unavailable
                    : graphicsEvents.Length == 0 || applicationFailures.Length == 0
                        ? CorrelationNoteReason.PreconditionNotObserved
                        : CorrelationNoteReason.ConditionNotObserved);

        AddTemporalRule(CorrelationGuidance.PerformanceEventRuleId, performanceObservations, windowsEvents,
            limits.PerformanceEventWindow,
            "Observação de desempenho e evento do Windows sobrepostos no tempo",
            (left, right, gap) =>
                $"A observação de desempenho \"{left.Title}\" ({When(left)}) e o evento do Windows " +
                $"\"{right.Title}\" ({When(right)}) se sobrepõem ou ficam a {Seconds(gap)}. A sobreposição " +
                "temporal permite investigar a região; não demonstra que o desempenho tenha provocado o evento " +
                "nem o contrário.",
            "Par formado por observação medida em sessão de desempenho e evento do Windows dentro da janela desta regra.",
            _ => EvidenceSeverity.PotentialImpact,
            (left, right) => TemporalConfidence(left, right),
            [
                "Somente observações medidas em sessão participam; inventários estáticos não são medições de desempenho.",
                "A presença do próprio SentinelCore AI altera a medição de desempenho."
            ],
            () => Skipped(CorrelationSource.Performance) || Skipped(CorrelationSource.Events)
                ? CorrelationNoteReason.NotSelected
                : !Available(CorrelationSource.Performance) || !Available(CorrelationSource.Events)
                    ? CorrelationNoteReason.Unavailable
                    : performanceObservations.Length == 0 || windowsEvents.Length == 0
                        ? CorrelationNoteReason.PreconditionNotObserved
                        : CorrelationNoteReason.ConditionNotObserved);

        AddTemporalRule(CorrelationGuidance.NetworkEventRuleId, networkObservations, networkEvents,
            limits.NetworkEventWindow,
            "Observação de rede e evento de rede próximos no tempo",
            (left, right, gap) =>
                $"A observação de rede \"{left.Title}\" ({When(left)}) e o evento de rede \"{right.Title}\" " +
                $"({When(right)}) ficaram a {Seconds(gap)}. Uma conexão ou contagem observada não é causa de " +
                "falha, e a ausência de conexão não prova indisponibilidade da rede.",
            "Par formado por observação de rede e evento de categoria Rede dentro da janela desta regra.",
            _ => EvidenceSeverity.PotentialImpact,
            (left, right) => TemporalConfidence(left, right),
            [
                "Contagens de endpoints são instantâneos locais e não capturam tráfego.",
                "A associação não indica esgotamento, indisponibilidade ou causa do evento."
            ],
            () => Skipped(CorrelationSource.Network) || Skipped(CorrelationSource.Events)
                ? CorrelationNoteReason.NotSelected
                : !Available(CorrelationSource.Network) || !Available(CorrelationSource.Events)
                    ? CorrelationNoteReason.Unavailable
                    : networkObservations.Length == 0 || networkEvents.Length == 0
                        ? CorrelationNoteReason.PreconditionNotObserved
                        : CorrelationNoteReason.ConditionNotObserved);

        AddIdentityRule(CorrelationGuidance.ProcessIdentityRuleId, performanceObservations, networkObservations,
            "Observações de desempenho e rede com a mesma identidade de processo",
            (left, right) =>
                $"As observações \"{left.Title}\" e \"{right.Title}\" referenciam a mesma identidade de processo " +
                $"\"{left.IdentityKey}\" (PID + hora de criação) e possuem janelas compatíveis: sobrepostas ou " +
                $"separadas por até {limits.IdentityWindow.TotalSeconds:F0} s. Identidade compartilhada não é causa; " +
                "PID isolado não é usado como identidade.",
            "Identidade de processo comparada por PID + hora de criação, nunca apenas pelo número do PID.",
            [
                "PID pode ser reutilizado pelo Windows; a hora de criação é obrigatória para a igualdade.",
                "A identidade do processo não demonstra que ele tenha causado uma falha."
            ],
            () => Skipped(CorrelationSource.Performance) || Skipped(CorrelationSource.Network)
                ? CorrelationNoteReason.NotSelected
                : !Available(CorrelationSource.Performance) || !Available(CorrelationSource.Network)
                    ? CorrelationNoteReason.Unavailable
                    : performanceObservations.Length == 0 || networkObservations.Length == 0
                        ? CorrelationNoteReason.PreconditionNotObserved
                        : CorrelationNoteReason.ConditionNotObserved,
            () => !performanceObservations.Any(IsProcessIdentity) || !networkObservations.Any(IsProcessIdentity)
                ? CorrelationNoteReason.PreconditionNotObserved
                : CorrelationNoteReason.ConditionNotObserved);

        var hardwareDevices = evidence.Where(item =>
            item.Kind == CorrelationEvidenceKind.DeviceInventory && item.Source == CorrelationSource.Hardware).ToArray();
        var graphicsDevices = evidence.Where(item =>
            item.Kind == CorrelationEvidenceKind.DeviceInventory && item.Source == CorrelationSource.Graphics).ToArray();
        AddIdentityRule(CorrelationGuidance.DeviceIdentityRuleId, hardwareDevices, graphicsDevices,
            "Mesmo dispositivo referenciado por inventário e por investigação gráfica",
            (left, right) =>
                $"O inventário de hardware e a investigação gráfica referenciam a mesma identidade técnica de " +
                $"dispositivo \"{left.IdentityKey}\" ({left.Title}). A identidade técnica é compartilhada; uso " +
                "efetivo do dispositivo, integridade e causa permanecem não avaliados. Nome comercial isolado " +
                "nunca é usado como identidade.",
            "Identidade comparada pelo StableId técnico do dispositivo nas duas fontes.",
            [
                "Dois dispositivos com o mesmo nome comercial não são considerados idênticos.",
                "A presença do dispositivo no inventário não confirma que ele esteja em uso."
            ],
            () => Skipped(CorrelationSource.Hardware) && Skipped(CorrelationSource.Graphics)
                ? CorrelationNoteReason.NotSelected
                : !Available(CorrelationSource.Hardware) || !Available(CorrelationSource.Graphics)
                    ? CorrelationNoteReason.Unavailable
                    : hardwareDevices.Length == 0 || graphicsDevices.Length == 0
                        ? CorrelationNoteReason.PreconditionNotObserved
                        : CorrelationNoteReason.ConditionNotObserved,
            () => CorrelationNoteReason.ConditionNotObserved);

        AddSequenceRule(CorrelationGuidance.ChangeSequenceRuleId, evidence, limits, boot);
        var associationCount = relations.Count;
        if (associationCount == 0)
        {
            var insufficient = new CorrelationRelation(
                $"{CorrelationGuidance.InsufficientEvidenceRuleId}:{window.FromUtc.UtcDateTime:O}",
                CorrelationGuidance.InsufficientEvidenceRuleId, RuleVersion,
                CorrelationRelationKind.InsufficientEvidence,
                "Correlação não avaliável com as evidências disponíveis",
                $"{CorrelationGuidance.InsufficientEvidenceStatement}: nenhuma associação determinística foi " +
                "identificada nos registros históricos disponíveis para a janela. Isso não prova que não exista " +
                "relação: as fontes podem estar incompletas, desatualizadas ou sem horário confiável.",
                "Nenhuma regra produziu associação; o resultado permanece explícito em vez de uma relação artificial.",
                EvidenceSeverity.Unknown, EvidenceConfidence.Low, CausalityStatus.NotAssessed,
                [], window.FromUtc, window.ToUtc, null,
                [CorrelationGuidance.AbsenceLimitation, "Evidência insuficiente não é evidência de ausência."]);
            relations.Add(insufficient);
        }

        notes.Add(new(CorrelationGuidance.InsufficientEvidenceRuleId, RuleVersion, true,
            associationCount > 0
                ? $"{associationCount} associação(ões) identificada(s) por regras determinísticas."
                : CorrelationGuidance.InsufficientEvidenceStatement + ".",
            associationCount > 0 ? CorrelationNoteReason.Observed : CorrelationNoteReason.InsufficientEvidence));

        var ordered = relations
            .OrderBy(relation => relation.Kind)
            .ThenBy(relation => relation.RuleId, StringComparer.Ordinal)
            .ThenBy(relation => relation.WindowFromUtc)
            .ThenBy(relation => relation.Id, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length > limits.MaxRelations)
        {
            limitHit = true;
            ordered = ordered.Take(limits.MaxRelations).ToArray();
        }

        return new(ordered, notes, limitHit);

        void AddTemporalRule(string ruleId, IReadOnlyList<CorrelationEvidence> left,
            IReadOnlyList<CorrelationEvidence> right, TimeSpan span, string title,
            Func<CorrelationEvidence, CorrelationEvidence, TimeSpan, string> statement, string justification,
            Func<CorrelationEvidence, EvidenceSeverity> severity,
            Func<CorrelationEvidence, CorrelationEvidence, EvidenceConfidence> confidence,
            IReadOnlyList<string> extraLimitations,
            Func<CorrelationNoteReason> notObservedReason)
        {
            var produced = 0;
            foreach (var first in left)
            {
                CheckCancellation();
                foreach (var second in right)
                {
                    CheckCancellation();
                    if (first.Id == second.Id || !SameBootSession(first, second, boot)) continue;
                    var gap = Gap(first, second);
                    if (gap is null || gap > span) continue;
                    if (Count(ruleId) >= limits.MaxPairsPerRule) { limitHit = true; break; }
                    Emit(new CorrelationRelation(
                        $"{ruleId}:{first.Id}->{second.Id}", ruleId, RuleVersion,
                        CorrelationRelationKind.TemporalAssociation, title,
                        statement(first, second, gap.Value),
                        $"{justification} Janela da regra: {Seconds(span)}.",
                        Max(severity(first), severity(second)), confidence(first, second),
                        CausalityStatus.NotAssessed, [first.Id, second.Id],
                        Min(first, second), MaxEnd(first, second), gap.Value.TotalSeconds,
                        [
                            CorrelationGuidance.CausalityLimitation,
                            .. extraLimitations,
                            .. PrecisionLimitations(first, second)
                        ]));
                    produced++;
                }
            }

            var reason = produced > 0 ? CorrelationNoteReason.Observed : notObservedReason();
            notes.Add(new(ruleId, RuleVersion, produced > 0, produced > 0
                    ? $"{produced} par(es) dentro da janela de {Seconds(span)}."
                    : NotObservedDetail(reason),
                reason));
        }

        void AddIdentityRule(string ruleId, IReadOnlyList<CorrelationEvidence> left,
            IReadOnlyList<CorrelationEvidence> right, string title,
            Func<CorrelationEvidence, CorrelationEvidence, string> statement, string justification,
            IReadOnlyList<string> extraLimitations,
            Func<CorrelationNoteReason> unavailableReason,
            Func<CorrelationNoteReason> noMatchReason)
        {
            var produced = 0;
            foreach (var first in left)
            {
                CheckCancellation();
                if (first.IdentityKey is null) continue;
                foreach (var second in right)
                {
                    CheckCancellation();
                    if (second.IdentityKey is null ||
                        !string.Equals(first.IdentityKey, second.IdentityKey, StringComparison.Ordinal)) continue;
                    if (first.Id == second.Id || !SameBootSession(first, second, boot)) continue;
                    if (Gap(first, second) is not { } gap || gap > limits.IdentityWindow) continue;
                    if (Count(ruleId) >= limits.MaxPairsPerRule) { limitHit = true; break; }
                    Emit(new CorrelationRelation(
                        $"{ruleId}:{first.Id}->{second.Id}", ruleId, RuleVersion,
                        CorrelationRelationKind.IdentityAssociation, title, statement(first, second),
                        justification,
                        EvidenceSeverity.Informational, EvidenceConfidence.Medium, CausalityStatus.NotAssessed,
                        [first.Id, second.Id], Min(first, second), MaxEnd(first, second), gap.TotalSeconds,
                        [
                            CorrelationGuidance.CausalityLimitation,
                            .. extraLimitations,
                            "Identidade técnica compartilhada não prova uso simultâneo."
                        ]));
                    produced++;
                }
            }

            var reason = produced > 0 ? CorrelationNoteReason.Observed
                : left.Count == 0 || right.Count == 0
                    ? unavailableReason()
                    : noMatchReason();
            notes.Add(new(ruleId, RuleVersion, produced > 0, produced > 0
                    ? $"{produced} par(es) com identidade compatível."
                    : NotObservedDetail(reason),
                reason));
        }

        void AddSequenceRule(string ruleId, IReadOnlyList<CorrelationEvidence> all, CorrelationLimits limitOptions,
            CorrelationBootReference bootReference)
        {
            var changes = all.Where(item =>
                (item.Kind is CorrelationEvidenceKind.HardwareChange or CorrelationEvidenceKind.GraphicsChange) &&
                item.EffectiveTimestampUtc is not null).ToArray();
            var subsequent = all.Where(item =>
                item.Kind == CorrelationEvidenceKind.EventLog &&
                (IsGraphicsGroup(item.GroupId) || IsApplicationGroup(item.GroupId))).ToArray();
            var produced = 0;
            foreach (var change in changes)
            {
                CheckCancellation();
                var changeAt = change.EffectiveTimestampUtc!.Value;
                foreach (var observed in subsequent)
                {
                    CheckCancellation();
                    if (observed.EffectiveTimestampUtc is not { } at || at < changeAt) continue;
                    if (!SameBootSession(change, observed, bootReference)) continue;
                    var gap = at - changeAt;
                    if (gap > limitOptions.ChangeEventWindow) continue;
                    if (Count(ruleId) >= limitOptions.MaxPairsPerRule) { limitHit = true; break; }
                    Emit(new CorrelationRelation(
                        $"{ruleId}:{change.Id}->{observed.Id}", ruleId, RuleVersion,
                        CorrelationRelationKind.ObservedSequence,
                        "Alteração observada e evento posterior",
                        $"A alteração \"{change.Title}\" foi observada na coleta de {When(change)}; o evento " +
                        $"\"{observed.Title}\" foi registrado {Seconds(gap)} depois. O instante exato da alteração " +
                        "é desconhecido e a sequência observada não indica causa.",
                        "Alteração confirmada entre inventários comparáveis e evento posterior dentro da janela desta regra.",
                        EvidenceSeverity.Informational, EvidenceConfidence.Low, CausalityStatus.NotAssessed,
                        [change.Id, observed.Id], changeAt, at, gap.TotalSeconds,
                        [
                            CorrelationGuidance.CausalityLimitation,
                            "A alteração pode ter ocorrido antes da coleta que a observou; eventos anteriores a essa " +
                            "coleta não são atribuídos à mudança.",
                            "Sequência temporal não indica melhoria, regressão ou causa."
                        ]));
                    produced++;
                }
            }

            var reason = produced > 0 ? CorrelationNoteReason.Observed
                : Skipped(CorrelationSource.Events) &&
                  Skipped(CorrelationSource.Hardware) && Skipped(CorrelationSource.Graphics)
                    ? CorrelationNoteReason.NotSelected
                    : changes.Length == 0 ? CorrelationNoteReason.PreconditionNotObserved
                    : CorrelationNoteReason.ConditionNotObserved;
            notes.Add(new(ruleId, RuleVersion, produced > 0, produced > 0
                    ? $"{produced} sequência(s) observada(s) após alteração."
                    : "Nenhuma sequência entre alteração observada e evento posterior nesta janela.",
                reason));
        }

        int Count(string ruleId) => pairsPerRule.GetValueOrDefault(ruleId);

        void Emit(CorrelationRelation relation)
        {
            relations.Add(relation);
            pairsPerRule[relation.RuleId] = pairsPerRule.GetValueOrDefault(relation.RuleId) + 1;
        }
    }

    private static bool IsGraphicsGroup(string? groupId) =>
        groupId?.StartsWith("graphics:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsApplicationGroup(string? groupId) =>
        string.Equals(groupId, "applications:crash", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(groupId, "applications:hang", StringComparison.OrdinalIgnoreCase);

    private static bool IsNetworkGroup(string? groupId) =>
        groupId?.StartsWith("network:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsProcessIdentity(CorrelationEvidence evidence) =>
        evidence.IdentityKind == CorrelationIdentityKind.ProcessInstance && evidence.IdentityKey is not null;

    private static bool SameBootSession(CorrelationEvidence first, CorrelationEvidence second,
        CorrelationBootReference boot)
    {
        if (boot.LastBootUtc is not { } bootUtc) return true;
        if (first.EffectiveTimestampUtc is not { } left || second.EffectiveTimestampUtc is not { } right)
            return true;
        return left < bootUtc == right < bootUtc;
    }

    private static TimeSpan? Gap(CorrelationEvidence first, CorrelationEvidence second)
    {
        var firstStart = first.WindowFromUtc ?? first.EffectiveTimestampUtc;
        var firstEnd = first.WindowToUtc ?? first.EffectiveTimestampUtc;
        var secondStart = second.WindowFromUtc ?? second.EffectiveTimestampUtc;
        var secondEnd = second.WindowToUtc ?? second.EffectiveTimestampUtc;
        if (firstStart is null || firstEnd is null || secondStart is null || secondEnd is null) return null;
        if (firstEnd < secondStart) return secondStart.Value - firstEnd.Value;
        if (secondEnd < firstStart) return firstStart.Value - secondEnd.Value;
        return TimeSpan.Zero;
    }

    private static DateTimeOffset Min(CorrelationEvidence first, CorrelationEvidence second)
    {
        var left = first.WindowFromUtc ?? first.EffectiveTimestampUtc ?? DateTimeOffset.MinValue;
        var right = second.WindowFromUtc ?? second.EffectiveTimestampUtc ?? DateTimeOffset.MinValue;
        return left <= right ? left : right;
    }

    private static DateTimeOffset MaxEnd(CorrelationEvidence first, CorrelationEvidence second)
    {
        var left = first.EndTimestampUtc ?? first.EffectiveTimestampUtc ?? DateTimeOffset.MinValue;
        var right = second.EndTimestampUtc ?? second.EffectiveTimestampUtc ?? DateTimeOffset.MinValue;
        return left >= right ? left : right;
    }

    private static EvidenceSeverity Max(EvidenceSeverity first, EvidenceSeverity second) =>
        (EvidenceSeverity)Math.Max((int)first, (int)second);

    private static EvidenceConfidence TemporalConfidence(CorrelationEvidence first, CorrelationEvidence second) =>
        first.Precision == CorrelationTimestampPrecision.Exact &&
        second.Precision == CorrelationTimestampPrecision.Exact
            ? EvidenceConfidence.Medium
            : EvidenceConfidence.Low;

    private static IReadOnlyList<string> PrecisionLimitations(CorrelationEvidence first,
        CorrelationEvidence second)
    {
        var limitations = new List<string>();
        if (first.Precision != CorrelationTimestampPrecision.Exact)
            limitations.Add($"A evidência \"{first.Title}\" tem precisão {CorrelationGuidance.PrecisionName(first.Precision)}.");
        if (second.Precision != CorrelationTimestampPrecision.Exact)
            limitations.Add($"A evidência \"{second.Title}\" tem precisão {CorrelationGuidance.PrecisionName(second.Precision)}.");
        return limitations;
    }

    private static string When(CorrelationEvidence evidence)
    {
        if (evidence.WindowFromUtc is { } start && evidence.WindowToUtc is { } end)
            return $"entre {Utc(start)} e {Utc(end)}";
        return evidence.EffectiveTimestampUtc is { } at ? Utc(at) : "sem horário confiável";
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s";
}
