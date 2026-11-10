using SentinelCore.Domain;

namespace SentinelCore.RepairBroker;

internal static class NativeOutcome
{
    internal static RepairExecutionOutcome BeforeStart(RepairBrokerStatus status, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        return new(false, false, status == RepairBrokerStatus.CancelledBeforeStart,
            status == RepairBrokerStatus.Indeterminate, null, detail, false, [], now, now, TimeSpan.Zero, false,
            [detail]) { Native = new(status, false, null, null) };
    }
}

