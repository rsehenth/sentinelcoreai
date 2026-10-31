using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SentinelCore.Application;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Infrastructure.Windows.Network;
using SentinelCore.Persistence;

namespace SentinelCore.App;

public partial class App : System.Windows.Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, args) =>
        {
            LogFailure(args.Exception);
            MessageBox.Show("Erro não tratado. O aplicativo será fechado. Consulte logs/application.log no diretório de dados.", "SentinelCore AI");
            args.Handled = true; Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) LogFailure(error);
        };
        try
        {
            instanceMutex = new Mutex(true, @"Local\SentinelCoreAI.Phase1", out var created);
            if (!created)
            {
                MessageBox.Show("O SentinelCore AI já está aberto nesta sessão.", "SentinelCore AI");
                Shutdown(); return;
            }
            var performanceRunner = new PerformanceSessionRunner(Infrastructure.Windows.Performance.WindowsPerformanceCollectors.Create());
            var networkRunner = new NetworkSessionRunner(
                WindowsNetworkCollectors.CreateSampleCollectors(),
                WindowsNetworkCollectors.CreateFactsCollectors());
            var store = new MemoryStore();
            MainWindow = new MainWindow(new AuditRunner(WindowsCollectors.CreateBasic()),
                new HardwareRunner(WindowsHardwareCollectors.Create()),
                new EventInvestigationRunner(WindowsEventEvidenceCollectors.Create()),
                performanceRunner, networkRunner, store, store, store, store, store);
            MainWindow.Show();
        }
        catch (Exception error)
        {
            LogFailure(error); MessageBox.Show("Não foi possível iniciar o aplicativo. Consulte logs/application.log.", "SentinelCore AI"); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }
    internal static void LogFailure(Exception error)
    {
        try
        {
            var directory = Path.Combine(MemoryStore.DefaultRoot, "logs"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "application.log"), $"{DateTimeOffset.UtcNow:O} {error}\n");
        }
        catch (Exception loggingError) { System.Diagnostics.Debug.WriteLine($"Falha ao gravar log: {loggingError.GetType().Name}"); }
    }
}
