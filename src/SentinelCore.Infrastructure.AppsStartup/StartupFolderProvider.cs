using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

public sealed record StartupFile(string FullPath, string Name, string Type, bool? Readable);

public interface IStartupFolderSource
{
    IReadOnlyList<StartupFile>? Read(AppsScope scope);
}

public sealed class StartupFolderProvider(IStartupFolderSource source) : IStartupInventoryProvider
{
    public Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        var entries = new List<StartupEntry>();
        var warnings = new List<string>();
        foreach (var scope in new[] { AppsScope.CurrentUser, AppsScope.Machine })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Read(scope) is not { } files) { warnings.Add($"inaccessible:{scope}"); continue; }
            foreach (var file in files)
            {
                if (file.Readable is null) warnings.Add($"readability-unverified:{scope}:{file.Name}");
                else if (file.Readable == false) warnings.Add($"unreadable:{scope}:{file.Name}");
                entries.Add(new(AppsIdentity.Startup(StartupSourceKind.StartupFolder, scope, file.FullPath),
                    file.Name, null, StartupSourceKind.StartupFolder, file.FullPath, scope,
                    StartupConfiguredState.Unknown,
                    StartupRuntimeState.NotVerified, file.Type, AppsDataOrigin.Operational) { ReferenceReadable = file.Readable });
            }
        }
        return Task.FromResult(new StartupInventoryResult(entries, warnings, warnings.Count == 0));
    }
}

/// <summary>Somente enumeração local; não resolve atalhos nem segue pontos de reanálise.</summary>
public sealed class WindowsStartupFolderSource : IStartupFolderSource
{
    public IReadOnlyList<StartupFile>? Read(AppsScope scope)
    {
        var folder = scope switch
        {
            AppsScope.CurrentUser => Environment.SpecialFolder.Startup,
            AppsScope.Machine => Environment.SpecialFolder.CommonStartup,
            _ => (Environment.SpecialFolder)(-1)
        };
        if ((int)folder == -1) return null;
        try
        {
            var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var files = new List<StartupFile>();
            foreach (var item in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(item);
                files.Add(new(item, Path.GetFileName(item), Path.GetExtension(item),
                    (attributes & FileAttributes.ReparsePoint) == 0 ? null : false));
            }
            return files;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }
}
