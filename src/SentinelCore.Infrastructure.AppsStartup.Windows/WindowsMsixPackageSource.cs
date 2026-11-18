using SentinelCore.Infrastructure.AppsStartup;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SentinelCore.Infrastructure.AppsStartup.Windows;

/// <summary>Não composto. Distingue candidatos comuns, mas ausência de prova de política impede remoção.</summary>
public sealed class WindowsMsixPackageSource : IMsixPackageSource
{
    public IReadOnlyList<MsixPackageSnapshot> ReadCurrentUser()
    {
        var packages = new PackageManager().FindPackagesForUser(string.Empty).ToArray();
        var dependencies = packages.SelectMany(p => p.Dependencies).Select(p => p.Id.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return packages.Select(p => new MsixPackageSnapshot(p.Id.FullName, p.Id.FamilyName, p.Id.Name,
            p.Id.Publisher, $"{p.Id.Version.Major}.{p.Id.Version.Minor}.{p.Id.Version.Build}.{p.Id.Version.Revision}",
            p.IsFramework ? "Framework" : p.IsResourcePackage ? "Resource" : p.IsBundle ? "Bundle" : "Package",
            p.Status.VerifyIsOK(), p.IsFramework, dependencies.Contains(p.Id.FullName),
            p.SignatureKind == PackageSignatureKind.System || p.IsResourcePackage || p.IsBundle || p.IsOptional || p.IsStub)
            {
                // shortcut: a projeção Package não prova a política NonRemovable; exigir evidência suportada antes de liberar.
                ProtectionKnown = false
            }).ToArray();
    }
}
