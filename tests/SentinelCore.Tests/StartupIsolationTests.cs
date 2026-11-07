using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Repairs;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// V2 — isolamento da inicialização. Somente raízes temporárias e provedores artificiais: nada aqui executa
/// <c>App.OnStartup</c>, consulta o Windows real ou usa o diretório operacional do SentinelCore.
/// </summary>
public sealed class StartupIsolationTests : IDisposable
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), "SentinelCoreV2Tests", Guid.NewGuid().ToString("N"));
    private string DefaultRoot => Path.Combine(sandbox, "default-root");
    private string Chosen => Path.Combine(sandbox, "chosen");

    public StartupIsolationTests() => Directory.CreateDirectory(sandbox);

    public void Dispose()
    {
        try { Directory.Delete(sandbox, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string AppSource() => File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "App.xaml.cs"));

    private static string[] Tree(string root) => !Directory.Exists(root) ? [] :
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order().ToArray();

    [Fact]
    public void ValidDataRootSelectsOnlyTheIntendedRootWithoutCreatingAnything()
    {
        foreach (var args in new[] { new[] { "--data-root", Chosen }, new[] { "--data-root=" + Chosen } })
            Assert.Equal(Path.GetFullPath(Chosen), DataRootArgument.Resolve(args, DefaultRoot, []));
        Assert.False(Directory.Exists(Chosen));
        Assert.False(Directory.Exists(DefaultRoot));
        Assert.Equal(Path.GetFullPath(DefaultRoot), DataRootArgument.Resolve([], DefaultRoot, []));
        Assert.Equal(Path.GetFullPath(DefaultRoot), DataRootArgument.Resolve(["--outro", "x"], DefaultRoot, []));
        Assert.False(Directory.Exists(DefaultRoot));
    }

    [Fact]
    public void InvalidExplicitDataRootFailsClosedWithoutFallbackOrDirectories()
    {
        string[] invalid =
        [
            "", "   ", @"relativo\dados", @".\dados", @"..\dados", @"\dados", "C:dados",
            @"\\servidor\compartilhamento\dados", @"\\?\C:\SentinelCoreV2\dados",
            @"C:\SentinelCoreV2\dados:fluxo", "C:\\SentinelCoreV2\\dad\0os"
        ];
        foreach (var value in invalid)
        {
            Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", value], DefaultRoot, []));
            Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root=" + value], DefaultRoot, []));
        }
        Assert.False(Directory.Exists(DefaultRoot));
    }

    [Fact]
    public void MissingDuplicateRootAndForbiddenLocationsFailClosed()
    {
        var drive = Path.GetPathRoot(sandbox)!;
        var forbidden = Path.Combine(sandbox, "proibido");
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root"], DefaultRoot, []));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(
            ["--data-root", Chosen, "--data-root", Chosen + "2"], DefaultRoot, []));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", drive], DefaultRoot, []));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(
            ["--data-root", Path.Combine(forbidden, "dados")], DefaultRoot, [forbidden]));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", forbidden], DefaultRoot, [forbidden]));
        // A raiz padrão não é aceita como "destino explícito": omitir o argumento é o caminho previsto.
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", DefaultRoot], DefaultRoot, []));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(
            ["--data-root", Path.Combine(DefaultRoot, "filho")], DefaultRoot, []));
        // Diretórios reais do sistema (somente comparação de texto: nada é lido nem criado).
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(
            ["--data-root", Path.Combine(windows, "SentinelCoreV2")], DefaultRoot));
        Assert.False(Directory.Exists(DefaultRoot));
        Assert.False(Directory.Exists(forbidden));
        Assert.False(Directory.Exists(Chosen));
    }

    [Fact]
    public void DataRootCollidingWithAFileOrBelowAFileIsRejected()
    {
        var file = Path.Combine(sandbox, "arquivo.txt");
        File.WriteAllText(file, "fixture");
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", file], DefaultRoot, []));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(
            ["--data-root", Path.Combine(file, "dados")], DefaultRoot, []));
    }

    [Fact]
    public void DataRootThatIsOrSitsBelowAReparsePointIsRejected()
    {
        // Atributos artificiais: o destino (ainda inexistente) fica sob um ancestral que é link/junção.
        var junction = Path.Combine(sandbox, "juncao");
        FileAttributes Attributes(string path) =>
            string.Equals(path, junction, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : throw new DirectoryNotFoundException(path);
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(
            ["--data-root", Path.Combine(junction, "dados")], DefaultRoot, [], Attributes));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", junction], DefaultRoot, [], Attributes));
        Assert.Equal(Path.GetFullPath(Chosen), DataRootArgument.Resolve(["--data-root", Chosen], DefaultRoot, [], Attributes));
        // Falha ao ler atributos (por exemplo, acesso negado) também é fail-closed.
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", Chosen], DefaultRoot, [],
            _ => throw new UnauthorizedAccessException()));
        Assert.False(Directory.Exists(junction));
    }

    [Fact]
    public void DataRootBehindARealSymbolicLinkIsRejectedWhenTheEnvironmentAllowsCreatingOne()
    {
        var target = Path.Combine(sandbox, "alvo-real");
        var link = Path.Combine(sandbox, "link");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return; // sem privilégio de link simbólico neste ambiente: o caso fica não exercitado (ver relatório V2)
        }
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(["--data-root", link], DefaultRoot, []));
        Assert.Throws<ArgumentException>(() => DataRootArgument.Resolve(
            ["--data-root", Path.Combine(link, "dados")], DefaultRoot, []));
        Assert.Empty(Tree(target));
    }

    [Fact]
    public void ApplicationLogWritesOnlyUnderItsAuthorizedRootAndNothingWithoutOne()
    {
        new ApplicationLog(null).Write(new InvalidOperationException("sem raiz"));
        Assert.False(Directory.Exists(DefaultRoot));

        new ApplicationLog(Chosen).Write(new InvalidOperationException("com raiz"));
        Assert.Equal(["logs", "logs/application.log"], Tree(Chosen));
        Assert.Contains("com raiz", File.ReadAllText(Path.Combine(Chosen, "logs", "application.log")));
        Assert.False(Directory.Exists(DefaultRoot));
    }

    [Fact]
    public void StartupSourceNeverLogsOrUsesTheDefaultRootWhileDataRootIsUnvalidated()
    {
        var source = AppSource();
        var resolve = source.IndexOf("DataRootArgument.Resolve(", StringComparison.Ordinal);
        var arm = source.IndexOf("log = new ApplicationLog(dataRoot)", StringComparison.Ordinal);
        Assert.True(resolve > 0 && arm > resolve);
        var failure = source[resolve..arm];
        Assert.DoesNotContain("LogFailure", failure);
        Assert.Contains("Shutdown(1)", failure);
        Assert.DoesNotContain("DefaultRoot", source);
        Assert.Contains("private static ApplicationLog log = new(null)", source);
        // Nada que possa gravar antes da validação.
        var before = source[..resolve];
        Assert.DoesNotContain("new MemoryStore", before);
        Assert.DoesNotContain("FileExclusiveLockFactory", before);
        Assert.DoesNotContain("DispatcherUnhandledException", before);
        Assert.DoesNotContain("Mutex", before[before.IndexOf("OnStartup", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task CompositionOnlyTouchesTheChosenRootAndSyntheticStartupLoadsStayInsideIt()
    {
        var lockFactory = new FileExclusiveLockFactory(Chosen);
        Assert.Equal(["data", "data/locks"], Tree(Chosen));
        var store = new MemoryStore(Chosen + "-store");
        Assert.Empty(Tree(Chosen + "-store"));
        Assert.False(Directory.Exists(DefaultRoot));
        GC.KeepAlive(lockFactory);

        // Mesmos carregadores que MainWindow.OnLoaded chama; só a raiz temporária pode receber arquivos.
        await store.LoadAsync();
        await store.LoadSnapshotsAsync();
        await store.LoadInvestigationsAsync();
        await store.LoadSessionsAsync();
        await store.LoadNetworkSessionsAsync();
        await store.LoadGraphicsInvestigationsAsync();
        await store.LoadCorrelationInvestigationsAsync();
        await store.LoadMonitoringSessionsAsync();
        await store.LoadRepairsAsync();
        var top = Directory.EnumerateFileSystemEntries(Chosen + "-store").Select(Path.GetFileName).Order().ToArray();
        Assert.All(top, entry => Assert.Contains(entry, new[] { "data", "logs", "memory", "reports" }));
        Assert.False(Directory.Exists(DefaultRoot));
    }

    private sealed class CountingProfile : IRepairSystemProfileReader
    {
        public int Calls;
        public RepairSystemProfile Read() { Calls++; return new FakeRepairProfile().Profile; }
    }

    private sealed class CountingVolumes : IRepairVolumeProvider
    {
        public int Calls;
        public Task<IReadOnlyList<RepairVolume>> GetVolumesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return new FakeRepairVolumes().GetVolumesAsync(cancellationToken);
        }
    }

    private sealed class CountingTools : IRepairToolLocator
    {
        public int Calls;
        public RepairToolLocation Resolve(RepairTool tool) { Calls++; return new FakeRepairToolLocator().Resolve(tool); }
    }

    [Fact]
    public async Task RepairCompositionAndStartupHistoryLoadNeverRunThePreflight()
    {
        var profile = new CountingProfile(); var volumes = new CountingVolumes(); var tools = new CountingTools();
        var userProfileCalls = 0;
        var catalog = RepairCatalog.CreateDefault();
        var store = new MemoryStore(Chosen);
        var center = new RepairCenter(catalog, new RepairPreflightEngine(catalog, profile, volumes, tools),
            new SyntheticRepairExecutor(), store, () => { userProfileCalls++; return @"C:\Fixture\User"; });
        var manager = new RepairManager(catalog, center, volumes);
        void AssertNothingRead() => Assert.Equal((0, 0, 0, 0), (profile.Calls, volumes.Calls, tools.Calls, userProfileCalls));

        AssertNothingRead();                    // construção
        var history = await manager.LoadHistoryAsync();   // o que OnLoaded passou a chamar
        Assert.Empty(history.Records);
        AssertNothingRead();

        var view = await manager.LoadAsync();   // entrada na página Reparos / Atualizar
        Assert.NotEmpty(view.Operations);
        Assert.True(volumes.Calls > 0 && profile.Calls > 0 && tools.Calls > 0);
    }

    [Fact]
    public void StartupLoadsRepairHistoryOnlyAndPreflightWaitsForThePageOrRefresh()
    {
        var shell = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.xaml.cs"));
        var loaded = shell[shell.IndexOf("private async void OnLoaded", StringComparison.Ordinal)..
            shell.IndexOf("private async Task ReloadAsync", StringComparison.Ordinal)];
        Assert.Contains("ReloadRepairHistoryAsync()", loaded);
        Assert.DoesNotContain("ReloadRepairsAsync", loaded);

        var repairs = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.Repairs.cs"));
        var selection = repairs[repairs.IndexOf("private void OnRepairsPageSelectionChanged", StringComparison.Ordinal)..
            repairs.IndexOf("private void ScrollRepairsToTop", StringComparison.Ordinal)];
        Assert.Contains("_ = ReloadRepairsAsync();", selection);
        Assert.Contains("await ReloadRepairsAsync(", repairs[repairs.IndexOf("OnRepairsRefresh", StringComparison.Ordinal)..]);
        // O estado inicial não sugere verificação concluída.
        Assert.Contains("ainda não lidos", repairs);
        Assert.Contains("Nenhuma ferramenta foi executada", repairs);
    }

    [Fact]
    public void FiveBuildGatesRemainClosed()
    {
        Assert.False(RealTweakBuildGate.AllowsRealExecution([]));
        Assert.False(RealTweakBuildGate.AllowsRealExecution([RealTweakBuildGate.EnableArgument]));
        Assert.False(RealRepairBuildGate.AllowsRealExecution([]));
        Assert.False(RealRepairBuildGate.AllowsRealExecution([RealRepairBuildGate.EnableArgument]));
        Assert.False(RealDebloatBuildGate.AllowsRealExecution());
        Assert.False(NativeDebloatInventoryGate.AllowsActivation());
        Assert.False(DebloatRemovalBuildGate.AllowsRealExecution());
        // O argumento de dados não é caminho de contorno: ele não interage com os gates.
        Assert.False(RealTweakBuildGate.AllowsRealExecution(["--data-root", Chosen, RealTweakBuildGate.EnableArgument]));
        Assert.False(RealRepairBuildGate.AllowsRealExecution(["--data-root", Chosen, RealRepairBuildGate.EnableArgument]));
    }
}
