using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.App;

public partial class MainWindow
{
    private readonly ProductOperationCoordinator operations;
    private bool recoveryClosingAfterSave;
    private bool operationClosingAfterSave;

    private IDisposable? TryBeginProductOperation()
    {
        try { return operations.Acquire(); }
        catch (TargetLockedException error)
        {
            ShowWarning(error.Message);
            return null;
        }
    }

    private void OnProductOperationChanged()
    {
        LanguageRow.IsEnabled = !operations.IsBusy;
        UpdateAllBusyButtons();
        UpdateTweakButtons();
        UpdateRepairButtons();
        DebloatSection.SetOperationBlocked(operations.IsBusy);
        RecoverySection.SetOperationBlocked(operations.IsBusy);
        TweaksNav.IsEnabled = RepairsNav.IsEnabled = RecoveryNav.IsEnabled = !operations.IsBusy;
        if (!operations.IsBusy && operationClosingAfterSave)
        {
            operationClosingAfterSave = false;
            Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void InvalidateMaintenanceHistory()
    {
        RecoverySection.InvalidateAuditHistory();
        tweaksLoaded = false;
    }
}
