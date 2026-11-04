using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Infrastructure.Repairs;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Infrastructure.Windows.Monitoring;
using SentinelCore.Infrastructure.Windows.Network;
using SentinelCore.Persistence;

namespace SentinelCore.App;

public partial class App : System.Windows.Application
{
    private Mutex? instanceMutex;
    private static string dataRoot = MemoryStore.DefaultRoot;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            dataRoot = ResolveDataRoot(e.Args) ?? MemoryStore.DefaultRoot;
        }
        catch (Exception error)
        {
            LogFailure(error);
            MessageBox.Show("Diretório de dados inválido: " + error.Message, "SentinelCore AI");
            Shutdown(1);
            return;
        }
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
            var graphicsRunner = new GraphicsInvestigationRunner(
                WindowsHardwareCollectors.Create(),
                WindowsEventEvidenceCollectors.Create());
            var store = new MemoryStore(dataRoot);
            var correlationRunner = new EvidenceCorrelationRunner(new CorrelationEvidenceReader(
                audits: store, hardware: store, events: store, performance: store, network: store,
                graphics: store, monitoring: store));
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
                realTweaksEnabled, T2bCatalog.OperationIds);
            var transactionLocks = new FileExclusiveLockFactory(dataRoot);
            var tweakEngine = new SafetyEngine(tweakCatalog, tweakProfile, tweakReader,
                new HkcuProtectionProvider(), store, tweakExecutor, transactionLocks,
                confirmationPolicy: new TweakAuthorizationConfirmationPolicy());
            var tweakRecovery = new RecoveryService(store, tweakReader, tweakExecutor, transactionLocks,
                catalog: tweakCatalog, confirmationPolicy: new TweakRecoveryConfirmationPolicy());
            var tweakManager = new TweakManager(tweakCatalog, tweakEngine, tweakRecovery, store,
                realTweaksEnabled ? TweakExecutionMode.RealControlled : TweakExecutionMode.RealDisabled);

            // T3: metadata fixtures only. No native package inventory or removal executor is composed.
            var debloatCatalog = DebloatCatalog.CreateDefault();
            var debloatInventory = SyntheticDebloatInventory.Create(debloatCatalog.All);
            var debloatManager = new DebloatManager(debloatCatalog, debloatInventory,
                new SyntheticDebloatRuntimeFactory(debloatInventory), store.CreateDebloatJournal(), transactionLocks);

            // R1 — Windows Repair Center. Nenhum executor real é composto: o gate é independente do Tweaks
            // e a execução disponível é exclusivamente sintética, com fixtures marcadas.
            var repairCatalog = RepairCatalog.CreateDefault();
            var repairVolumes = new WindowsRepairVolumeProvider();
            var repairPreflight = new RepairPreflightEngine(repairCatalog,
                new WindowsRepairSystemProfileReader(), repairVolumes, new WindowsRepairToolLocator());
            var repairCenter = new RepairCenter(repairCatalog, repairPreflight,
                new SyntheticRepairExecutor(), store);
            var repairManager = new RepairManager(repairCatalog, repairCenter, repairVolumes);

            MainWindow = new MainWindow(new AuditRunner(WindowsCollectors.CreateBasic()),
                new HardwareRunner(WindowsHardwareCollectors.Create()),
                new EventInvestigationRunner(WindowsEventEvidenceCollectors.Create()),
                performanceRunner, networkRunner, graphicsRunner, correlationRunner, monitoringRunner,
                tweakManager, repairManager, store, store, store, store, store, store, store, store,
                debloat: debloatManager, recovery: new RecoveryCenter(store));
            MainWindow.Show();
        }
        catch (Exception error)
        {
            LogFailure(error); MessageBox.Show("Não foi possível iniciar o aplicativo. Consulte logs/application.log.", "SentinelCore AI"); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }

    /// <summary>
    /// Hosts de validação podem injetar um diretório de dados absoluto e isolado com <c>--data-root</c>. Sem o
    /// argumento, o comportamento padrão (<c>%LOCALAPPDATA%\SentinelCoreAI</c>) permanece inalterado.
    /// </summary>
    private static string? ResolveDataRoot(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            string? value = null;
            if (args[index].StartsWith("--data-root=", StringComparison.Ordinal))
                value = args[index]["--data-root=".Length..];
            else if (args[index] == "--data-root")
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("O argumento --data-root exige um caminho absoluto.");
                value = args[++index];
            }
            if (value is null) continue;
            if (!Path.IsPathFullyQualified(value))
                throw new ArgumentException("O diretório de dados injetado deve ser um caminho absoluto.");
            var full = Path.GetFullPath(value);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("Compartilhamentos de rede não são aceitos como diretório de dados.");
            var driveRoot = Path.GetPathRoot(full) ?? string.Empty;
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                    driveRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("O diretório de dados não pode ser a raiz de uma unidade.");
            foreach (var forbidden in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                         Environment.GetFolderPath(Environment.SpecialFolder.System),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
                if (IsWithin(full, forbidden))
                    throw new ArgumentException($"O diretório de dados não pode ficar em {forbidden}.");
            return full;
        }
        return null;
    }

    private static bool IsWithin(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var normalized = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return path.Equals(normalized.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    internal static void LogFailure(Exception error)
    {
        try
        {
            var directory = Path.Combine(dataRoot, "logs"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "application.log"), $"{DateTimeOffset.UtcNow:O} {error}\n");
        }
        catch (Exception loggingError) { System.Diagnostics.Debug.WriteLine($"Falha ao gravar log: {loggingError.GetType().Name}"); }
    }
}
