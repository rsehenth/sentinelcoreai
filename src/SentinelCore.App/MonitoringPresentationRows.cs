using System.Globalization;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

// Pure projections: no window, collector, service composition or historical writes.
internal static class MonitoringPresentationRows
{
    public sealed record MonitoringPresetRow(MonitoringDurationPreset Preset, string PortugueseLabel)
    {
        public string Label => Loc.T(PortugueseLabel);
        public override string ToString() => Label;
    }

    public sealed record MonitoringCategoryRow(MonitoringIncidentCategory Category, string PortugueseLabel)
    {
        public string Label => Loc.T(PortugueseLabel);
        public override string ToString() => Label;
    }

    public sealed record MonitoringOccurrenceRow(TimeSpan? Offset, string PortugueseLabel)
    {
        public string Label => Loc.T(PortugueseLabel);
        public override string ToString() => Label;
    }

    public sealed record MonitoringHistoryRow(MonitoringSession Session)
    {
        public string DateText => Session.CompletedUtc.ToLocalTime()
            .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        public string SummaryText =>
            Loc.F($"{Loc.T(MonitoringGuidance.StatusName(Session.Status))} · cobertura ") +
            Loc.F($"{Loc.T(MonitoringGuidance.CoverageName(Session.CoverageStatus))} · ") +
            Loc.F($"{Session.Incidents.Count} incidente(s) · {Session.Segments.Count} checkpoint(s)") +
            (Session.Recovery.Recovered ? Loc.T(" · recuperada") : string.Empty);
    }

    public sealed record MonitoringIncidentRow(MonitoringIncident Incident, MonitoringSession? Session)
    {
        public string Time => Incident.RecordedUtc.ToLocalTime()
            .ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture);
        public string Category => Loc.T(MonitoringGuidance.CategoryName(Incident.Category));
        public string Detail => Incident.OccurredUtc is { } occurred
            ? Loc.F($"ocorrência ≈ {occurred.ToLocalTime():HH:mm:ss}")
            : Loc.T("ocorrência não informada");
        public string Description => string.IsNullOrWhiteSpace(Incident.Description)
            ? "—"
            : Incident.Description;
    }

    public sealed record MonitoringSegmentRow(MonitoringSegment Segment)
    {
        public string Source => Loc.T(MonitoringGuidance.SourceName(Segment.Source));
        public string Sequence => Segment.Sequence.ToString(CultureInfo.CurrentCulture);
        public string Range => Loc.F($"{Segment.FromUtc.ToLocalTime():dd/MM HH:mm:ss}–") +
            Loc.F($"{Segment.ToUtc.ToLocalTime():HH:mm:ss}");
        public string Observations => Segment.ObservationCount.ToString(CultureInfo.CurrentCulture);
    }
}
