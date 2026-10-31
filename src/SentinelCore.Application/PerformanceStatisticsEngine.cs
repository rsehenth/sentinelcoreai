using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class PerformanceStatisticsEngine
{
    public PerformanceStatistics Compute(IReadOnlyList<PerformanceSample> samples,
        PerformanceRuleOptions options)
    {
        var total = samples.Count;
        var duration = total < 2 ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, samples[^1].ElapsedSeconds - samples[0].ElapsedSeconds));
        var cpuSeries = samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Cpu is { Valid: true } cpu ? (double?)cpu.TotalPercent : null)).ToArray();
        var cpu = Metric(cpuSeries, "cpu.total.percent", "%", options.SustainedCpuPercent, true, options.MaxGapSeconds);
        var user = Metric(samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Cpu is { Valid: true } reading ? (double?)reading.UserPercent : null)).ToArray(),
            "cpu.user.percent", "%", null, true, options.MaxGapSeconds);
        var kernel = Metric(samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Cpu is { Valid: true } reading ? (double?)reading.KernelPercent : null)).ToArray(),
            "cpu.kernel.percent", "%", null, true, options.MaxGapSeconds);
        var queue = Metric(samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Cpu?.ProcessorQueueLength)).ToArray(), "cpu.queue.length", "itens", null, true, options.MaxGapSeconds);
        var available = Metric(samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Memory is { Valid: true } reading ? reading.AvailablePercent : null)).ToArray(),
            "memory.available.percent", "%", options.LowAvailableMemoryPercent, false, options.MaxGapSeconds);
        var used = Metric(samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Memory is { Valid: true } reading ? (double?)reading.UsedBytes : null)).ToArray(),
            "memory.used.bytes", "bytes", null, true, options.MaxGapSeconds);
        var commit = Metric(samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Memory is { Valid: true } reading ? reading.CommitUsagePercent : null)).ToArray(),
            "memory.commit.percent", "%", options.CommitUsagePercent, true, options.MaxGapSeconds);
        var pages = Metric(samples.Select(sample => (sample.TimestampUtc, sample.ElapsedSeconds, sample.Index,
            Value: sample.Memory is { Valid: true } reading ? reading.PagesPerSecond : null)).ToArray(),
            "memory.pages.per.second", "/s", null, true, options.MaxGapSeconds);
        var processes = ProcessStatistics(samples, options);
        var storage = StorageStatistics(samples, options);
        var limitations = new List<string>();
        if (total == 0) limitations.Add("Nenhuma amostra coletada; estatísticas indisponíveis.");
        if (cpu.ValidSamples < 2) limitations.Add("Menos de 2 amostras válidas de CPU; tendência e duração não calculadas.");
        return new(duration, total, cpu.ValidSamples, available.ValidSamples, cpu, user, kernel, queue,
            available, used, commit, pages, processes, storage, limitations);
    }

    private static MetricStatistics Metric(
        IReadOnlyList<(DateTimeOffset Time, double Elapsed, int Index, double? Value)> series,
        string metric, string unit, double? threshold, bool upperBound, double maxGapSeconds = 1.5)
    {
        var valid = series.Where(item => item.Value.HasValue).Select(item => (
            item.Time, item.Elapsed, item.Index, Value: item.Value!.Value)).ToArray();
        var values = valid.Select(item => item.Value).OrderBy(value => value).ToArray();
        var limitations = new List<string>();
        if (values.Length < 3) limitations.Add("Menos de 3 amostras válidas; mediana indisponível.");
        if (values.Length < 20) limitations.Add("Menos de 20 amostras válidas; percentis P95/P99 indisponíveis.");
        double? median = values.Length >= 3
            ? values.Length % 2 == 1
                ? values[values.Length / 2]
                : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2d
            : null;
        double? p95 = values.Length >= 20 ? Percentile(values, 95) : null;
        double? p99 = values.Length >= 20 ? Percentile(values, 99) : null;
        double? slope = null, growth = null;
        if (valid.Length >= 5)
        {
            var span = valid[^1].Elapsed - valid[0].Elapsed;
            if (span >= 10)
            {
                var meanX = valid.Average(item => item.Elapsed - valid[0].Elapsed);
                var meanY = values.Average();
                var numerator = valid.Sum(item =>
                    (item.Elapsed - valid[0].Elapsed - meanX) * (item.Value - meanY));
                var denominator = valid.Sum(item =>
                    Math.Pow(item.Elapsed - valid[0].Elapsed - meanX, 2));
                if (denominator > 0) slope = numerator / denominator * 60d;
            }
        }
        if (valid.Length >= 2 && valid[0].Value != 0)
            growth = (valid[^1].Value - valid[0].Value) / Math.Abs(valid[0].Value) * 100d;
        double? beyond = null;
        if (threshold is { } limit && valid.Length >= 2)
        {
            var seconds = 0d;
            for (var i = 1; i < valid.Length; i++)
            {
                var gap = valid[i].Elapsed - valid[i - 1].Elapsed;
                var continuous = valid[i].Index == valid[i - 1].Index + 1 && gap > 0 && gap <= maxGapSeconds;
                if (!continuous) continue;

                var previous = upperBound ? valid[i - 1].Value >= limit : valid[i - 1].Value <= limit;
                var current = upperBound ? valid[i].Value >= limit : valid[i].Value <= limit;
                if (previous && current) seconds += gap;
            }
            beyond = seconds;
        }
        return new(metric, unit, valid.Length, series.Count,
            values.Length == 0 ? null : values[0], values.Length == 0 ? null : values[^1],
            values.Length == 0 ? null : values.Average(), median, p95, p99,
            valid.Length == 0 ? null : valid[0].Value, valid.Length == 0 ? null : valid[^1].Value,
            growth, slope, threshold, upperBound, beyond, limitations);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100d * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static IReadOnlyList<ProcessStatistics> ProcessStatistics(
        IReadOnlyList<PerformanceSample> samples, PerformanceRuleOptions options)
    {
        var readings = samples.SelectMany(sample => sample.Processes.Select(process =>
            (Sample: sample, Process: process))).ToArray();
        return readings.GroupBy(item => item.Process.Identity, StringComparer.Ordinal)
            .Select(group =>
            {
                var latest = group.OrderBy(item => item.Sample.Index).Last().Process;
                MetricStatistics Series(Func<ProcessReading, double?> value,
                    Func<ProcessReading, bool> valid, string metric, string unit) =>
                    Metric(group.Select(item => (item.Sample.TimestampUtc, item.Sample.ElapsedSeconds, item.Sample.Index,
                            Value: valid(item.Process) ? value(item.Process) : null)).ToArray(),
                        metric, unit, null, true, options.MaxGapSeconds);
                return new ProcessStatistics(group.Key, latest.Pid, latest.Name,
                    Series(process => process.CpuPercent, process => process.CpuValid,
                        "process.cpu.percent", "%"),
                    Series(process => process.PrivateBytes, _ => true, "process.private.bytes", "bytes"),
                    Series(process => process.WorkingSetBytes, _ => true, "process.working.set.bytes", "bytes"),
                    Series(process => process.IoReadBytesPerSecond, process => process.IoValid,
                        "process.io.read.bytes.per.second", "bytes/s"),
                    Series(process => process.IoWriteBytesPerSecond, process => process.IoValid,
                        "process.io.write.bytes.per.second", "bytes/s"));
            })
            .OrderByDescending(item => item.CpuPercent.Max ?? 0)
            .ThenByDescending(item => item.PrivateBytes.Max ?? 0)
            .ToArray();
    }

    private static IReadOnlyList<StorageStatistics> StorageStatistics(
        IReadOnlyList<PerformanceSample> samples, PerformanceRuleOptions options)
    {
        var readings = samples.SelectMany(sample => sample.Storage.Select(storage =>
            (Sample: sample, Reading: storage))).ToArray();
        return readings.GroupBy(item => item.Reading.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var latest = group.OrderBy(item => item.Sample.Index).Last().Reading;
                MetricStatistics Series(Func<StorageReading, double?> value, string metric, string unit,
                    double? threshold = null, bool upperBound = true) =>
                    Metric(group.Where(item => item.Reading.Valid)
                            .Select(item => (item.Sample.TimestampUtc, item.Sample.ElapsedSeconds, item.Sample.Index,
                                Value: value(item.Reading))).ToArray(),
                        metric, unit, threshold, upperBound, options.MaxGapSeconds);
                return new StorageStatistics(group.Key, latest.Scope, latest.InstanceName, latest.DiskNumber,
                    latest.VolumeLetter, latest.HardwareStableId,
                    Series(reading => reading.ReadBytesPerSecond, "storage.read.bytes.per.second", "bytes/s"),
                    Series(reading => reading.WriteBytesPerSecond, "storage.write.bytes.per.second", "bytes/s"),
                    Series(reading => reading.ReadOperationsPerSecond, "storage.read.operations.per.second", "ops/s"),
                    Series(reading => reading.WriteOperationsPerSecond, "storage.write.operations.per.second", "ops/s"),
                    Series(reading => reading.ReadLatencyMs, "storage.read.latency.ms", "ms",
                        options.DiskLatencyMilliseconds),
                    Series(reading => reading.WriteLatencyMs, "storage.write.latency.ms", "ms",
                        options.DiskLatencyMilliseconds),
                    Series(reading => reading.QueueLength, "storage.queue.length", "itens", null, false),
                    Series(reading => reading.PercentActiveTime, "storage.active.percent", "%", null, false));
            })
            .OrderBy(item => item.Scope).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
