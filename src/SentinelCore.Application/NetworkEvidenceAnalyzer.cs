using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Analisador determinístico de observações de rede. Cada regra é conservadora, sensível à
/// duração e à cobertura, e nunca atribui causa: <c>CausalityStatus</c> é sempre
/// <c>NotAssessed</c>. TIME_WAIT não é erro, contagem elevada não é esgotamento e um processo
/// com muitas conexões não é malicioso.
/// </summary>
public sealed class NetworkEvidenceAnalyzer
{
    private readonly NetworkRuleOptions options;

    public NetworkEvidenceAnalyzer(NetworkRuleOptions? options = null) =>
        this.options = options ?? NetworkRuleOptions.Default;

    public sealed record Result(
        IReadOnlyList<NetworkObservation> Observations,
        IReadOnlyList<NetworkAnalysisNote> Notes);

    /// <summary>Valor de uma janela qualificada: nome do sujeito, contagem e participação.</summary>
    private readonly record struct StreakPoint(string Name, double Count, double Share);

    public Result Analyze(IReadOnlyList<NetworkSampleAggregate> samples,
        IReadOnlyList<NetworkDynamicPortRange> portRanges, int requestedDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var observations = new List<NetworkObservation>();
        var notes = new List<NetworkAnalysisNote>();
        var ordered = samples.OrderBy(sample => sample.Index).ToArray();
        var runs = ContiguousRuns(ordered);

        AnalyzeGrowth(ordered, runs, observations, notes);
        AnalyzeProcessConcentration(ordered, observations, notes);
        AnalyzeStateConcentration(ordered, observations, notes);
        AnalyzeDynamicPortPressure(ordered, portRanges, observations, notes);
        AnalyzeAttemptFailures(runs, observations, notes);
        _ = requestedDurationSeconds;
        return new(observations, notes);
    }

    /// <summary>
    /// Divide as amostras em blocos contíguos. Índice não consecutivo ou salto temporal acima do
    /// limite encerram o bloco: nenhuma regra atravessa lacuna.
    /// </summary>
    internal List<List<NetworkSampleAggregate>> ContiguousRuns(IReadOnlyList<NetworkSampleAggregate> ordered)
    {
        var runs = new List<List<NetworkSampleAggregate>>();
        var current = new List<NetworkSampleAggregate>();
        foreach (var sample in ordered)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                var gap = sample.ElapsedSeconds - previous.ElapsedSeconds;
                if (sample.Index != previous.Index + 1 || gap > options.MaxGapSeconds)
                {
                    runs.Add(current);
                    current = [];
                }
            }
            current.Add(sample);
        }
        if (current.Count > 0) runs.Add(current);
        return runs;
    }

    private void AnalyzeGrowth(IReadOnlyList<NetworkSampleAggregate> ordered,
        List<List<NetworkSampleAggregate>> runs, List<NetworkObservation> observations,
        List<NetworkAnalysisNote> notes)
    {
        const string rule = "NET-ENDPOINT-GROWTH";
        const string version = "1.0";
        if (ordered.Count == 0)
        {
            notes.Add(new(rule, version, false, "Nenhuma amostra coletada.", NetworkNoteReason.Unavailable));
            return;
        }

        NetworkObservation? best = null;
        var evaluated = 0;
        foreach (var run in runs)
        {
            if (run.Count < options.EndpointGrowthMinimumSamples) continue;
            var first = run[0];
            var last = run[^1];
            var span = last.ElapsedSeconds - first.ElapsedSeconds;
            if (span < options.EndpointGrowthMinimumSeconds) continue;
            evaluated++;
            var delta = last.TcpConnections - first.TcpConnections;
            var growth = first.TcpConnections > 0
                ? delta * 100d / first.TcpConnections
                : delta > 0 ? double.PositiveInfinity : 0d;
            if (delta < options.EndpointGrowthMinimumDelta || growth < options.EndpointGrowthPercent) continue;
            var observation = Create(rule, version, NetworkObservationKind.EndpointGrowth,
                "Crescimento de conexões TCP observado",
                $"As conexões TCP observadas passaram de {first.TcpConnections} para {last.TcpConnections} " +
                $"({growth:0.#}% em {span:0.#} s). O crescimento indica alteração do estado observado; " +
                "não identifica causa nem comprova esgotamento de recursos.",
                EvidenceSeverity.PotentialImpact, EvidenceConfidence.Low,
                "network.tcp.connections", "conexões", options.EndpointGrowthMinimumDelta,
                null, null, first.TimestampUtc, last.TimestampUtc, TimeSpan.FromSeconds(span),
                run.Count, run.Count,
                run.Min(sample => (double)sample.TcpConnections), run.Max(sample => (double)sample.TcpConnections),
                run.Average(sample => (double)sample.TcpConnections), null, "network-endpoints",
                run.Select(sample => new NetworkEvidencePoint(sample.TimestampUtc, sample.Index,
                    sample.TcpConnections, "conexões")).ToArray(),
                ["Crescimento de contagem não distingue novas conexões legítimas de repetição ou acúmulo em TIME_WAIT."]);
            if (best is null || delta > best.ObservedMax) best = observation;
        }

        if (best is not null)
        {
            observations.Add(best);
            notes.Add(new(rule, version, true, best.Statement, NetworkNoteReason.Observed));
        }
        else
            notes.Add(evaluated == 0
                ? new(rule, version, false,
                    $"Amostras ou duração insuficientes: são necessários {options.EndpointGrowthMinimumSamples} " +
                    $"índices contíguos e {options.EndpointGrowthMinimumSeconds:0.#} s.",
                    NetworkNoteReason.InsufficientSamples)
                : new(rule, version, true, "Condição não observada na janela.", NetworkNoteReason.ConditionNotObserved));
    }

    private void AnalyzeProcessConcentration(IReadOnlyList<NetworkSampleAggregate> ordered,
        List<NetworkObservation> observations, List<NetworkAnalysisNote> notes)
    {
        const string rule = "NET-PROC-CONCENTRATION";
        const string version = "1.0";
        if (ordered.Count == 0)
        {
            notes.Add(new(rule, version, false, "Nenhuma amostra coletada.", NetworkNoteReason.Unavailable));
            return;
        }

        var identities = ordered.SelectMany(sample => sample.TopProcesses
                .Where(process => process.Identity is not null)
                .Select(process => process.Identity!))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (identities.Length == 0)
        {
            notes.Add(new(rule, version, false,
                "Nenhum processo com identidade verificável foi observado; concentração não avaliável.",
                NetworkNoteReason.Unavailable));
            return;
        }

        NetworkObservation? best = null;
        var evaluated = false;
        var minimumEndpoints = options.ProcessConcentrationMinimumEndpoints;
        foreach (var identity in identities)
        {
            var streak = new List<(NetworkSampleAggregate Sample, int Endpoints, double Share, string? Name)>();
            var longest = new List<(NetworkSampleAggregate Sample, int Endpoints, double Share, string? Name)>();
            void Commit()
            {
                if (streak.Count > longest.Count) longest = [.. streak];
                streak.Clear();
            }

            foreach (var sample in ordered)
            {
                var process = sample.TopProcesses.FirstOrDefault(candidate =>
                    string.Equals(candidate.Identity, identity, StringComparison.Ordinal));
                var total = sample.TcpConnections + sample.UdpEndpoints;
                var endpoints = process is null ? 0 : process.TcpConnections + process.UdpEndpoints;
                var share = total > 0 ? endpoints * 100d / total : 0;
                if (endpoints < minimumEndpoints || share < options.ProcessConcentrationPercent)
                {
                    Commit();
                    continue;
                }
                if (streak.Count > 0)
                {
                    var previous = streak[^1].Sample;
                    if (sample.Index != previous.Index + 1 ||
                        sample.ElapsedSeconds - previous.ElapsedSeconds > options.MaxGapSeconds)
                        Commit();
                }
                streak.Add((sample, endpoints, share, process?.Name));
            }
            Commit();
            if (longest.Count == 0) continue;

            evaluated = true;
            var span = longest[^1].Sample.ElapsedSeconds - longest[0].Sample.ElapsedSeconds;
            if (longest.Count < options.ProcessConcentrationMinimumSamples ||
                span < options.ProcessConcentrationMinimumSeconds) continue;

            var name = longest[^1].Name;
            var observation = Create(rule, version, NetworkObservationKind.ProcessConcentration,
                "Concentração de endpoints em um processo",
                $"{name ?? identity} sustentou entre {longest.Min(point => point.Endpoints)} e " +
                $"{longest.Max(point => point.Endpoints)} endpoint(s) por {span:0.#} s, alcançando " +
                $"{longest.Max(point => point.Share):0.#}% dos endpoints observados. A concentração é uma " +
                "observação de distribuição; não caracteriza o processo nem atribui causa.",
                EvidenceSeverity.Informational, EvidenceConfidence.Low,
                "network.endpoints.per_process", "endpoints", minimumEndpoints,
                identity, name, longest[0].Sample.TimestampUtc, longest[^1].Sample.TimestampUtc,
                TimeSpan.FromSeconds(span), longest.Count, longest.Count,
                longest.Min(point => (double)point.Endpoints), longest.Max(point => (double)point.Endpoints),
                longest.Average(point => (double)point.Endpoints), longest.Max(point => point.Share),
                "network-endpoints",
                longest.Select(point => new NetworkEvidencePoint(point.Sample.TimestampUtc, point.Sample.Index,
                    point.Endpoints, "endpoints")).ToArray(),
                ["Um processo com muitas conexões pode ser legítimo (navegador, servidor local, agente de atualização)."]);
            if (best is null || observation.ObservedMax > best.ObservedMax) best = observation;
        }

        if (best is not null)
        {
            observations.Add(best);
            notes.Add(new(rule, version, true, best.Statement, NetworkNoteReason.Observed));
        }
        else
            notes.Add(evaluated
                ? new(rule, version, true, "Condição não observada na janela.", NetworkNoteReason.ConditionNotObserved)
                : new(rule, version, false,
                    $"Nenhum processo atingiu {minimumEndpoints} endpoints com " +
                    $"{options.ProcessConcentrationPercent:0.#}% de participação por " +
                    $"{options.ProcessConcentrationMinimumSeconds:0.#} s.",
                    NetworkNoteReason.PreconditionNotObserved));
    }

    private void AnalyzeStateConcentration(IReadOnlyList<NetworkSampleAggregate> ordered,
        List<NetworkObservation> observations, List<NetworkAnalysisNote> notes)
    {
        const string rule = "NET-STATE-CONCENTRATION";
        const string version = "1.0";
        if (ordered.Count == 0)
        {
            notes.Add(new(rule, version, false, "Nenhuma amostra coletada.", NetworkNoteReason.Unavailable));
            return;
        }

        NetworkObservation? best = null;
        var evaluated = false;
        foreach (var state in ordered.SelectMany(sample => sample.TcpStates)
                     .Select(count => count.State)
                     .Where(state => state is not (TcpConnectionState.Listen or TcpConnectionState.Closed
                         or TcpConnectionState.Unknown))
                     .Distinct())
        {
            var streak = LongestQualifyingStreak<StreakPoint>(ordered, sample =>
            {
                if (sample.TcpConnections == 0) return null;
                var candidate = sample.TcpStates.FirstOrDefault(count => count.State == state);
                if (candidate is null || candidate.Connections < options.StateConcentrationMinimumConnections)
                    return null;
                var share = candidate.Connections * 100d / sample.TcpConnections;
                return share >= options.StateConcentrationPercent
                    ? new StreakPoint(state.ToString(), candidate.Connections, share)
                    : null;
            });
            if (streak.Count == 0) continue;

            evaluated = true;
            var span = streak[^1].Sample.ElapsedSeconds - streak[0].Sample.ElapsedSeconds;
            if (streak.Count < options.StateConcentrationMinimumSamples ||
                span < options.StateConcentrationMinimumSeconds) continue;

            var stateName = streak[^1].Value.Name;
            var observation = Create(rule, version, NetworkObservationKind.StateConcentration,
                "Concentração de conexões TCP em um estado",
                $"O estado {stateName} concentrou entre {streak.Min(point => point.Value.Count):0} e " +
                $"{streak.Max(point => point.Value.Count):0} conexões ({streak.Max(point => point.Value.Share):0.#}% " +
                $"do TCP) por {span:0.#} s. Concentração em um estado não é erro por si só e não comprova esgotamento.",
                EvidenceSeverity.Informational, EvidenceConfidence.Low,
                $"network.tcp.state.{stateName.ToLowerInvariant()}", "conexões",
                options.StateConcentrationMinimumConnections, stateName, stateName,
                streak[0].Sample.TimestampUtc, streak[^1].Sample.TimestampUtc, TimeSpan.FromSeconds(span),
                streak.Count, streak.Count,
                streak.Min(point => point.Value.Count), streak.Max(point => point.Value.Count),
                streak.Average(point => point.Value.Count), streak.Max(point => point.Value.Share),
                "network-endpoints",
                streak.Select(point => new NetworkEvidencePoint(point.Sample.TimestampUtc, point.Sample.Index,
                    point.Value.Count, "conexões")).ToArray(),
                ["TIME_WAIT e CLOSE_WAIT são estados normais do protocolo; concentração apenas descreve o estado observado."]);
            if (best is null || observation.ObservedMax > best.ObservedMax) best = observation;
        }

        if (best is not null)
        {
            observations.Add(best);
            notes.Add(new(rule, version, true, best.Statement, NetworkNoteReason.Observed));
        }
        else if (!evaluated)
            notes.Add(new(rule, version, false,
                $"Nenhum estado TCP atingiu {options.StateConcentrationMinimumConnections} conexões com " +
                $"{options.StateConcentrationPercent:0.#}% de participação.",
                NetworkNoteReason.PreconditionNotObserved));
        else
            notes.Add(new(rule, version, false,
                $"É necessário sustentar a condição por {options.StateConcentrationMinimumSamples} amostras e " +
                $"{options.StateConcentrationMinimumSeconds:0.#} s.",
                NetworkNoteReason.InsufficientSamples));
    }

    private void AnalyzeDynamicPortPressure(IReadOnlyList<NetworkSampleAggregate> ordered,
        IReadOnlyList<NetworkDynamicPortRange> portRanges, List<NetworkObservation> observations,
        List<NetworkAnalysisNote> notes)
    {
        const string rule = "NET-EPHEMERAL-PRESSURE";
        const string version = "1.0";
        if (ordered.Count == 0)
        {
            notes.Add(new(rule, version, false, "Nenhuma amostra coletada.", NetworkNoteReason.Unavailable));
            return;
        }
        var tcpRanges = portRanges.Where(range => range.Protocol == NetworkProtocol.Tcp).ToArray();
        if (tcpRanges.Length == 0)
        {
            notes.Add(new(rule, version, false,
                "Intervalo dinâmico de TCP indisponível; pressão sobre portas dinâmicas não avaliável.",
                NetworkNoteReason.Unavailable));
            return;
        }

        var streak = LongestQualifyingStreak<StreakPoint>(ordered, sample =>
        {
            if (sample.TcpConnections == 0) return null;
            var share = sample.EphemeralTcpConnections * 100d / sample.TcpConnections;
            return sample.EphemeralTcpConnections >= options.DynamicPortMinimumEndpoints &&
                   share >= options.DynamicPortMinimumSharePercent
                ? new StreakPoint("tcp", sample.EphemeralTcpConnections, share)
                : null;
        });
        var span = streak.Count == 0 ? 0 : streak[^1].Sample.ElapsedSeconds - streak[0].Sample.ElapsedSeconds;
        if (streak.Count < options.DynamicPortMinimumSamples || span < options.DynamicPortMinimumSeconds)
        {
            notes.Add(new(rule, version, false,
                $"Nenhuma janela sustentou {options.DynamicPortMinimumEndpoints} endpoints na faixa dinâmica com " +
                $"{options.DynamicPortMinimumSharePercent:0.#}% do TCP por {options.DynamicPortMinimumSeconds:0.#} s.",
                NetworkNoteReason.PreconditionNotObserved));
            return;
        }

        var range = tcpRanges.OrderBy(item => item.StartPort).First();
        var peak = streak.Max(point => point.Value.Count);
        observations.Add(Create(rule, version, NetworkObservationKind.DynamicPortPressure,
            "Uso elevado de portas dinâmicas observado",
            $"Entre {streak.Min(point => point.Value.Count):0} e {peak:0} conexões TCP usaram portas locais dentro do " +
            $"intervalo dinâmico configurado ({range.StartPort}–{range.EndPort}, {range.PortCount} portas), " +
            $"atingindo {streak.Max(point => point.Value.Share):0.#}% do TCP por {span:0.#} s. " +
            "A razão entre endpoints observados e tamanho do intervalo não determina disponibilidade real: " +
            "reservas, exclusões gerenciadas e tempo de permanência em TIME_WAIT também influenciam.",
            EvidenceSeverity.PotentialImpact, EvidenceConfidence.Low,
            "network.tcp.ephemeral", "conexões", options.DynamicPortMinimumEndpoints, null, null,
            streak[0].Sample.TimestampUtc, streak[^1].Sample.TimestampUtc, TimeSpan.FromSeconds(span),
            streak.Count, streak.Count,
            streak.Min(point => point.Value.Count), peak,
            streak.Average(point => point.Value.Count), streak.Max(point => point.Value.Share),
            "network-endpoints",
            streak.Select(point => new NetworkEvidencePoint(point.Sample.TimestampUtc, point.Sample.Index,
                point.Value.Count, "conexões")).ToArray(),
            ["Contagem elevada na faixa dinâmica não confirma esgotamento de portas nem falha de conexão."]));
        notes.Add(new(rule, version, true, "Uso elevado de portas dinâmicas observado.", NetworkNoteReason.Observed));
    }

    private void AnalyzeAttemptFailures(List<List<NetworkSampleAggregate>> runs,
        List<NetworkObservation> observations, List<NetworkAnalysisNote> notes)
    {
        const string rule = "NET-ATTEMPT-FAILURES";
        const string version = "1.0";
        NetworkObservation? best = null;
        var evaluated = 0;
        var wrapped = false;
        var total = runs.Sum(run => run.Count);
        foreach (var run in runs)
        {
            var values = run.Where(sample => sample.TcpAttemptFailures is not null).ToArray();
            if (values.Length < options.AttemptFailureMinimumSamples) continue;
            var first = values[0];
            var last = values[^1];
            var span = last.ElapsedSeconds - first.ElapsedSeconds;
            if (span < options.AttemptFailureMinimumSeconds) continue;
            evaluated++;
            var start = first.TcpAttemptFailures!.Value;
            var end = last.TcpAttemptFailures!.Value;
            if (end < start)
            {
                // Contador de 32 bits regrediu: pode ser wrap ou reinício. Nunca é tratado como aumento.
                wrapped = true;
                continue;
            }
            var delta = end - start;
            if (delta < options.AttemptFailureMinimumDelta) continue;
            var observation = Create(rule, version, NetworkObservationKind.AttemptFailures,
                "Aumento de falhas de tentativa TCP",
                $"O contador cumulativo de falhas de tentativa TCP cresceu {delta} durante {span:0.#} s observados. " +
                "Falhas de tentativa podem ocorrer por indisponibilidade momentânea, recusa ou timeout; " +
                "o aumento não atribui causa e não confirma esgotamento de portas.",
                EvidenceSeverity.PotentialImpact, EvidenceConfidence.Low,
                "network.tcp.attempt_failures", "falhas", options.AttemptFailureMinimumDelta,
                null, null, first.TimestampUtc, last.TimestampUtc, TimeSpan.FromSeconds(span),
                values.Length, values.Length,
                null, delta, null, null, "network-statistics",
                values.Select(sample => new NetworkEvidencePoint(sample.TimestampUtc, sample.Index,
                    sample.TcpAttemptFailures!.Value, "falhas cumulativas")).ToArray(),
                ["O contador de 32 bits é cumulativo desde o boot e pode sofrer wrap em máquinas muito ativas."]);
            if (best is null || delta > best.ObservedMax) best = observation;
        }

        if (best is not null)
        {
            observations.Add(best);
            notes.Add(new(rule, version, true, best.Statement, NetworkNoteReason.Observed));
            return;
        }
        if (wrapped)
        {
            notes.Add(new(rule, version, false,
                "O contador de falhas de tentativa regrediu (wrap de 32 bits ou reinício do sistema); " +
                "o intervalo não foi interpretado como aumento.",
                NetworkNoteReason.InvalidSamples));
            return;
        }
        if (total == 0)
        {
            notes.Add(new(rule, version, false, "Nenhuma amostra coletada.", NetworkNoteReason.Unavailable));
            return;
        }
        notes.Add(evaluated > 0
            ? new(rule, version, true, "Condição não observada na janela.", NetworkNoteReason.ConditionNotObserved)
            : new(rule, version, false,
                $"São necessárias {options.AttemptFailureMinimumSamples} amostras e " +
                $"{options.AttemptFailureMinimumSeconds:0.#} s para avaliar falhas de tentativa.",
                NetworkNoteReason.InsufficientSamples));
    }

    /// <summary>
    /// Maior sequência contígua de amostras que satisfazem o predicado. Índice não consecutivo ou
    /// salto temporal acima do limite encerram a sequência.
    /// </summary>
    private List<(NetworkSampleAggregate Sample, T Value)> LongestQualifyingStreak<T>(
        IReadOnlyList<NetworkSampleAggregate> ordered, Func<NetworkSampleAggregate, T?> predicate)
        where T : struct
    {
        var streak = new List<(NetworkSampleAggregate Sample, T Value)>();
        var longest = new List<(NetworkSampleAggregate Sample, T Value)>();
        void Commit()
        {
            if (streak.Count > longest.Count) longest = [.. streak];
            streak.Clear();
        }

        foreach (var sample in ordered)
        {
            var value = predicate(sample);
            if (value is null)
            {
                Commit();
                continue;
            }
            if (streak.Count > 0)
            {
                var previous = streak[^1].Sample;
                if (sample.Index != previous.Index + 1 ||
                    sample.ElapsedSeconds - previous.ElapsedSeconds > options.MaxGapSeconds)
                    Commit();
            }
            streak.Add((sample, value.Value));
        }
        Commit();
        return longest;
    }

    private NetworkObservation Create(string ruleId, string version, NetworkObservationKind kind,
        string title, string statement, EvidenceSeverity severity, EvidenceConfidence confidence,
        string metric, string unit, double? threshold, string? subject, string? subjectName,
        DateTimeOffset from, DateTimeOffset to, TimeSpan duration, int supporting, int evaluated,
        double? min, double? max, double? mean, double? p95, string source,
        IReadOnlyList<NetworkEvidencePoint> points, IReadOnlyList<string> limitations) =>
        new($"{ruleId}@{from.UtcDateTime:O}#{subject ?? "session"}", ruleId, version, kind, title, statement,
            severity, confidence, CausalityStatus.NotAssessed, metric, unit, threshold, subject, subjectName,
            from, to, duration, supporting, evaluated, min, max, mean, p95, source,
            points.Take(Math.Max(1, options.EvidencePointLimit)).ToArray(), limitations);
}
