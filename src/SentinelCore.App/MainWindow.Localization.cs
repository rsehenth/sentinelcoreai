using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SentinelCore.App;

public partial class MainWindow
{
    // Re-render cached records only. In particular, never reload a maintenance manager or prepare a plan.
    private void RefreshLanguagePresentation()
    {
        PreservePresentationSelection([HardwareSnapshotList, HardwareCategoryList, HardwareItemList], () =>
        {
            if (selectedHardware is { } hardware) ApplyHardwareSnapshot(hardware);
            else ClearHardwareView();
        });
        PreservePresentationSelection([EventHistoryList, EventWindowCombo, EventCategoryCombo, EventGroupList], () =>
        {
            if (selectedEventInvestigation is { } events) ApplyEventInvestigation(events);
            else ClearEventView();
        });
        PreservePresentationSelection([PerformanceHistoryList, PerformanceDurationCombo, PerformanceMetricList,
            PerformanceProcessList, PerformanceStorageList, PerformanceObservationList], () =>
        {
            if (selectedPerformanceSession is { } performance) ApplyPerformanceSession(performance);
            else ClearPerformanceView();
        });
        PreservePresentationSelection([GraphicsHistoryList, GraphicsPresetCombo, GraphicsAdapterList,
            GraphicsMonitorList, GraphicsDriverList, GraphicsEventList, GraphicsWerList, GraphicsObservationList], () =>
        {
            if (selectedGraphics is { } graphics) ApplyGraphicsInvestigation(graphics);
            else ClearGraphicsView();
        });
        PreservePresentationSelection([CorrelationHistoryList, CorrelationPresetCombo, CorrelationTimelineList,
            CorrelationRelationList, CorrelationEvidenceList, CorrelationSourceList, CorrelationOriginCombo], () =>
        {
            if (selectedCorrelation is { } correlation) ApplyCorrelation(correlation);
            else ClearCorrelationView();
        });
        PreservePresentationSelection([NetworkHistoryList, NetworkPresetCombo, NetworkPortList, NetworkStateList,
            NetworkProcessList, NetworkRangeList, NetworkObservationList], () =>
        {
            if (selectedNetworkSession is { } network) ApplyNetworkSession(network);
            else ClearNetworkView();
        });
        PreservePresentationSelection([MonitoringHistoryList, MonitoringPresetCombo, MonitoringIncidentCategoryCombo,
            MonitoringIncidentOccurredCombo, MonitoringIncidentList, MonitoringTimelineList], () =>
        {
            if (selectedMonitoring is { } monitoring) ApplyMonitoringSession(monitoring);
            else ClearMonitoringView();
        });
        correlationMessages.Refresh();
        monitoringMessages.Refresh();
        // These controls keep their current model, preview, typed confirmation, search and filter keys.
        DebloatSection.RefreshLanguage();
        RecoverySection.RefreshLanguage();
        UpdateSummary();
        if (displayedAudit is { } audit)
        {
            var selectedCollector = CollectorList.SelectedItem;
            DisplayLatest(audit);
            if (selectedCollector is not null && CollectorList.Items.Contains(selectedCollector))
                CollectorList.SelectedItem = selectedCollector;
        }
        HistoryList.Items.Refresh();
        OnHistorySelected(HistoryList, null!);
    }

    private static void PreservePresentationSelection(Selector[] selectors, Action render)
    {
        var selected = selectors.Select(selector => selector.SelectedItem).ToArray();
        render();
        for (var index = 0; index < selectors.Length; index++)
        {
            var selector = selectors[index];
            selector.Items.Refresh();
            // Record projections compare their unchanged underlying model, including GUID and enum keys.
            selector.SelectedItem = selected[index] is { } item
                ? selector.Items.Cast<object>().FirstOrDefault(candidate => Equals(candidate, item) ||
                    candidate is HardwareItemRow row && item is HardwareItemRow previous && Equals(row.Item, previous.Item))
                : null;
        }
    }
}
