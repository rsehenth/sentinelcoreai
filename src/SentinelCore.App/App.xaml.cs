using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Infrastructure.Debloat.Native;
using SentinelCore.Infrastructure.Repairs;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Infrastructure.Windows.Monitoring;
using SentinelCore.Infrastructure.Windows.Network;
using SentinelCore.Persistence;

namespace SentinelCore.App;

public partial class App : System.Windows.Application
{
    private Mutex? instanceMutex;
    // Sem raiz autorizada (antes de --data-root ser validado) nada é gravado em disco.
    private static ApplicationLog log = new(null);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string dataRoot;
        try
        {
            dataRoot = DataRootArgument.Resolve(e.Args);
        }
        catch (Exception error)
        {
            // Fail-closed: sem log e sem diretório; o erro é só exibido.
            MessageBox.Show("Não foi possível iniciar o aplicativo: diretório de dados inválido. " + error.Message,
                "SentinelCore AI");
            Shutdown(1);
            return;
        }
        log = new ApplicationLog(dataRoot);
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
            var hardwareCollectors = WindowsHardwareCollectors.Create();
            var eventCollectors = WindowsEventEvidenceCollectors.Create();
            var hardwareRunner = new HardwareRunner(hardwareCollectors);
            var eventRunner = new EventInvestigationRunner(eventCollectors);
            var graphicsRunner = new GraphicsInvestigationRunner(hardwareCollectors, eventCollectors);
            var store = new MemoryStore(dataRoot);
            var correlationRunner = new EvidenceCorrelationRunner(new CorrelationEvidenceReader(
                audits: store, hardware: store, events: store, performance: store, network: store,
                graphics: store, monitoring: store, recoveryHistory: store));
            var monitoringLimits = SentinelCore.Domain.MonitoringLimits.Default;
            var monitoringRunner = new MonitoringSessionRunner(
                [
                    new EventMonitoringSource(WindowsMonitoringCollectors.CreateEventSource(monitoringLimits)),
                    new PerformanceMonitoringSource(WindowsMonitoringCollectors.CreatePerformanceCollector()),
                    new NetworkMonitoringSource(WindowsNetworkCollectors.CreateSampleCollectors())
                ], store, limits: monitoringLimits);
            var realTweaksEnabled = RealTweakBuildGate.AllowsRealExecution(e.Args);
            var tweakCatalog = T2bCatalog.Create();
            var registryAccess = new HkcuRegistryAccess();
            var tweakReader = new HkcuRegistryTargetReader(registryAccess);
            IOperationExecutor tweakExecutor = realTweaksEnabled
                ? new HkcuRegistryOperationExecutor(registryAccess)
                : new DisabledOperationExecutor();
            var tweakProfile = new Windows11SystemProfileReader(new HkcuPolicyProbe(),
                realTweaksEnabled, T2bCatalog.OperationIds, OperatingSystem.IsWindows,
                () => OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0,
                () => Environment.IsPrivilegedProcess);
            var transactionLocks = new FileExclusiveLockFactory(dataRoot);
            var tweakEngine = new SafetyEngine(tweakCatalog, tweakProfile, tweakReader,
                new HkcuProtectionProvider(), store, tweakExecutor, transactionLocks,
                confirmationPolicy: new TweakAuthorizationConfirmationPolicy(),
                hkcuContext: realTweaksEnabled ? HkcuContext.ReadHash : null);
            var tweakRecovery = new RecoveryService(store, tweakReader, tweakExecutor, transactionLocks,
                catalog: tweakCatalog, confirmationPolicy: new TweakRecoveryConfirmationPolicy(), hkcuEngine: tweakEngine);
            var tweakManager = new TweakManager(tweakCatalog, tweakEngine, tweakRecovery, store,
                realTweaksEnabled ? TweakExecutionMode.RealControlled : TweakExecutionMode.RealDisabled);

            // The native observer is read-only and queried from Debloat on request. Simulations keep their fixture inputs.
            var debloatCatalog = DebloatCatalog.CreateDefault();
            var debloatInventory = SyntheticDebloatInventory.Create(debloatCatalog.All);
            var debloatManager = new DebloatManager(debloatCatalog, debloatInventory,
                new SyntheticDebloatRuntimeFactory(debloatInventory), store.CreateDebloatJournal(), transactionLocks);
            var debloatObserver = new DebloatInventoryObserver(debloatCatalog, new NativeAppxInventoryReader());

            // R1 — Windows Repair Center. Nenhum executor real é composto: o gate é independente do Tweaks
            // e a execução disponível é exclusivamente sintética, com fixtures marcadas.
            var repairCatalog = RepairCatalog.CreateDefault();
            var repairVolumes = new WindowsRepairVolumeProvider();
            var repairPreflight = new RepairPreflightEngine(repairCatalog,
                new WindowsRepairSystemProfileReader(), repairVolumes, new WindowsRepairToolLocator());
            var repairCenter = new RepairCenter(repairCatalog, repairPreflight,
                new SyntheticRepairExecutor(), store,
                () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var repairManager = new RepairManager(repairCatalog, repairCenter, repairVolumes);

            var fullAudit = new FullAuditDiagnostics(hardwareRunner, eventRunner, performanceRunner,
                networkRunner, graphicsRunner, monitoringRunner, store);
            MainWindow = new MainWindow(new AuditRunner(WindowsCollectors.CreateBasic(), full: fullAudit, checkpoints: store),
                hardwareRunner, eventRunner,
                performanceRunner, networkRunner, graphicsRunner, correlationRunner, monitoringRunner,
                tweakManager, repairManager, store, store, store, store, store, store, store, store,
                debloat: debloatManager, recovery: new RecoveryCenter(store, tweaks: tweakManager),
                debloatObserver: debloatObserver, operations: new ProductOperationCoordinator(transactionLocks));
            MainWindow.Show();
        }
        catch (Exception error)
        {
            LogFailure(error); MessageBox.Show("Não foi possível iniciar o aplicativo. Consulte logs/application.log.", "SentinelCore AI"); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }

    internal static void LogFailure(Exception error) => log.Write(error);
}
