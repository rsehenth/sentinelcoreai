using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

public static class WindowsEventEvidenceCollectors
{
    public static IReadOnlyList<IEventEvidenceCollector> Create(InvestigationLimits? limits = null) =>
        [new WindowsEventLogCollector(limits), new WerMetadataCollector(limits)];
}
