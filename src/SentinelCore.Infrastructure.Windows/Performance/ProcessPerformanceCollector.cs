using System.ComponentModel;
using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Performance;

internal sealed class ProcessPerformanceCollector : IPerformanceCollector
{
    private const string CollectorId = "performance-processes";

    public string Id => CollectorId;
    public string Version => "1.0.0";
    public string Source => "System.Diagnostics.Process + GetProcessIoCounters";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Processes;
    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session(context);

    private sealed class Session : IPerformanceCollectorSession
    {
        private readonly int processors = NativeSystem.LogicalProcessors();
        private readonly int limit;
        private Dictionary<string, ProcessCounterSample> previous = new(StringComparer.Ordinal);
        private Dictionary<int, string> previousByPid = new();
        private HashSet<string> tracked = new(StringComparer.Ordinal);

        public Session(PerformanceCollectionContext context) =>
            limit = context.Limits?.MaxTrackedProcesses ?? PerformanceLimits.Default.MaxTrackedProcesses;

        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
            CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                previous.Clear();
                previousByPid.Clear();
                return PerformanceCollectorTick.Empty(CollectorId, timestampUtc,
                    $"Não foi possível enumerar processos ({error.GetType().Name}).");
            }

            var limitations = new List<string>();
            var inaccessible = 0;
            var observedPids = new HashSet<int>();
            var counters = new List<ProcessCounterSample>(processes.Length);
            for (var i = 0; i < processes.Length; i++)
            {
                if (i > 0 && i % 32 == 0) cancellationToken.ThrowIfCancellationRequested();
                var process = processes[i];
                try
                {
                    if (TryReadCounter(process, ref inaccessible, observedPids) is { } counter) counters.Add(counter);
                }
                finally { process.Dispose(); }
            }
            if (inaccessible > 0)
                limitations.Add($"{inaccessible} processo(s) sem acesso completo a nome ou métricas; valores omitidos.");

            var exits = new List<ProcessExitReading>();
            var readings = new List<ProcessReading>(counters.Count);
            var current = new HashSet<string>(StringComparer.Ordinal);
            var next = new Dictionary<string, ProcessCounterSample>(StringComparer.Ordinal);
            var nextByPid = new Dictionary<int, string>();
            var unknownStart = 0;
            var ioDenied = 0;
            foreach (var counter in counters)
            {
                var identity = PerformanceNormalizers.ProcessIdentity(counter.Pid, counter.StartTimeUtc);
                current.Add(identity);
                next[identity] = counter;
                if (previousByPid.TryGetValue(counter.Pid, out var oldIdentity) && oldIdentity != identity)
                {
                    if (previous.TryGetValue(oldIdentity, out var old))
                        exits.Add(new(oldIdentity, counter.Pid, old.Name, timestampUtc, "PID reutilizado"));
                    previous.Remove(oldIdentity);
                }
                nextByPid[counter.Pid] = identity;
                ProcessCounterSample? before = previous.TryGetValue(identity, out var value) ? value : null;
                var reading = PerformanceNormalizers.NormalizeProcess(before, counter, elapsed.TotalSeconds,
                    processors, newInSession: false, []);
                if (counter.StartTimeUtc is null) unknownStart++;
                if (!counter.IoAvailable) ioDenied++;
                readings.Add(reading);
            }
            foreach (var pair in previous)
                if (!current.Contains(pair.Key))
                {
                    if (observedPids.Contains(pair.Value.Pid))
                    {
                        limitations.Add(
                            $"Processo {pair.Value.Pid}: leitura indisponível nesta amostra; término não confirmado.");
                        continue;
                    }
                    var reason = ExitReason(pair.Value);
                    if (reason is null)
                        limitations.Add($"Processo {pair.Value.Pid}: término não confirmado nesta amostra.");
                    else
                        exits.Add(new(pair.Key, pair.Value.Pid, pair.Value.Name, timestampUtc, reason));
                }
            if (unknownStart > 0)
                limitations.Add(
                    $"{unknownStart} processo(s) sem hora de início; reutilização de PID não verificável nesses casos.");
            if (ioDenied > 0)
                limitations.Add($"{ioDenied} processo(s) sem contadores de I/O; taxas de I/O omitidas para eles.");

            var selected = SelectInternal(readings, limitations, limit, tracked);
            tracked = selected.Select(reading => reading.Identity).ToHashSet(StringComparer.Ordinal);
            previous = next;
            previousByPid = nextByPid;
            return new PerformanceCollectorTick(CollectorId, timestampUtc, false, null, null,
                selected, exits, [], limitations.Distinct(StringComparer.Ordinal).ToArray());
        }, cancellationToken);

        public void Dispose() { }

        private static ProcessCounterSample? TryReadCounter(Process process, ref int inaccessible,
            HashSet<int> observedPids)
        {
            int pid;
            try { pid = process.Id; }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or Win32Exception or UnauthorizedAccessException) { return null; }
            observedPids.Add(pid);

            var name = $"processo-{pid}";
            try { name = process.ProcessName; }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or Win32Exception or UnauthorizedAccessException) { inaccessible++; }

            DateTimeOffset? start = null;
            try { start = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or Win32Exception or UnauthorizedAccessException) { }

            ulong cpuTicks, workingSet, privateBytes;
            try
            {
                cpuTicks = (ulong)Math.Max(0, process.TotalProcessorTime.Ticks);
                workingSet = (ulong)Math.Max(0, process.WorkingSet64);
                privateBytes = (ulong)Math.Max(0, process.PrivateMemorySize64);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or Win32Exception or UnauthorizedAccessException)
            {
                inaccessible++;
                return null;
            }

            var ioAvailable = NativeSystem.TryGetProcessIoCounters(pid, out var read, out var write);

            return new(pid, start, name, cpuTicks, workingSet, privateBytes, read, write, ioAvailable);
        }

        private static string? ExitReason(ProcessCounterSample before)
        {
            try
            {
                using var process = Process.GetProcessById(before.Pid);
                try
                {
                    var current = new DateTimeOffset(process.StartTime.ToUniversalTime());
                    return before.StartTimeUtc is { } known && current != known ? "PID reutilizado" : null;
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                    or Win32Exception or UnauthorizedAccessException) { return null; }
            }
            catch (ArgumentException) { return "Processo encerrado"; }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or Win32Exception or UnauthorizedAccessException) { return null; }
        }
    }

    internal static IReadOnlyList<ProcessReading> SelectInternal(List<ProcessReading> readings,
        List<string> limitations, int limit, HashSet<string>? tracked = null)
    {
        var union = new List<ProcessReading>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var activeTracked = tracked ?? [];
        foreach (var reading in readings.Where(reading => reading.CpuValid)
                     .OrderByDescending(reading => reading.CpuPercent).Take(10)
                     .Concat(readings.OrderByDescending(reading => reading.PrivateBytes).Take(10))
                     .Concat(readings.Where(reading => activeTracked.Contains(reading.Identity))
                         .OrderByDescending(reading => reading.PrivateBytes)))
            if (ids.Add(reading.Identity)) union.Add(reading);

        var selected = union.Count <= limit
            ? union
            : union.OrderByDescending(reading => reading.CpuValid ? reading.CpuPercent : -1)
                .ThenByDescending(reading => reading.PrivateBytes)
                .Take(limit)
                .ToList();

        if (readings.Count > selected.Count)
        {
            limitations.Add(
                $"Limite de {limit} processos acompanhados aplicado: {selected.Count} reportados de {readings.Count} processos elegíveis; {readings.Count - selected.Count} não acompanhados.");
        }
        return selected;
    }
}
