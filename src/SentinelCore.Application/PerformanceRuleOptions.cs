namespace SentinelCore.Application;

public sealed record PerformanceRuleOptions(
    double SustainedCpuPercent = 85,
    int SustainedCpuMinimumSamples = 10,
    double SustainedCpuMinimumSeconds = 20,
    double LowAvailableMemoryPercent = 10,
    int LowMemoryMinimumSamples = 8,
    double LowMemoryMinimumSeconds = 15,
    double CommitUsagePercent = 90,
    ulong CommitHeadroomMinimumBytes = 1_073_741_824,
    int CommitMinimumSamples = 8,
    double CommitMinimumSeconds = 15,
    double PrivateGrowthPercent = 25,
    ulong PrivateGrowthMinimumBytes = 67_108_864,
    int PrivateGrowthMinimumSamples = 10,
    double PrivateGrowthMinimumSeconds = 30,
    double DiskLatencyMilliseconds = 50,
    int DiskLatencyMinimumSamples = 8,
    double DiskLatencyMinimumSeconds = 15,
    double IoPeakMultiplier = 3,
    double IoPeakMinimumBytesPerSecond = 1_048_576,
    int IoPeakMinimumSamples = 3,
    double IoPeakMinimumSeconds = 5,
    double MaxGapSeconds = 2.5)
{
    public static PerformanceRuleOptions Default { get; } = new();
}
