using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

/// <summary>Inicialização sintética em memória. Fixtures artificiais; nunca consulta nem altera o Windows.</summary>
public sealed class SyntheticStartupStore : IStartupInventoryProvider, IStartupChangeExecutor
{
    private readonly object sync = new();
    private readonly List<StartupEntry> entries;
    private readonly Dictionary<string, AppsOperationOutcome> injected = new(StringComparer.Ordinal);

    public SyntheticStartupStore(IEnumerable<StartupEntry> entries) => this.entries = entries.ToList();

    public bool IsSynthetic => true;

    public static SyntheticStartupStore CreateDefault()
    {
        static StartupEntry E(string name, StartupSourceKind source, AppsScope scope, StartupConfiguredState state,
            StartupRuntimeState runtime, string type, string reference, string? publisher = "Fixture Publisher") =>
            new(AppsIdentity.Startup(source, scope, name), name, publisher, source, reference, scope, state, runtime, type,
                AppsDataOrigin.Synthetic);
        return new(
        [
            E("Fixture Chat Client", StartupSourceKind.RegistryRun, AppsScope.CurrentUser, StartupConfiguredState.Enabled, StartupRuntimeState.Running, "Run", @"C:\Fixture\Chat\chat.exe --background"),
            E("Fixture Cloud Sync", StartupSourceKind.RegistryRun, AppsScope.CurrentUser, StartupConfiguredState.Enabled, StartupRuntimeState.NotVerified, "Run", @"C:\Fixture\Sync\sync.exe"),
            E("Fixture Game Launcher", StartupSourceKind.StartupFolder, AppsScope.CurrentUser, StartupConfiguredState.Disabled, StartupRuntimeState.NotRunning, "Atalho", @"Startup\Fixture Game Launcher.lnk"),
            E("Fixture Machine Updater", StartupSourceKind.RegistryRun, AppsScope.Machine, StartupConfiguredState.Enabled, StartupRuntimeState.NotRunning, "Run", @"C:\Fixture\Updater\update.exe /check"),
            E("Fixture Packaged Widget", StartupSourceKind.PackagedStartupTask, AppsScope.CurrentUser, StartupConfiguredState.Enabled, StartupRuntimeState.NotVerified, "StartupTask", "Fixture.Widget_1.0_x64__fixture!WidgetStartup"),
            E("Fixture Vendor Task", StartupSourceKind.ScheduledTask, AppsScope.Machine, StartupConfiguredState.Enabled, StartupRuntimeState.NotVerified, "Tarefa", @"\Fixture\VendorTask"),
            E("Fixture RunOnce Setup", StartupSourceKind.RegistryRunOnce, AppsScope.CurrentUser, StartupConfiguredState.Enabled, StartupRuntimeState.NotVerified, "RunOnce", @"C:\Fixture\Setup\once.exe"),
            E("Fixture Security Agent", StartupSourceKind.SecurityComponent, AppsScope.Machine, StartupConfiguredState.Enabled, StartupRuntimeState.Running, "Segurança", "FixtureSecurityAgent", publisher: null),
            E("Fixture Unknown State", StartupSourceKind.RegistryRun, AppsScope.CurrentUser, StartupConfiguredState.Unknown, StartupRuntimeState.NotVerified, "Run", @"C:\Fixture\Unknown\u.exe")
        ]);
    }

    /// <summary>Teste: força o resultado da próxima execução sobre a entrada (falha/indeterminação/etc.).</summary>
    public void InjectNextOutcome(string stableId, AppsOperationOutcome outcome)
    { lock (sync) injected[stableId] = outcome; }

    /// <summary>Teste: simula alteração concorrente de estado por outro agente.</summary>
    public void ChangeExternally(string stableId, StartupConfiguredState state)
    {
        lock (sync)
        {
            var i = entries.FindIndex(e => e.StableId == stableId);
            entries[i] = entries[i] with { State = state };
        }
    }

    public Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(new StartupInventoryResult(entries.ToArray(), [], true));
    }

    public Task<StartupChangeResult> ExecuteAsync(StartupChangeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var i = entries.FindIndex(e => e.StableId == request.Entry.StableId);
            if (i < 0) return Task.FromResult(new StartupChangeResult(AppsOperationOutcome.Failed, "missing", null));
            var current = entries[i];
            // Fronteira do executor: só origens suportadas e apenas itens sintéticos.
            if (current.Origin != AppsDataOrigin.Synthetic || current.Source is not (StartupSourceKind.RegistryRun
                or StartupSourceKind.StartupFolder or StartupSourceKind.PackagedStartupTask))
                return Task.FromResult(new StartupChangeResult(AppsOperationOutcome.Blocked, "unsupported-source", null));
            if (current.StateToken != request.ExpectedStateToken)
                return Task.FromResult(new StartupChangeResult(AppsOperationOutcome.Conflict, "concurrent-change", null));
            if (injected.Remove(current.StableId, out var forced))
                return Task.FromResult(new StartupChangeResult(forced, "injected", forced == AppsOperationOutcome.Indeterminate ? current.Reference : null));
            var target = request.Action == StartupAction.Disable ? StartupConfiguredState.Disabled : StartupConfiguredState.Enabled;
            // Só a configuração muda: a referência original é preservada e o estado de execução não é tocado.
            entries[i] = current with { State = target };
            return Task.FromResult(new StartupChangeResult(AppsOperationOutcome.Succeeded, "ok", current.Reference));
        }
    }
}

/// <summary>Aplicativos instalados sintéticos em memória; a desinstalação apenas remove a linha da fixture.</summary>
public sealed class SyntheticApplicationStore : IInstalledApplicationProvider, IApplicationUninstallExecutor
{
    private readonly object sync = new();
    private readonly List<InstalledApplicationEntry> entries;
    private readonly Dictionary<string, AppsOperationOutcome> injected = new(StringComparer.Ordinal);

    public SyntheticApplicationStore(IEnumerable<InstalledApplicationEntry> entries) => this.entries = entries.ToList();

    public bool IsSynthetic => true;

    public static SyntheticApplicationStore CreateDefault()
    {
        static InstalledApplicationEntry A(string name, InstalledApplicationKind kind, AppsScope scope, string key,
            string? version = "1.0.0", string? productCode = null, string? package = null, bool system = false,
            string source = "Uninstall") =>
            new(AppsIdentity.Application(kind, scope, key), name, "Fixture Publisher", version, kind, source, scope,
                $@"C:\Fixture\{name}", productCode, package, system, AppsDataOrigin.Synthetic);
        return new(
        [
            A("Fixture Chat Client", InstalledApplicationKind.Msix, AppsScope.CurrentUser, "Fixture.Chat_1.0.0.0_x64__fixture", package: "Fixture.Chat_1.0.0.0_x64__fixture", source: "AppModel"),
            A("Fixture Cloud Sync", InstalledApplicationKind.Win32Registry, AppsScope.CurrentUser, "FixtureCloudSync"),
            A("Fixture Office Suite", InstalledApplicationKind.Msi, AppsScope.Machine, "{11111111-1111-1111-1111-111111111111}", "2.5.1", "{11111111-1111-1111-1111-111111111111}"),
            A("Fixture Office Suite", InstalledApplicationKind.Msi, AppsScope.CurrentUser, "{22222222-2222-2222-2222-222222222222}", "2.5.1", "{22222222-2222-2222-2222-222222222222}"),
            A("Fixture Game", InstalledApplicationKind.Win32Registry, AppsScope.Machine, "FixtureGame", "7.0"),
            A("Fixture Runtime Framework", InstalledApplicationKind.Msix, AppsScope.CurrentUser, "Fixture.Framework_1.0.0.0_x64__fixture", package: "Fixture.Framework_1.0.0.0_x64__fixture", system: true, source: "AppModel"),
            A("Fixture All Users Package", InstalledApplicationKind.Msix, AppsScope.Machine, "Fixture.AllUsers_1.0.0.0_x64__fixture", package: "Fixture.AllUsers_1.0.0.0_x64__fixture", source: "AppModel")
        ]);
    }

    public void InjectNextOutcome(string stableId, AppsOperationOutcome outcome)
    { lock (sync) injected[stableId] = outcome; }

    public void ChangeExternally(string stableId, string version)
    {
        lock (sync)
        {
            var i = entries.FindIndex(e => e.StableId == stableId);
            entries[i] = entries[i] with { Version = version };
        }
    }

    public Task<InstalledApplicationInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(new InstalledApplicationInventoryResult(entries.ToArray(), [], true));
    }

    public Task<UninstallResult> ExecuteAsync(UninstallRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var i = entries.FindIndex(e => e.StableId == request.Entry.StableId);
            if (i < 0) return Task.FromResult(new UninstallResult(AppsOperationOutcome.Failed, "missing"));
            var current = entries[i];
            if (current.Origin != AppsDataOrigin.Synthetic || current.IsSystemOrFramework || request.Method == UninstallMethod.None)
                return Task.FromResult(new UninstallResult(AppsOperationOutcome.Blocked, "unsupported"));
            if (current.StateToken != request.ExpectedStateToken)
                return Task.FromResult(new UninstallResult(AppsOperationOutcome.Conflict, "concurrent-change"));
            if (injected.Remove(current.StableId, out var forced))
                return Task.FromResult(new UninstallResult(forced, "injected"));
            entries.RemoveAt(i);
            return Task.FromResult(new UninstallResult(AppsOperationOutcome.Succeeded, "ok"));
        }
    }
}
