using SentinelCore.Domain;

namespace SentinelCore.Application;

internal sealed record Run(int Start, int End, int Points);

internal static class SustainedMetricRuns
{
    public static IReadOnlyList<Run> Find<T>(
        IReadOnlyList<(DateTimeOffset Time, double Elapsed, int Index, T Value)> valid,
        Func<T, bool> predicate, int minimumSamples, double minimumSeconds, double maxGapSeconds = 1.5)
    {
        var runs = new List<Run>();
        var start = -1;
        for (var i = 0; i < valid.Count; i++)
        {
            var match = predicate(valid[i].Value);
            if (match)
            {
                if (start < 0)
                {
                    start = i;
                }
                else
                {
                    var prev = valid[i - 1];
                    var curr = valid[i];
                    var gap = curr.Elapsed - prev.Elapsed;
                    var continuous = curr.Index == prev.Index + 1 && gap > 0 && gap <= maxGapSeconds;
                    if (!continuous)
                    {
                        var end = i - 1;
                        var span = valid[end].Elapsed - valid[start].Elapsed;
                        if (end - start + 1 >= minimumSamples && span >= minimumSeconds)
                            runs.Add(new(start, end, (int)Math.Min(8, end - start + 1)));
                        start = i;
                    }
                }
            }
            else
            {
                if (start >= 0)
                {
                    var end = i - 1;
                    var span = valid[end].Elapsed - valid[start].Elapsed;
                    if (end - start + 1 >= minimumSamples && span >= minimumSeconds)
                        runs.Add(new(start, end, (int)Math.Min(8, end - start + 1)));
                    start = -1;
                }
            }
        }
        if (start >= 0)
        {
            var end = valid.Count - 1;
            var span = valid[end].Elapsed - valid[start].Elapsed;
            if (end - start + 1 >= minimumSamples && span >= minimumSeconds)
                runs.Add(new(start, end, (int)Math.Min(8, end - start + 1)));
        }
        return runs;
    }
}

public sealed record PerformanceAnalysis(
    IReadOnlyList<PerformanceObservation> Observations,
    IReadOnlyList<PerformanceAnalysisNote> Notes);

public sealed class PerformanceEvidenceAnalyzer(PerformanceRuleOptions? options = null)
{
    private readonly PerformanceRuleOptions rules = options ?? PerformanceRuleOptions.Default;

    public PerformanceAnalysis Analyze(IReadOnlyList<PerformanceSample> samples,
        PerformanceStatistics statistics)
    {
        var observations = new List<PerformanceObservation>();
        var notes = new List<PerformanceAnalysisNote>();
        EvaluateSustainedCpu(samples, observations, notes);
        EvaluateLowMemory(samples, observations, notes);
        EvaluateCommit(samples, observations, notes);
        EvaluatePrivateGrowth(samples, observations, notes);
        EvaluateDiskLatency(samples, observations, notes);
        EvaluateIoPeak(samples, observations, notes);
        return new(observations.OrderBy(item => item.WindowFromUtc).ToArray(), notes);
    }

    private static PerformanceAnalysisNote Note(string rule, bool evaluated,
        PerformanceNoteReason reason, string detail) => new(rule, "1.0", evaluated, detail, reason);

    private static string Availability(int total, int unavailable, int invalid)
    {
        var parts = new List<string>(2);
        if (unavailable > 0) parts.Add($"{unavailable} sem leitura (indisponível)");
        if (invalid > 0) parts.Add($"{invalid} inválida(s)");
        return parts.Count == 0
            ? $"{total} amostra(s) da sessão"
            : string.Join(" e ", parts) + $" de {total} amostra(s)";
    }

    private static PerformanceNoteReason AvailabilityReason(int valid, int unavailable, int invalid) =>
        valid == 0 && unavailable > 0 && invalid == 0 ? PerformanceNoteReason.Unavailable
        : valid == 0 && invalid > 0 && unavailable == 0 ? PerformanceNoteReason.InvalidSamples
        : PerformanceNoteReason.InsufficientSamples;

    private static string InsufficientDetail(string label, int valid, int required, int total,
        int unavailable, int invalid, PerformanceNoteReason reason) => reason switch
    {
        PerformanceNoteReason.Unavailable =>
            $"Leitura de {label} indisponível em {unavailable} de {total} amostra(s); regra não avaliada.",
        PerformanceNoteReason.InvalidSamples =>
            $"Nenhuma amostra válida de {label} ({invalid} inválida(s) de {total}); regra não avaliada.",
        _ => $"Amostras válidas de {label} insuficientes ({valid} de {required}); " +
            Availability(total, unavailable, invalid) + "."
    };

    private static (DateTimeOffset Time, double Elapsed, int Index, double Value)[] Series(
        IReadOnlyList<PerformanceSample> samples, Func<PerformanceSample, double?> value) =>
        samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index, Value: value(sample)))
            .Where(item => item.Value.HasValue)
            .Select(item => (Time: item.TimestampUtc, Elapsed: item.ElapsedSeconds, item.Index, Value: item.Value!.Value))
            .ToArray();

    private static (DateTimeOffset Time, double Elapsed, int Index, StorageReading Reading)[] StorageSamples(
        IReadOnlyList<PerformanceSample> samples) =>
        samples.SelectMany(sample => sample.Storage
                .Where(storage => storage.Valid)
                .Select(storage => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index, Reading: storage)))
            .ToArray();

    private static double Median(double[] sorted) => sorted.Length % 2 == 1
        ? sorted[sorted.Length / 2]
        : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2d;

    private static EvidenceConfidence Confidence(TimeSpan span, double minimumSeconds, bool limited) =>
        limited ? EvidenceConfidence.Low
            : span.TotalSeconds >= 2 * minimumSeconds ? EvidenceConfidence.High : EvidenceConfidence.Medium;

    private static PerformanceObservation Observe(string ruleId, PerformanceObservationKind kind, string title,
        string statement, EvidenceSeverity severity, EvidenceConfidence confidence, string metric, string unit,
        double? threshold, string? subject, string? subjectName,
        (DateTimeOffset Time, double Elapsed, int Index, double Value)[] window, int evaluatedSamples,
        string source, IReadOnlyList<string> limitations)
    {
        var values = window.Select(item => item.Value).OrderBy(value => value).ToArray();
        var span = window.Length < 2 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, window[^1].Elapsed - window[0].Elapsed));
        var step = Math.Max(1, window.Length / 8);
        var points = window.Where((_, index) => index % step == 0 || index == window.Length - 1)
            .Take(8)
            .Select(item => new PerformanceEvidencePoint(item.Time, item.Index, item.Value, unit))
            .ToArray();
        double? p95 = values.Length >= 20
            ? values[Math.Clamp((int)Math.Ceiling(0.95 * values.Length) - 1, 0, values.Length - 1)]
            : null;
        return new(
            $"{ruleId}:{metric}@{window[0].Time:O}#{subject ?? "session"}", ruleId, "1.0", kind, title, statement,
            severity, confidence, CausalityStatus.NotAssessed, metric, unit, threshold, subject, subjectName,
            window[0].Time, window[^1].Time, span, window.Length, evaluatedSamples,
            values[0], values[^1], values.Average(), p95, source, points, limitations);
    }

    private void EvaluateSustainedCpu(IReadOnlyList<PerformanceSample> samples,
        List<PerformanceObservation> observations, List<PerformanceAnalysisNote> notes)
    {
        const string rule = "PERF-CPU-SUSTAINED";
        var series = Series(samples, sample => sample.Cpu is { Valid: true } cpu ? cpu.TotalPercent : null);
        var unavailable = samples.Count(sample => sample.Cpu is null);
        var invalid = samples.Count(sample => sample.Cpu is { Valid: false });
        if (series.Length < rules.SustainedCpuMinimumSamples)
        {
            var reason = AvailabilityReason(series.Length, unavailable, invalid);
            notes.Add(Note(rule, false, reason, InsufficientDetail("CPU", series.Length,
                rules.SustainedCpuMinimumSamples, samples.Count, unavailable, invalid, reason)));
            return;
        }
        var runs = SustainedMetricRuns.Find(series, value => value >= rules.SustainedCpuPercent,
            rules.SustainedCpuMinimumSamples, rules.SustainedCpuMinimumSeconds, rules.MaxGapSeconds);
        if (runs.Count == 0)
        {
            notes.Add(Note(rule, true, PerformanceNoteReason.ConditionNotObserved,
                $"Nenhum intervalo com CPU total ≥ {rules.SustainedCpuPercent}% por " +
                $"{rules.SustainedCpuMinimumSeconds} s; condição não observada em {series.Length} amostra(s) válida(s)."));
            return;
        }
        var run = runs[0];
        var window = series.Skip(run.Start).Take(run.End - run.Start + 1).ToArray();
        var span = TimeSpan.FromSeconds(Math.Max(0, window[^1].Elapsed - window[0].Elapsed));
        observations.Add(Observe(rule, PerformanceObservationKind.SustainedCpu, "CPU total sustentada",
            $"CPU total permaneceu em ou acima de {rules.SustainedCpuPercent}% por {span.TotalSeconds:0} s; " +
            "observação não confirma causa.", EvidenceSeverity.PotentialImpact,
            Confidence(span, rules.SustainedCpuMinimumSeconds, false), "cpu.total.percent", "%",
            rules.SustainedCpuPercent, null, null, window, series.Length, "GetSystemTimes", []));
        notes.Add(Note(rule, true, PerformanceNoteReason.Observed,
            $"{runs.Count} intervalo(s) sustentado(s) observado(s) em {series.Length} amostra(s) válida(s)."));
    }

    private void EvaluateLowMemory(IReadOnlyList<PerformanceSample> samples,
        List<PerformanceObservation> observations, List<PerformanceAnalysisNote> notes)
    {
        const string rule = "PERF-MEM-LOW-AVAILABLE";
        var series = Series(samples, sample =>
            sample.Memory is { Valid: true } memory ? memory.AvailablePercent : null);
        var unavailable = samples.Count(sample => sample.Memory is null);
        var invalid = samples.Count(sample => sample.Memory is { Valid: false });
        if (series.Length < rules.LowMemoryMinimumSamples)
        {
            var reason = AvailabilityReason(series.Length, unavailable, invalid);
            notes.Add(Note(rule, false, reason, InsufficientDetail("memória", series.Length,
                rules.LowMemoryMinimumSamples, samples.Count, unavailable, invalid, reason)));
            return;
        }
        var runs = SustainedMetricRuns.Find(series, value => value <= rules.LowAvailableMemoryPercent,
            rules.LowMemoryMinimumSamples, rules.LowMemoryMinimumSeconds, rules.MaxGapSeconds);
        if (runs.Count == 0)
        {
            notes.Add(Note(rule, true, PerformanceNoteReason.ConditionNotObserved,
                $"Memória disponível permaneceu acima de {rules.LowAvailableMemoryPercent}%; condição não observada " +
                $"em {series.Length} amostra(s) válida(s)."));
            return;
        }
        var run = runs[0];
        var window = series.Skip(run.Start).Take(run.End - run.Start + 1).ToArray();
        var span = TimeSpan.FromSeconds(Math.Max(0, window[^1].Elapsed - window[0].Elapsed));
        observations.Add(Observe(rule, PerformanceObservationKind.LowAvailableMemory,
            "Memória disponível reduzida",
            $"Memória disponível permaneceu em ou abaixo de {rules.LowAvailableMemoryPercent}% por " +
            $"{span.TotalSeconds:0} s; não confirma vazamento.", EvidenceSeverity.PotentialImpact,
            Confidence(span, rules.LowMemoryMinimumSeconds, false), "memory.available.percent", "%",
            rules.LowAvailableMemoryPercent, null, null, window, series.Length,
            "GlobalMemoryStatusEx + WMI PerfOS_Memory", []));
        notes.Add(Note(rule, true, PerformanceNoteReason.Observed,
            $"{runs.Count} intervalo(s) observado(s) em {series.Length} amostra(s) válida(s)."));
    }

    private void EvaluateCommit(IReadOnlyList<PerformanceSample> samples,
        List<PerformanceObservation> observations, List<PerformanceAnalysisNote> notes)
    {
        const string rule = "PERF-MEM-COMMIT-MARGIN";
        var missing = samples.Count(sample => sample.Memory is null) +
            samples.Count(sample => sample.Memory is { Valid: true } memory &&
                (memory.CommitUsagePercent is null || memory.CommitHeadroomBytes is null));
        var invalid = samples.Count(sample => sample.Memory is { Valid: false });
        var series = samples
            .Where(sample => sample.Memory is { Valid: true } memory &&
                memory.CommitUsagePercent.HasValue && memory.CommitHeadroomBytes.HasValue)
            .Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
                Value: (Usage: sample.Memory!.CommitUsagePercent!.Value,
                    Headroom: sample.Memory.CommitHeadroomBytes!.Value)))
            .ToArray();
        if (series.Length < rules.CommitMinimumSamples)
        {
            var reason = AvailabilityReason(series.Length, missing, invalid);
            notes.Add(Note(rule, false, reason, InsufficientDetail("commit", series.Length,
                rules.CommitMinimumSamples, samples.Count, missing, invalid, reason)));
            return;
        }
        var candidates = series.Count(item =>
            item.Value.Headroom <= rules.CommitHeadroomMinimumBytes);
        if (candidates < rules.CommitMinimumSamples)
        {
            notes.Add(Note(rule, true, PerformanceNoteReason.PreconditionNotObserved,
                $"Amostras com margem de commit ≤ {rules.CommitHeadroomMinimumBytes / 1073741824d:0.##} GiB " +
                $"insuficientes para sustentação ({candidates} de {series.Length} amostras satisfazem a " +
                $"pré-condição; mínimo {rules.CommitMinimumSamples}). A pré-condição não foi observada."));
            return;
        }
        var runs = SustainedMetricRuns.Find(series,
            item => item.Headroom <= rules.CommitHeadroomMinimumBytes &&
                item.Usage >= rules.CommitUsagePercent,
            rules.CommitMinimumSamples, rules.CommitMinimumSeconds, rules.MaxGapSeconds);
        if (runs.Count == 0)
        {
            notes.Add(Note(rule, true, PerformanceNoteReason.ConditionNotObserved,
                $"Uso de commit ≥ {rules.CommitUsagePercent}% e margem ≤ " +
                $"{rules.CommitHeadroomMinimumBytes / 1073741824d:0.##} GiB não coexistiram por " +
                $"{rules.CommitMinimumSeconds} s; condição não observada ({candidates} de {series.Length} " +
                "amostras válidas com margem baixa)."));
            return;
        }
        var run = runs[0];
        var selected = series.Skip(run.Start).Take(run.End - run.Start + 1).ToArray();
        var window = selected.Select(item => (item.TimestampUtc, item.ElapsedSeconds, item.Index, item.Value.Usage)).ToArray();
        var span = TimeSpan.FromSeconds(Math.Max(0, window[^1].ElapsedSeconds - window[0].ElapsedSeconds));
        observations.Add(Observe(rule, PerformanceObservationKind.CommitMargin, "Margem de commit reduzida",
            $"Uso de commit em ou acima de {rules.CommitUsagePercent}% e margem em ou abaixo de " +
            $"{rules.CommitHeadroomMinimumBytes / 1073741824d:0.##} GiB por {span.TotalSeconds:0} s.",
            EvidenceSeverity.PotentialImpact, Confidence(span, rules.CommitMinimumSeconds, false),
            "memory.commit.percent", "%", rules.CommitUsagePercent, null, null, window, series.Length,
            "GlobalMemoryStatusEx + WMI PerfOS_Memory", []));
        notes.Add(Note(rule, true, PerformanceNoteReason.Observed,
            $"{runs.Count} intervalo(s) observado(s) em {series.Length} amostra(s) válida(s) de commit."));
    }

    private void EvaluatePrivateGrowth(IReadOnlyList<PerformanceSample> samples,
        List<PerformanceObservation> observations, List<PerformanceAnalysisNote> notes)
    {
        const string rule = "PERF-PROC-PRIVATE-GROWTH";
        var groups = samples
            .SelectMany(sample => sample.Processes.Select(process => (Sample: sample, Process: process)))
            .GroupBy(item => item.Process.Identity, StringComparer.Ordinal)
            .Select(group => (Identity: group.Key, Name: group.First().Process.Name,
                Points: group.OrderBy(item => item.Sample.Index)
                    .Select(item => (Time: item.Sample.TimestampUtc, Elapsed: item.Sample.ElapsedSeconds, item.Sample.Index,
                        Value: (double)item.Process.PrivateBytes)).ToArray()))
            .ToArray();

        var growths = new List<(string Identity, string Name,
            (DateTimeOffset Time, double Elapsed, int Index, double Value)[] Points, double First, double Last)>();

        foreach (var group in groups)
        {
            // Segment continuous points for this process
            var currentSegment = new List<(DateTimeOffset Time, double Elapsed, int Index, double Value)>();
            for (var i = 0; i < group.Points.Length; i++)
            {
                var pt = group.Points[i];
                if (currentSegment.Count == 0)
                {
                    currentSegment.Add(pt);
                }
                else
                {
                    var prev = currentSegment[^1];
                    var gap = pt.Elapsed - prev.Elapsed;
                    var continuous = pt.Index == prev.Index + 1 && gap > 0 && gap <= rules.MaxGapSeconds;
                    if (continuous)
                    {
                        currentSegment.Add(pt);
                    }
                    else
                    {
                        CheckAndAddGrowth(currentSegment, group.Identity, group.Name, growths);
                        currentSegment.Clear();
                        currentSegment.Add(pt);
                    }
                }
            }
            if (currentSegment.Count > 0)
            {
                CheckAndAddGrowth(currentSegment, group.Identity, group.Name, growths);
            }
        }

        if (growths.Count == 0)
        {
            var eligibleCount = groups.Count(g => g.Points.Length >= rules.PrivateGrowthMinimumSamples &&
                (g.Points[^1].Elapsed - g.Points[0].Elapsed) >= rules.PrivateGrowthMinimumSeconds);
            if (eligibleCount == 0)
            {
                notes.Add(Note(rule, false, PerformanceNoteReason.InsufficientSamples,
                    $"Nenhum processo acompanhado por pelo menos {rules.PrivateGrowthMinimumSamples} amostras em " +
                    $"{rules.PrivateGrowthMinimumSeconds} s ({samples.Count} amostra(s) de sessão)."));
            }
            else
            {
                notes.Add(Note(rule, true, PerformanceNoteReason.ConditionNotObserved,
                    "Nenhum processo cresceu acima do percentual e dos bytes mínimos na janela."));
            }
            return;
        }

        foreach (var growth in growths.OrderByDescending(item => item.Last - item.First).Take(5))
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, growth.Points[^1].Elapsed - growth.Points[0].Elapsed));
            var delta = growth.Last - growth.First;
            var percent = delta / growth.First * 100d;
            observations.Add(Observe(rule, PerformanceObservationKind.PrivateMemoryGrowth,
                "Crescimento de memória privada",
                $"Crescimento observado de {delta / 1048576d:0} MiB ({percent:0}%) em {span.TotalSeconds:0} s; " +
                "não confirma vazamento.", EvidenceSeverity.Informational,
                Confidence(span, rules.PrivateGrowthMinimumSeconds, false), "process.private.bytes", "bytes",
                null, growth.Identity, growth.Name, growth.Points, samples.Count,
                "System.Diagnostics.Process", []));
        }
        notes.Add(Note(rule, true, PerformanceNoteReason.Observed,
            $"{growths.Count} processo(s) com crescimento observado."));
    }

    private void CheckAndAddGrowth(
        List<(DateTimeOffset Time, double Elapsed, int Index, double Value)> segment,
        string identity, string name,
        List<(string Identity, string Name, (DateTimeOffset Time, double Elapsed, int Index, double Value)[] Points, double First, double Last)> growths)
    {
        if (segment.Count < rules.PrivateGrowthMinimumSamples) return;
        var span = segment[^1].Elapsed - segment[0].Elapsed;
        if (span < rules.PrivateGrowthMinimumSeconds) return;

        var first = segment[0].Value;
        var last = segment[^1].Value;
        if (first <= 0 || last - first < rules.PrivateGrowthMinimumBytes) return;
        if ((last - first) / first * 100d < rules.PrivateGrowthPercent) return;
        growths.Add((identity, name, segment.ToArray(), first, last));
    }

    private void EvaluateDiskLatency(IReadOnlyList<PerformanceSample> samples,
        List<PerformanceObservation> observations, List<PerformanceAnalysisNote> notes)
    {
        const string rule = "PERF-STORAGE-LATENCY";
        var total = samples.Sum(sample => sample.Storage.Count);
        var valid = StorageSamples(samples);
        if (valid.Length == 0)
        {
            notes.Add(Note(rule, false,
                total == 0 ? PerformanceNoteReason.Unavailable : PerformanceNoteReason.InvalidSamples,
                total == 0
                    ? "Nenhuma leitura de armazenamento disponível; latência não avaliada."
                    : $"{total} leitura(s) de armazenamento inválida(s); latência não avaliada."));
            return;
        }
        var evaluated = 0;
        var found = 0;
        foreach (var group in valid.GroupBy(item => item.Reading.Id, StringComparer.Ordinal))
        {
            var read = group.Where(item => item.Reading.ReadLatencyMs.HasValue)
                .Select(item => (item.Time, item.Elapsed, item.Index, Value: item.Reading.ReadLatencyMs!.Value))
                .ToArray();
            var write = group.Where(item => item.Reading.WriteLatencyMs.HasValue)
                .Select(item => (item.Time, item.Elapsed, item.Index, Value: item.Reading.WriteLatencyMs!.Value))
                .ToArray();

            var directions = new[]
            {
                (Series: read, Direction: "leitura", Metric: "storage.read.latency.ms"),
                (Series: write, Direction: "gravação", Metric: "storage.write.latency.ms")
            };

            foreach (var (series, direction, metric) in directions)
            {
                if (series.Length == 0) continue;
                evaluated = Math.Max(evaluated, series.Length);
                if (series.Length < rules.DiskLatencyMinimumSamples) continue;
                var runs = SustainedMetricRuns.Find(series, value => value >= rules.DiskLatencyMilliseconds,
                    rules.DiskLatencyMinimumSamples, rules.DiskLatencyMinimumSeconds, rules.MaxGapSeconds);
                if (runs.Count == 0) continue;
                foreach (var run in runs)
                {
                    var window = series.Skip(run.Start).Take(run.End - run.Start + 1).ToArray();
                    var span = TimeSpan.FromSeconds(Math.Max(0, window[^1].Elapsed - window[0].Elapsed));
                    observations.Add(Observe(rule, PerformanceObservationKind.DiskLatency,
                        $"Latência de {direction} de disco elevada",
                        $"Latência de {direction} em ou acima de {rules.DiskLatencyMilliseconds} ms por " +
                        $"{span.TotalSeconds:0} s; não confirma defeito.", EvidenceSeverity.PotentialImpact,
                        Confidence(span, rules.DiskLatencyMinimumSeconds, false), metric, "ms",
                        rules.DiskLatencyMilliseconds, group.Key, group.First().Reading.InstanceName, window,
                        series.Length, "WMI PerfDisk_PhysicalDisk", []));
                    found++;
                }
            }
        }
        if (evaluated == 0)
        {
            notes.Add(Note(rule, false, PerformanceNoteReason.Unavailable,
                "Latência não informada nas leituras válidas de armazenamento; condição não avaliada."));
            return;
        }
        if (evaluated < rules.DiskLatencyMinimumSamples)
        {
            notes.Add(Note(rule, false, PerformanceNoteReason.InsufficientSamples,
                $"Amostras válidas de latência insuficientes ({evaluated} de " +
                $"{rules.DiskLatencyMinimumSamples}); condição não avaliada."));
            return;
        }
        notes.Add(found > 0
            ? Note(rule, true, PerformanceNoteReason.Observed,
                $"{found} instância(s) com latência sustentada.")
            : Note(rule, true, PerformanceNoteReason.ConditionNotObserved,
                $"Latência abaixo de {rules.DiskLatencyMilliseconds} ms nas instâncias avaliadas; condição não observada."));
    }

    private void EvaluateIoPeak(IReadOnlyList<PerformanceSample> samples,
        List<PerformanceObservation> observations, List<PerformanceAnalysisNote> notes)
    {
        const string rule = "PERF-STORAGE-IO-PEAK";
        var total = samples.Sum(sample => sample.Storage.Count);
        var valid = StorageSamples(samples);
        if (valid.Length == 0)
        {
            notes.Add(Note(rule, false,
                total == 0 ? PerformanceNoteReason.Unavailable : PerformanceNoteReason.InvalidSamples,
                total == 0
                    ? "Nenhuma leitura de armazenamento disponível; picos de I/O não avaliados."
                    : $"{total} leitura(s) de armazenamento inválida(s); picos de I/O não avaliados."));
            return;
        }
        var evaluated = 0;
        var found = 0;
        foreach (var group in valid.GroupBy(item => item.Reading.Id, StringComparer.Ordinal))
        {
            var read = group.Where(item => item.Reading.ReadBytesPerSecond.HasValue)
                .Select(item => (item.Time, item.Elapsed, item.Index, Value: item.Reading.ReadBytesPerSecond!.Value)).ToArray();
            var write = group.Where(item => item.Reading.WriteBytesPerSecond.HasValue)
                .Select(item => (item.Time, item.Elapsed, item.Index, Value: item.Reading.WriteBytesPerSecond!.Value)).ToArray();
            foreach (var series in new[] { read, write })
            {
                if (series.Length == 0) continue;
                evaluated = Math.Max(evaluated, series.Length);
                if (series.Length < rules.IoPeakMinimumSamples) continue;
                var median = Median(series.Select(item => item.Value).OrderBy(value => value).ToArray());
                var threshold = Math.Max(rules.IoPeakMinimumBytesPerSecond, median * rules.IoPeakMultiplier);
                var runs = SustainedMetricRuns.Find(series, value => value >= threshold,
                    rules.IoPeakMinimumSamples, rules.IoPeakMinimumSeconds, rules.MaxGapSeconds);
                if (runs.Count == 0) continue;
                foreach (var run in runs)
                {
                    var window = series.Skip(run.Start).Take(run.End - run.Start + 1).ToArray();
                    var span = TimeSpan.FromSeconds(Math.Max(0, window[^1].Elapsed - window[0].Elapsed));
                    var peak = window.Max(item => item.Value);
                    var ratio = median > 0 ? peak / median : 0;
                    observations.Add(Observe(rule, PerformanceObservationKind.IoPeak, "Pico de I/O",
                        $"Pico de {peak / 1048576d:0.#} MB/s, {ratio:0.#}× a mediana, por {span.TotalSeconds:0} s; " +
                        "pico não indica gargalo persistente.", EvidenceSeverity.Informational,
                        Confidence(span, rules.IoPeakMinimumSeconds, false), "storage.throughput", "bytes/s",
                        threshold, group.Key, group.First().Reading.InstanceName, window, series.Length,
                        "WMI PerfDisk_PhysicalDisk", []));
                    found++;
                }
            }
        }
        if (evaluated == 0)
        {
            notes.Add(Note(rule, false, PerformanceNoteReason.Unavailable,
                "Vazão não informada nas leituras válidas de armazenamento; picos não avaliados."));
            return;
        }
        if (evaluated < rules.IoPeakMinimumSamples)
        {
            notes.Add(Note(rule, false, PerformanceNoteReason.InsufficientSamples,
                $"Amostras válidas de I/O insuficientes ({evaluated} de {rules.IoPeakMinimumSamples}); " +
                "picos não avaliados."));
            return;
        }
        notes.Add(found > 0
            ? Note(rule, true, PerformanceNoteReason.Observed, $"{found} pico(s) de I/O observado(s).")
            : Note(rule, true, PerformanceNoteReason.ConditionNotObserved, "Nenhum pico relevante."));
    }
}
