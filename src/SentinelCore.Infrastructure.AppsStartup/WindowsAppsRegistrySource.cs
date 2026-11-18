using Microsoft.Win32;
using System.Security;
using System.Runtime.Versioning;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

/// <summary>Fonte não composta no App; acessa somente as chaves usadas pelos provedores de inventário.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppsRegistrySource : IAppsRegistrySource, IRunRegistrySource
{
    private static readonly HashSet<string> ApplicationFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "DisplayName", "Publisher", "DisplayVersion", "InstallLocation", "WindowsInstaller", "SystemComponent", "ParentKeyName"
    };

    public (IReadOnlyList<RunRegistryValue> Values, IReadOnlyList<string> Warnings) ReadRunValues()
    {
        var result = new List<RunRegistryValue>();
        var warnings = new List<string>();
        foreach (var scope in new[] { AppsScope.CurrentUser, AppsScope.Machine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            // HKCU\Software é compartilhada entre visões; lê-la duas vezes duplicaria entradas.
            if (scope == AppsScope.CurrentUser && view == RegistryView.Registry32) continue;
            try
            {
                var hive = scope == AppsScope.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(RunKeyStartupProvider.RunPath, false);
                if (key is null) { warnings.Add($"inaccessible:{scope}:{view}"); continue; }
                foreach (var name in key.GetValueNames())
                {
                    var kind = key.GetValueKind(name);
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (raw is not string content) { warnings.Add($"unsupported-value:{scope}:{view}:{name}"); continue; }
                    result.Add(new(scope, hive.ToString(), RunKeyStartupProvider.RunPath, view.ToString(), name,
                        kind.ToString(), content, StartupConfiguredState.Unknown));
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
            { warnings.Add($"inaccessible:{scope}:{view}"); }
        }
        return (result, warnings);
    }

    public IReadOnlyDictionary<string, object?>? ReadValues(AppsScope scope, string path)
    {
        if (path is not (RunKeyStartupProvider.RunPath or RunKeyStartupProvider.ApprovedPath)) return null;
        try
        {
            using var root = OpenRoot(scope);
            using var key = root?.OpenSubKey(path, writable: false);
            return key is null ? null : Values(key, null);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
        { return null; }
    }

    public IReadOnlyList<RegistryKeySnapshot>? ReadSubKeys(AppsScope scope, string path)
    {
        if (path is not (UninstallRegistryApplicationProvider.UninstallPath or UninstallRegistryApplicationProvider.UninstallPathWow)) return null;
        try
        {
            var hive = scope switch { AppsScope.CurrentUser => RegistryHive.CurrentUser, AppsScope.Machine => RegistryHive.LocalMachine, _ => (RegistryHive)0 };
            if ((int)hive == 0) return null;
            using var root = RegistryKey.OpenBaseKey(hive, path == UninstallRegistryApplicationProvider.UninstallPathWow ? RegistryView.Registry32 : RegistryView.Registry64);
            using var key = root.OpenSubKey(UninstallRegistryApplicationProvider.UninstallPath, writable: false);
            if (key is null) return null;
            var result = new List<RegistryKeySnapshot>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(name, writable: false);
                if (child is null) return null;
                result.Add(new(name, Values(child, ApplicationFields)));
            }
            return result;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
        { return null; }
    }

    private static RegistryKey? OpenRoot(AppsScope scope) => scope switch
    {
        AppsScope.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        AppsScope.Machine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        _ => null
    };

    private static IReadOnlyDictionary<string, object?> Values(RegistryKey key, HashSet<string>? allowed)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames())
            if (allowed is null || allowed.Contains(name))
                values.Add(name, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        return values;
    }
}
