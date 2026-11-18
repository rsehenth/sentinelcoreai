using SentinelCore.Domain;
using Windows.Management.Deployment;

namespace SentinelCore.Infrastructure.AppsStartup.Windows;

/// <summary>Remoção individual do usuário atual. Não composto; nenhum cancelamento após iniciar.</summary>
public sealed class WindowsMsixUninstallApi : IMsixUninstallApi
{
    public bool IsSynthetic => false;
    private static void Guard()
    {
        if (!AppsOperationalExecutionGate.PermitsExecution) throw new InvalidOperationException("gate");
    }

    public MsixRemovalIdentity? Inspect(string packageFullName)
    {
        Guard();
        if (string.IsNullOrWhiteSpace(packageFullName) || packageFullName.Length > 512 || packageFullName.Contains('\0'))
            throw new ArgumentException("invalid-package-identity");
        var packages = new WindowsMsixPackageSource().ReadCurrentUser();
        var matches = packages.Where(p => p.FullName == packageFullName).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException("ambiguous-package");
        var p = matches[0];
        return new(p.FullName!, p.FamilyName ?? "", p.Name ?? "", p.Version ?? "", true, p.Available == true,
            p.Framework, p.Dependency, p.Protected || !p.ProtectionKnown,
            !string.IsNullOrWhiteSpace(p.FamilyName) && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Version));
    }

    public async Task<MsixRemovalReceipt> RemoveAsync(string packageFullName)
    {
        Guard();
        if (Inspect(packageFullName) is not { CurrentUser: true, Available: true, IdentityKnown: true,
                Framework: false, Dependency: false, Protected: false })
            throw new InvalidOperationException("package-eligibility-not-proven");
        var result = await new PackageManager().RemovePackageAsync(packageFullName);
        // ErrorText may include paths or arguments; only the numeric HRESULT leaves this boundary.
        var code = result.ExtendedErrorCode?.HResult ?? 0;
        return new(code == 0, code);
    }
}
