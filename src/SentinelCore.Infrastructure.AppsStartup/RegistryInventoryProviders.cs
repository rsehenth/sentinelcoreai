using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

public sealed record RegistryKeySnapshot(string KeyName, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// Fonte de Registro SOMENTE LEITURA, com caminhos fixos escolhidos pelos provedores. A implementação Windows não é
/// composta no App; os provedores também podem operar sobre fakes.
/// Retornar null significa "inacessível" (nunca "vazio").
/// </summary>
public interface IAppsRegistrySource
{
    IReadOnlyList<RegistryKeySnapshot>? ReadSubKeys(AppsScope scope, string path);
    IReadOnlyDictionary<string, object?>? ReadValues(AppsScope scope, string path);
}

public sealed record RunRegistryValue(AppsScope Scope, string Hive, string Path, string View, string Name,
    string ValueKind, string Content, StartupConfiguredState State);

public interface IRunRegistrySource
{
    (IReadOnlyList<RunRegistryValue> Values, IReadOnlyList<string> Warnings) ReadRunValues();
}

/// <summary>Entradas convencionais Run (HKCU/HKLM). RunOnce não é lido como aplicativo comum.</summary>
public sealed class RunKeyStartupProvider(IAppsRegistrySource source) : IStartupInventoryProvider
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (source is IRunRegistrySource detailed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (values, errors) = detailed.ReadRunValues();
            var items = values.Select(v => new StartupEntry(
                AppsIdentity.Startup(StartupSourceKind.RegistryRun, v.Scope, $"{v.Hive}|{v.Path}|{v.View}|{v.Name}"),
                v.Name, null, StartupSourceKind.RegistryRun, v.Content, v.Scope, v.State,
                StartupRuntimeState.NotVerified, "Run", AppsDataOrigin.Operational)
                { RegistryIdentity = new(v.Hive, v.Path, v.View, v.Name, v.ValueKind) }).ToArray();
            return Task.FromResult(new StartupInventoryResult(items, errors, errors.Count == 0));
        }
        var entries = new List<StartupEntry>();
        var warnings = new List<string>();
        foreach (var scope in new[] { AppsScope.CurrentUser, AppsScope.Machine })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.ReadValues(scope, RunPath) is not { } values) { warnings.Add($"inaccessible:{scope}"); continue; }
            var approved = source.ReadValues(scope, ApprovedPath);
            foreach (var (name, value) in values)
            {
                if (value is not string command || string.IsNullOrWhiteSpace(name)) { warnings.Add($"malformed:{name}"); continue; }
                entries.Add(new(AppsIdentity.Startup(StartupSourceKind.RegistryRun, scope, name), name, null,
                    StartupSourceKind.RegistryRun, command, scope, State(approved, name), StartupRuntimeState.NotVerified,
                    "Run", AppsDataOrigin.Operational));
            }
        }
        return Task.FromResult(new StartupInventoryResult(entries, warnings, warnings.Count == 0));
    }

    // Convenção observada do Explorer (não documentada): ausência = habilitado; primeiro byte par = habilitado, ímpar = desabilitado.
    private static StartupConfiguredState State(IReadOnlyDictionary<string, object?>? approved, string name)
    {
        if (approved is null) return StartupConfiguredState.Unknown;
        if (!approved.TryGetValue(name, out var raw)) return StartupConfiguredState.Enabled;
        return raw is byte[] { Length: > 0 } bytes
            ? bytes[0] % 2 == 0 ? StartupConfiguredState.Enabled : StartupConfiguredState.Disabled
            : StartupConfiguredState.Unknown;
    }
}

/// <summary>
/// Entradas de desinstalação do Windows (sem Win32_Product e sem ler UninstallString). Produto MSI só é reconhecido
/// quando o nome da chave é um GUID e WindowsInstaller=1; os demais Win32 ficam sem mecanismo de desinstalação.
/// </summary>
public sealed class UninstallRegistryApplicationProvider(IAppsRegistrySource source) : IInstalledApplicationProvider
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallPathWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<InstalledApplicationInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        var entries = new List<InstalledApplicationEntry>();
        var warnings = new List<string>();
        foreach (var (scope, path) in new[]
            { (AppsScope.CurrentUser, UninstallPath), (AppsScope.Machine, UninstallPath), (AppsScope.Machine, UninstallPathWow) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.ReadSubKeys(scope, path) is not { } keys) { warnings.Add($"inaccessible:{scope}:{path}"); continue; }
            foreach (var key in keys)
            {
                if (Text(key, "DisplayName") is not { } name) { warnings.Add($"no-display-name:{key.KeyName}"); continue; }
                var msi = Guid.TryParse(key.KeyName, out _) && key.Values.TryGetValue("WindowsInstaller", out var wi) && wi is 1;
                var kind = msi ? InstalledApplicationKind.Msi : InstalledApplicationKind.Win32Registry;
                var identityKey = path == UninstallPathWow ? "wow64|" + key.KeyName : key.KeyName;
                var system = key.Values.TryGetValue("SystemComponent", out var sc) && sc is 1 || Text(key, "ParentKeyName") is not null;
                entries.Add(new(AppsIdentity.Application(kind, scope, identityKey), name, Text(key, "Publisher"),
                    Text(key, "DisplayVersion"), kind, path, scope, Text(key, "InstallLocation"),
                    msi ? key.KeyName : null, null, system, AppsDataOrigin.Operational));
            }
        }
        return Task.FromResult(new InstalledApplicationInventoryResult(entries, warnings, warnings.Count == 0));
    }

    private static string? Text(RegistryKeySnapshot key, string name) =>
        key.Values.TryGetValue(name, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
