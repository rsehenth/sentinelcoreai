using System.Security.Principal;
using SentinelCore.Domain;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;

namespace SentinelCore.Infrastructure.Debloat.Native;

/// <summary>Only reads package metadata using the existing Windows token.</summary>
public sealed class NativeAppxInventoryReader(TimeProvider? time = null) : IDebloatNativeInventoryReader
{
    private static readonly PackageTypes Types = PackageTypes.Main | PackageTypes.Framework |
        PackageTypes.Resource | PackageTypes.Bundle | PackageTypes.Optional;

    public bool ReadOnly => true;
    public string ProviderVersion => DebloatNativeSources.ProviderVersion;

    public Task<DebloatInventoryQueryResult> ReadCurrentUserAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(false, cancellationToken), cancellationToken);

    public Task<DebloatInventoryQueryResult> ReadInventoryAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(true, cancellationToken), cancellationToken);

    private DebloatInventoryQueryResult Read(bool includeAdditionalScopes, CancellationToken token)
    {
        var queriedAt = (time ?? TimeProvider.System).GetUtcNow();
        var limitations = new List<string> { DebloatNativeMessages.ReadOnly, DebloatNativeMessages.CatalogLacksIdentity };
        using var identity = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? TryRead(WindowsIdentity.GetCurrent) : null;
        var sid = TryRead(() => identity?.User?.Value) ?? "";
        var manager = sid.Length == 0 ? null : TryRead(() => new PackageManager());
        var current = manager is null
            ? Unavailable(DebloatScope.CurrentUser, "Windows 11, identidade do token ou PackageManager indisponível.")
            : ReadScope(DebloatScope.CurrentUser,
                () => manager.FindPackagesForUserWithPackageTypes(string.Empty, Types), sid, token);

        if (!includeAdditionalScopes) limitations.Add(DebloatNativeMessages.CurrentUserOnly);
        var result = new DebloatInventoryQueryResult(DebloatInventoryOrigin.NativeAppx, current.State,
            current.Completeness, true, sid, queriedAt, ProviderVersion, current.Packages, current.Errors, limitations);
        if (!includeAdditionalScopes) return result;
        if (manager is null)
            return result with
            {
                AdditionalScopes = [Unavailable(DebloatScope.OtherUser, "PackageManager indisponível."),
                    Unavailable(DebloatScope.Provisioned, "PackageManager indisponível.")]
            };
        var canReadOtherScopes = identity is null ? null :
            TryRead<bool?>(() => new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        if (canReadOtherScopes is null)
            return result with
            {
                AdditionalScopes = [Unavailable(DebloatScope.OtherUser, "Privilégios do token indisponíveis."),
                    Unavailable(DebloatScope.Provisioned, "Privilégios do token indisponíveis.")]
            };
        if (canReadOtherScopes == false)
            return result with
            {
                AdditionalScopes = [Denied(DebloatScope.OtherUser), Denied(DebloatScope.Provisioned)]
            };

        var otherUsers = ReadScope(DebloatScope.OtherUser, () => manager.FindPackagesWithPackageTypes(Types), "", token,
            package => HasOtherInstallation(manager, package.Id.FullName, sid));
        var provisioned = ReadScope(DebloatScope.Provisioned, manager.FindProvisionedPackages, "", token);
        return result with { AdditionalScopes = [otherUsers, provisioned] };
    }

    private static bool HasOtherInstallation(PackageManager manager, string packageFullName, string currentSid)
    {
        foreach (var user in manager.FindUsers(packageFullName))
        {
            if (user.InstallState != PackageInstallState.Installed) continue;
            var sid = user.UserSecurityId;
            if (!DebloatNativeIdentity.Safe(sid) || !DebloatNativeIdentity.UserSidPattern().IsMatch(sid))
                throw new InvalidDataException("Escopo de uma instalação indisponível.");
            if (!string.Equals(sid, currentSid, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static DebloatInventoryScopeResult ReadScope(DebloatScope scope, Func<IEnumerable<Package>> enumerate,
        string sid, CancellationToken token, Func<Package, bool>? include = null)
    {
        var packages = new List<DebloatNativePackageMetadata>();
        var errors = new List<DebloatInventoryError>();
        DebloatInventoryScopeResult Result(DebloatInventoryQueryState state, DebloatObservationCompleteness completeness) =>
            new(scope, state, completeness, packages.ToArray(), errors.ToArray());
        var enumerated = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            foreach (var package in enumerate())
            {
                token.ThrowIfCancellationRequested();
                if (++enumerated > DebloatNativeLimits.MaximumPackages)
                {
                    errors.Add(new("ENUMERATION_LIMIT", "Limite de pacotes atingido. Consulta truncada.", true));
                    break;
                }
                try
                {
                    if (include is null || include(package)) packages.Add(Map(package, sid, scope));
                }
                catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
                {
                    errors.Add(new("PACKAGE_READ_FAILED", $"Metadados ou escopo de um pacote indisponíveis (0x{error.HResult:X8}).", true));
                }
            }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            errors.Add(new("CANCELLED", "Consulta interrompida.", true));
            return Result(DebloatInventoryQueryState.Interrupted, packages.Count == 0
                ? DebloatObservationCompleteness.NotObserved : DebloatObservationCompleteness.Partial);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (error.HResult == unchecked((int)0x80070005))
            {
                errors.Add(new("ACCESS_DENIED", "Windows negou acesso a este escopo.", true));
                return Result(DebloatInventoryQueryState.AccessDenied, packages.Count == 0
                    ? DebloatObservationCompleteness.Unavailable : DebloatObservationCompleteness.Partial);
            }
            errors.Add(new("ENUMERATION_FAILED", $"Consulta indisponível ou interrompida pela API (0x{error.HResult:X8}).", true));
            return packages.Count == 0
                ? Result(DebloatInventoryQueryState.Failed, DebloatObservationCompleteness.Unavailable)
                : Result(DebloatInventoryQueryState.Partial, DebloatObservationCompleteness.Partial);
        }
        if (errors.Count > 0) return Result(DebloatInventoryQueryState.Partial, DebloatObservationCompleteness.Partial);
        return Result(packages.Count == 0 ? DebloatInventoryQueryState.CompletedNoResults : DebloatInventoryQueryState.Completed,
            DebloatObservationCompleteness.Complete);
    }

    private static DebloatNativePackageMetadata Map(Package package, string sid, DebloatScope scope)
    {
        var id = package.Id;
        if (!DebloatNativeIdentity.Safe(id.Name) || !DebloatNativeIdentity.TechnicalNamePattern().IsMatch(id.Name))
            throw new InvalidDataException("Nome técnico indisponível.");
        var version = id.Version;
        var framework = package.IsFramework;
        var resource = package.IsResourcePackage;
        var optional = package.IsOptional;
        var bundle = package.IsBundle;
        var signature = TryRead<PackageSignatureKind?>(() => package.SignatureKind) switch
        {
            PackageSignatureKind.None => DebloatNativeSignature.Unsigned,
            PackageSignatureKind.Developer => DebloatNativeSignature.Developer,
            PackageSignatureKind.Enterprise => DebloatNativeSignature.Enterprise,
            PackageSignatureKind.Store => DebloatNativeSignature.Store,
            PackageSignatureKind.System => DebloatNativeSignature.System,
            _ => (DebloatNativeSignature?)null
        };
        return new(id.Name, id.FamilyName, id.FullName, id.Publisher, id.PublisherId,
            $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
            id.Architecture switch
            {
                ProcessorArchitecture.X64 => "x64", ProcessorArchitecture.X86 => "x86",
                ProcessorArchitecture.Arm => "arm", ProcessorArchitecture.Arm64 => "arm64",
                ProcessorArchitecture.Neutral => "neutral", ProcessorArchitecture.X86OnArm64 => "x86onarm64",
                _ => "unknown"
            },
            sid, framework ? DebloatPackageKind.Framework : resource ? DebloatPackageKind.Resource : DebloatPackageKind.Appx,
            scope, scope == DebloatScope.Provisioned ? DebloatNativeInstallState.Unknown :
                TryRead(() => InstallState(package.Status)), scope == DebloatScope.Provisioned ? true : null,
            framework, resource, !framework && !resource && !optional && !bundle, optional, bundle,
            scope switch
            {
                DebloatScope.Provisioned => "PackageManager.FindProvisionedPackages",
                DebloatScope.OtherUser => "PackageManager.FindPackagesWithPackageTypes + FindUsers",
                _ => DebloatNativeSources.PackageManager
            }, [])
        {
            DisplayName = TryRead(() => package.DisplayName),
            Signature = signature,
            DependencyFullNames = scope == DebloatScope.CurrentUser
                ? TryRead(() => AppxDependencyReader.Read(id.FullName)) : null
        };
    }

    private static DebloatNativeInstallState InstallState(Windows.ApplicationModel.PackageStatus status) => status.VerifyIsOK()
        ? DebloatNativeInstallState.Available : status.NotAvailable
            ? DebloatNativeInstallState.NotAvailable : DebloatNativeInstallState.Degraded;

    private static T? TryRead<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException)) { return default; }
    }

    private static DebloatInventoryScopeResult Unavailable(DebloatScope scope, string reason) =>
        new(scope, DebloatInventoryQueryState.Unavailable, DebloatObservationCompleteness.Unavailable,
            [], [new("UNAVAILABLE", reason, true)]);

    private static DebloatInventoryScopeResult Denied(DebloatScope scope) =>
        new(scope, DebloatInventoryQueryState.AccessDenied, DebloatObservationCompleteness.Unavailable,
            [], [new("ACCESS_DENIED", "A consulta exige privilégios que o token atual não oferece.", true)]);
}
