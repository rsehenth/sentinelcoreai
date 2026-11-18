using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;

namespace SentinelCore.App;

public partial class MainWindow
{
    private AppsStartupManager? appsStartupManager;
    private bool appsStartupComposed;
    private bool appsStartupClosingAfterSave;

    private void InitializeAppsStartup(AppsStartupManager? manager)
    {
        appsStartupManager = manager;
        AppsStartupSection.BecameIdle += () =>
        {
            if (!appsStartupClosingAfterSave) return;
            appsStartupClosingAfterSave = false;
            Dispatcher.BeginInvoke(new Action(Close));
        };
        Pages.SelectionChanged += OnAppsStartupPageSelectionChanged;
    }

    // Composto só ao selecionar a página: nada é lido na inicialização.
    private void OnAppsStartupPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Pages) || !ReferenceEquals(Pages.SelectedItem, AppsStartupTab) ||
            appsStartupComposed || appsStartupManager is not { } manager) return;
        appsStartupComposed = true;
        AppsStartupSection.Initialize(manager);
    }
}
