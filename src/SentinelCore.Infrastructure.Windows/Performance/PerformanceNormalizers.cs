using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Performance;

public readonly record struct CpuCounterSample(ulong Idle100ns, ulong Kernel100ns, ulong User100ns);

public readonly record struct ProcessCounterSample(int Pid, DateTimeOffset? StartTimeUtc, string Name,
    ulong CpuTime100ns, ulong WorkingSetBytes, ulong PrivateBytes,
    ulong IoReadBytes, ulong IoWriteBytes, bool IoAvailable);

public static partial class PerformanceNormalizers
{
    private const double TicksPerSecond = 10_000_000d;

    public static CpuReading NormalizeCpu(CpuCounterSample? previous, CpuCounterSample current,
        double elapsedSeconds, int logicalProcessors, double? queueLength)
    {
        var processors = Math.Max(1, logicalProcessors);
        if (previous is null || elapsedSeconds <= 0)
            return new(0, 0, 0, queueLength, processors, false,
                ["Primeira amostra usada como base; percentuais de CPU indisponíveis."]);
        var before = previous.Value;
        if (current.Idle100ns < before.Idle100ns || current.Kernel100ns < before.Kernel100ns ||
            current.User100ns < before.User100ns)
            return new(0, 0, 0, queueLength, processors, false,
                ["Contadores de CPU retrocederam; intervalo descartado e próxima amostra será usada como base."]);
        var idle = current.Idle100ns - before.Idle100ns;
        var kernel = current.Kernel100ns - before.Kernel100ns;
        var user = current.User100ns - before.User100ns;
        var kernelBusy = kernel > idle ? kernel - idle : 0UL;
        var busy = kernelBusy + user;
        var capacity = elapsedSeconds * TicksPerSecond * processors;
        var limitations = new List<string>();
        var scale = busy > capacity ? capacity / busy : 1d;
        if (busy > capacity)
            limitations.Add("Soma de tempos de CPU excedeu a capacidade do intervalo; valores normalizados e limitados a 100%.");
        double Percent(ulong value) => capacity <= 0 ? 0 : value * scale / capacity * 100d;
        return new(Percent(busy), Percent(user), Percent(kernelBusy), queueLength, processors, true, limitations);
    }

    public static MemoryReading NormalizeMemory(ulong installedBytes, ulong availableBytes,
        ulong? commitLimitBytes, ulong? committedBytes, double? pagesPerSecond, ulong? systemCacheBytes)
    {
        var used = availableBytes <= installedBytes ? installedBytes - availableBytes : 0;
        var availablePercent = installedBytes == 0 ? (double?)null
            : Math.Min(100, availableBytes * 100d / installedBytes);
        var commitUsage = commitLimitBytes is > 0 && committedBytes is { } committed
            ? (double?)(committed * 100d / commitLimitBytes.Value) : null;
        var headroom = commitLimitBytes is { } limit && committedBytes is { } usedCommit && limit >= usedCommit
            ? (ulong?)(limit - usedCommit) : null;
        var limitations = new List<string>();
        if (availableBytes > installedBytes)
            limitations.Add("Memória disponível excedeu a instalada; valores inconsistentes foram limitados.");
        return new(installedBytes, availableBytes, used, availablePercent, commitLimitBytes, committedBytes,
            commitUsage, headroom, pagesPerSecond, systemCacheBytes, true, limitations);
    }

    public static string ProcessIdentity(int pid, DateTimeOffset? startTimeUtc) =>
        startTimeUtc is { } start ? $"{pid}:{start.UtcTicks}" : $"{pid}:unknown";

    public static ProcessReading NormalizeProcess(ProcessCounterSample? previous, ProcessCounterSample current,
        double elapsedSeconds, int logicalProcessors, bool newInSession, List<string> limitations)
    {
        var processors = Math.Max(1, logicalProcessors);
        var identity = ProcessIdentity(current.Pid, current.StartTimeUtc);
        if (current.StartTimeUtc is null)
            limitations.Add($"Processo {current.Pid}: hora de início indisponível; reutilização de PID não pode ser detectada.");
        double cpu = 0;
        var cpuValid = false;
        if (previous is { } before && elapsedSeconds > 0 && before.Pid == current.Pid &&
            before.StartTimeUtc == current.StartTimeUtc)
        {
            if (current.CpuTime100ns < before.CpuTime100ns)
                limitations.Add($"Processo {current.Pid}: contador de CPU retrocedeu; intervalo descartado.");
            else
            {
                var delta = current.CpuTime100ns - before.CpuTime100ns;
                cpu = Math.Clamp(delta / (elapsedSeconds * 10_000_000d * processors) * 100d, 0, 100);
                cpuValid = true;
            }
        }
        var ioValid = false;
        double readRate = 0, writeRate = 0;
        if (current.IoAvailable && previous is { } ioBefore && elapsedSeconds > 0 && ioBefore.IoAvailable &&
            ioBefore.Pid == current.Pid && ioBefore.StartTimeUtc == current.StartTimeUtc &&
            current.IoReadBytes >= ioBefore.IoReadBytes &&
            current.IoWriteBytes >= ioBefore.IoWriteBytes)
        {
            readRate = (current.IoReadBytes - ioBefore.IoReadBytes) / elapsedSeconds;
            writeRate = (current.IoWriteBytes - ioBefore.IoWriteBytes) / elapsedSeconds;
            ioValid = true;
        }
        else if (!current.IoAvailable)
            limitations.Add($"Processo {current.Pid}: contadores de I/O indisponíveis.");
        return new(identity, current.Pid, current.Name, current.StartTimeUtc, cpu,
            current.WorkingSetBytes, current.PrivateBytes, readRate, writeRate,
            cpuValid, ioValid, newInSession || previous is null);
    }

    public static int? ParseDiskNumber(string instanceName)
    {
        var token = instanceName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(token, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    public static double? LatencyMilliseconds(double? averageSeconds, double? operationsPerSecond) =>
        averageSeconds is null || operationsPerSecond is not > 0
            ? null : Math.Max(0, averageSeconds.Value * 1000d);

    private static double? NonNegative(double? value) => value is { } number ? Math.Max(0, number) : null;

    public static StorageReading NormalizeStorage(StorageScope scope, string instanceName, int? diskNumber,
        string? volumeLetter, double? readBytesPerSecond, double? writeBytesPerSecond,
        double? readOperationsPerSecond, double? writeOperationsPerSecond,
        double? averageReadSeconds, double? averageWriteSeconds, double? queueLength,
        double? percentActiveTime, string? hardwareStableId, bool valid)
    {
        var letter = string.IsNullOrWhiteSpace(volumeLetter) ? null : volumeLetter.TrimEnd(':');
        var id = scope == StorageScope.Physical
            ? $"physical:{diskNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? instanceName}"
            : $"volume:{(letter ?? instanceName)}";
        return new(id, scope, instanceName, diskNumber, letter,
            NonNegative(readBytesPerSecond), NonNegative(writeBytesPerSecond),
            NonNegative(readOperationsPerSecond), NonNegative(writeOperationsPerSecond),
            LatencyMilliseconds(averageReadSeconds, readOperationsPerSecond),
            LatencyMilliseconds(averageWriteSeconds, writeOperationsPerSecond),
            queueLength, percentActiveTime, hardwareStableId, valid);
    }
}
