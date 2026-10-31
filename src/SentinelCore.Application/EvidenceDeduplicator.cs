using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed record DeduplicationResult(
    IReadOnlyList<EvidenceGroup> Groups,
    IReadOnlyList<EventLogEvidence> Events,
    IReadOnlyList<WerEvidence> Reports,
    IReadOnlyList<string> Notes);

// Identity rules are deliberately conservative: raw records keep their own identity,
// WER reports are counted by ReportId, and duplicate sightings are never added as new reports.
public static class EvidenceDeduplicator
{
    private const int MaximumGroupLimitations = 20;

    public static DeduplicationResult Build(IEnumerable<RawEventRecord> eventRecords,
        IEnumerable<RawWerReport> werReports)
    {
        ArgumentNullException.ThrowIfNull(eventRecords);
        ArgumentNullException.ThrowIfNull(werReports);

        var notes = new List<string>();
        var groups = new Dictionary<string, GroupState>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<EventLogEvidence>();
        var reports = new List<WerEvidence>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequence = 0;

        foreach (var raw in eventRecords)
        {
            var classification = EventCatalog.ClassifyEvent(raw);
            var state = State(groups, classification);
            var limitations = new List<string>();
            string id;
            if (raw.RecordId is { } recordId)
            {
                id = $"evt:{raw.Channel}:{recordId}";
            }
            else
            {
                // Without RecordId the record is never merged with another record by code or time.
                id = $"evt:{raw.Channel}:{raw.Provider}:{raw.EventId}:{raw.OccurredUtc.UtcTicks}:{sequence++}";
                limitations.Add("Identidade sem RecordId; o registro não foi fundido com outros por código ou horário.");
            }
            if (!seen.Add(id)) continue;
            var item = new EventLogEvidence(id, classification.GroupId, raw.Channel, raw.Provider, raw.EventId,
                raw.RecordId, raw.OccurredUtc, raw.Level, raw.RawLevel, raw.TaskCode, raw.ActivityId,
                raw.Message, raw.MessageTruncated, raw.Data ?? [], limitations);
            evidence.Add(item);
            state.Add(item, classification.Severity);
        }

        foreach (var raw in werReports)
        {
            var classification = EventCatalog.ClassifyReport(raw);
            var state = State(groups, classification);
            var id = $"wer:{raw.Source}:{raw.Store}:{raw.ReportId}";
            if (!seen.Add(id))
            {
                notes.Add($"Relatório WER repetido na mesma origem foi lido uma única vez ({raw.ReportId}).");
                continue;
            }
            var item = new WerEvidence(id, classification.GroupId, raw.ReportId, raw.ReportIdFromFile,
                raw.Source, raw.Store, raw.FolderName, raw.EventType, raw.ProblemCode, raw.EventTimeUtc,
                raw.CreationTimeUtc, raw.ReportStatus, raw.Application, raw.ApplicationPath, raw.Module,
                raw.ModuleVersion, raw.BucketId, raw.Data ?? [], raw.Limitations ?? [], raw.Confidence);
            reports.Add(item);
            state.Add(item, classification.Severity);
        }

        var built = groups.Values
            .Select(g => g.Build())
            .OrderByDescending(g => g.Severity)
            .ThenByDescending(g => g.LastObservedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new(built,
            evidence.OrderByDescending(e => e.OccurredUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray(),
            reports.OrderByDescending(e => e.EventTimeUtc ?? e.CreationTimeUtc ?? DateTimeOffset.MinValue)
                .ThenBy(e => e.Id, StringComparer.Ordinal).ToArray(),
            notes);
    }

    private static GroupState State(Dictionary<string, GroupState> groups, EventClassification classification)
    {
        if (groups.TryGetValue(classification.GroupId, out var existing)) return existing;
        var created = new GroupState(classification);
        groups[classification.GroupId] = created;
        return created;
    }

    private sealed class GroupState(EventClassification classification)
    {
        private EvidenceSeverity severity = classification.Severity;
        private EvidenceConfidence confidence = EvidenceConfidence.High;
        private int eventCount;
        private int reportCount;
        private DateTimeOffset? first;
        private DateTimeOffset? last;
        private readonly HashSet<string> reportIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> providers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> eventIds = [];
        private readonly HashSet<string> problemCodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> channels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> limitations = new(StringComparer.Ordinal);

        public void Add(EventLogEvidence item, EvidenceSeverity itemSeverity)
        {
            eventCount++;
            severity = Max(severity, itemSeverity);
            confidence = Min(confidence, item.RecordId is null ? EvidenceConfidence.Medium : EvidenceConfidence.High);
            Observe(item.OccurredUtc);
            providers.Add(item.Provider);
            eventIds.Add(item.EventId);
            channels.Add(item.Channel);
            sources.Add("Log de Eventos");
            foreach (var limitation in item.Limitations) limitations.Add(limitation);
        }

        public void Add(WerEvidence item, EvidenceSeverity itemSeverity)
        {
            reportCount++;
            reportIds.Add(item.ReportId);
            severity = Max(severity, itemSeverity);
            confidence = Min(confidence, item.Confidence);
            Observe(item.EventTimeUtc ?? item.CreationTimeUtc);
            if (!string.IsNullOrWhiteSpace(item.ProblemCode)) problemCodes.Add(item.ProblemCode);
            sources.Add($"WER — {item.Source}/{item.Store}");
            foreach (var limitation in item.Limitations) limitations.Add(limitation);
        }

        private void Observe(DateTimeOffset? at)
        {
            if (at is not { } value) return;
            if (first is null || value < first) first = value;
            if (last is null || value > last) last = value;
        }

        public EvidenceGroup Build() => new(
            classification.GroupId,
            classification.Category,
            classification.Title,
            providers.Count == 1 ? providers.Single() : null,
            eventIds.Count == 1 ? eventIds.Single() : null,
            problemCodes.Count == 1 ? problemCodes.Single() : null,
            severity,
            confidence,
            CausalityStatus.NotAssessed,
            eventCount,
            reportIds.Count,
            Math.Max(0, reportCount - reportIds.Count),
            first,
            last,
            channels.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray(),
            sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            limitations.Take(MaximumGroupLimitations).ToArray());

        private static EvidenceSeverity Max(EvidenceSeverity left, EvidenceSeverity right) =>
            (EvidenceSeverity)Math.Max((int)left, (int)right);

        private static EvidenceConfidence Min(EvidenceConfidence left, EvidenceConfidence right) =>
            (EvidenceConfidence)Math.Min((int)left, (int)right);
    }
}
