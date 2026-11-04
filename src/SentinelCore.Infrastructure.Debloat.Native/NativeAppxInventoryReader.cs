using System.Runtime.InteropServices;
using System.Security.Principal;
using SentinelCore.Domain;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;

namespace SentinelCore.Infrastructure.Debloat.Native;

/// <summary>
/// Strictly read-only enumeration of Appx/MSIX packages installed for the current user.
/// It never removes, registers, deploys, provisions or elevates, and it never enumerates another user.
/// This assembly compiles for Windows 11 but is not composed by the distributed application during T3B.
/// </summary>
public sealed class NativeAppxInventoryReader : IDebloatNativeInventoryReader
{
    private readonly TimeProvider time;

    public NativeAppxInventoryReader(TimeProvider? time = null) =>
        this.time = time ?? TimeProvider.System;

    public bool ReadOnly => true;
    public string ProviderVersion => DebloatNativeSources.ProviderVersion;

    public Task<DebloatInventoryQueryResult> ReadCurrentUserAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private DebloatInventoryQueryResult Read(CancellationToken cancellationToken)
    {
        var queriedAt = time.GetUtcNow();
        var packages = new List<DebloatNativePackageMetadata>();
        var errors = new List<DebloatInventoryError>();
        var limitations = new List<string>
        {
            DebloatNativeMessages.ReadOnly,
            DebloatNativeMessages.CurrentUserOnly,
            DebloatNativeMessages.CatalogLacksIdentity
        };

        var sid = CurrentUserSecurityId(limitations);
        IEnumerable<Package> enumeration;
        try
        {
            // Empty SID means the current user. No other user is requested and no administrative token is used.
            enumeration = new PackageManager().FindPackagesForUser(string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return Result(DebloatInventoryQueryState.AccessDenied,
                DebloatObservationCompleteness.Unavailable, packages, errors, limitations, sid, queriedAt);
        }
        catch (COMException error) when (error.HResult == AccessDeniedHResult)
        {
            return Result(DebloatInventoryQueryState.AccessDenied,
                DebloatObservationCompleteness.Unavailable, packages, errors, limitations, sid, queriedAt);
        }
        catch (Exception error) when (error is TypeLoadException or FileNotFoundException or
                                      PlatformNotSupportedException or NotSupportedException)
        {
            limitations.Add("Projeção WinRT de PackageManager indisponível neste host.");
            return Result(DebloatInventoryQueryState.Unavailable,
                DebloatObservationCompleteness.Unavailable, packages, errors, limitations, sid, queriedAt);
        }
        catch (Exception error)
        {
            errors.Add(new DebloatInventoryError("ENUMERATION_FAILED", error.Message, true));
            return Result(DebloatInventoryQueryState.Failed,
                DebloatObservationCompleteness.Unavailable, packages, errors, limitations, sid, queriedAt);
        }

        try
        {
            foreach (var package in enumeration)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    errors.Add(new DebloatInventoryError("CANCELLED", "Consulta interrompida por cancelamento.", true));
                    return Result(DebloatInventoryQueryState.Interrupted,
                        packages.Count == 0 ? DebloatObservationCompleteness.NotObserved : DebloatObservationCompleteness.Partial,
                        packages, errors, limitations, sid, queriedAt);
                }
                if (packages.Count >= DebloatNativeLimits.MaximumPackages)
                {
                    errors.Add(new DebloatInventoryError("ENUMERATION_LIMIT",
                        "Limite de pacotes atingido; enumeração truncada e ausência não confirmada.", true));
                    return Result(DebloatInventoryQueryState.Partial,
                        DebloatObservationCompleteness.Partial, packages, errors, limitations, sid, queriedAt);
                }
                try
                {
                    packages.Add(Map(package, sid));
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                              NullReferenceException or FormatException)
                {
                    errors.Add(new DebloatInventoryError("PACKAGE_MAP_FAILED",
                        "Pacote não pôde ser mapeado com segurança; observação parcial.", true));
                }
            }
        }
        catch (OperationCanceledException)
        {
            errors.Add(new DebloatInventoryError("CANCELLED", "Consulta interrompida por cancelamento.", true));
            return Result(DebloatInventoryQueryState.Interrupted,
                packages.Count == 0 ? DebloatObservationCompleteness.NotObserved : DebloatObservationCompleteness.Partial,
                packages, errors, limitations, sid, queriedAt);
        }
        catch (UnauthorizedAccessException)
        {
            return Result(DebloatInventoryQueryState.AccessDenied,
                packages.Count == 0 ? DebloatObservationCompleteness.Unavailable : DebloatObservationCompleteness.Partial,
                packages, errors, limitations, sid, queriedAt);
        }
        catch (COMException error) when (error.HResult == AccessDeniedHResult)
        {
            return Result(DebloatInventoryQueryState.AccessDenied,
                packages.Count == 0 ? DebloatObservationCompleteness.Unavailable : DebloatObservationCompleteness.Partial,
                packages, errors, limitations, sid, queriedAt);
        }
        catch (Exception error)
        {
            errors.Add(new DebloatInventoryError("ENUMERATION_FAILED", error.Message, true));
            return Result(packages.Count == 0 ? DebloatInventoryQueryState.Failed : DebloatInventoryQueryState.Partial,
                packages.Count == 0 ? DebloatObservationCompleteness.Unavailable : DebloatObservationCompleteness.Partial,
                packages, errors, limitations, sid, queriedAt);
        }

        if (errors.Any(error => error.Fatal))
            return Result(DebloatInventoryQueryState.Partial, DebloatObservationCompleteness.Partial,
                packages, errors, limitations, sid, queriedAt);
        return packages.Count == 0
            ? Result(DebloatInventoryQueryState.CompletedNoResults, DebloatObservationCompleteness.Complete,
                packages, errors, limitations, sid, queriedAt)
            : Result(DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete,
                packages, errors, limitations, sid, queriedAt);
    }

    private static DebloatInventoryQueryResult Result(DebloatInventoryQueryState state,
        DebloatObservationCompleteness completeness, List<DebloatNativePackageMetadata> packages,
        List<DebloatInventoryError> errors, List<string> limitations, string sid, DateTimeOffset queriedAt) =>
        new(DebloatInventoryOrigin.NativeAppx, state, completeness, true, sid, queriedAt,
            DebloatNativeSources.ProviderVersion, packages.ToArray(), errors.ToArray(),
            limitations.Distinct(StringComparer.Ordinal).ToArray());

    private static string CurrentUserSecurityId(List<string> limitations)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            if (sid.Length == 0)
                limitations.Add("SID do usuário atual indisponível; identidade dos pacotes não confirmada.");
            return sid;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A read-only identity lookup must degrade to an explicit limitation, never to a guessed SID.
            limitations.Add("SID do usuário atual indisponível; identidade dos pacotes não confirmada.");
            return string.Empty;
        }
    }

    private static DebloatNativePackageMetadata Map(Package package, string sid)
    {
        var id = package.Id;
        var version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}";
        var framework = package.IsFramework;
        var resource = package.IsResourcePackage;
        var optional = package.IsOptional;
        return new(
            id.Name,
            id.FamilyName,
            id.FullName,
            id.Publisher,
            id.PublisherId,
            version,
            Architecture(id.Architecture),
            sid,
            framework ? DebloatPackageKind.Framework : resource ? DebloatPackageKind.Resource : DebloatPackageKind.Appx,
            DebloatScope.CurrentUser,
            InstallState(package.Status),
            null,
            framework,
            resource,
            !framework && !resource && !optional,
            optional,
            package.IsBundle,
            DebloatNativeSources.PackageManager,
            ["Metadados somente leitura; conteúdo, dados do usuário e perfis de terceiros não são consultados."]);
    }

    private static string Architecture(ProcessorArchitecture value) => value switch
    {
        ProcessorArchitecture.X64 => "x64",
        ProcessorArchitecture.X86 => "x86",
        ProcessorArchitecture.Arm => "arm",
        ProcessorArchitecture.Arm64 => "arm64",
        ProcessorArchitecture.Neutral => "neutral",
        ProcessorArchitecture.X86OnArm64 => "x86onarm64",
        _ => "unknown"
    };

    private static DebloatNativeInstallState InstallState(Windows.ApplicationModel.PackageStatus value)
    {
        if (value.VerifyIsOK()) return DebloatNativeInstallState.Available;
        if (value.NotAvailable) return DebloatNativeInstallState.NotAvailable;
        return DebloatNativeInstallState.Degraded;
    }

    private const int AccessDeniedHResult = unchecked((int)0x80070005);
}
