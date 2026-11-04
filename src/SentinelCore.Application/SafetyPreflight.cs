using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Application;

internal static class SafetyGates
{
    public static BlockerCode? RiskGate(OperationDescriptor operation) => operation.Risk switch
    {
        RiskLevel.High => BlockerCode.RiskBlocked,
        RiskLevel.NotValidated => BlockerCode.NotValidated,
        _ => null
    };
}

internal sealed class PreflightEngine
{
    private readonly IOperationCatalog catalog;
    private readonly ISystemProfileReader profileReader;
    private readonly ITargetStateReader reader;
    private readonly IProtectionProvider protection;

    public PreflightEngine(IOperationCatalog catalog, ISystemProfileReader profileReader,
        ITargetStateReader reader, IProtectionProvider protection)
    {
        this.catalog = catalog;
        this.profileReader = profileReader;
        this.reader = reader;
        this.protection = protection;
    }

    public async Task<PreflightResult> EvaluateAsync(OperationDescriptor operation,
        CancellationToken cancellationToken = default)
    {
        var profile = profileReader.Read();
        var blockers = new List<BlockerCode>();
        var details = new List<string>();
        var limitations = new List<string>(profile.Limitations);

        if (!profile.IsWindows11)
        {
            blockers.Add(BlockerCode.UnsupportedWindows);
            details.Add($"Windows 11 é obrigatório; perfil reportado: {profile.ProductName}.");
        }

        if (int.TryParse(operation.Compatibility.MinimumBuild, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var minimum) && profile.Build < minimum)
        {
            blockers.Add(BlockerCode.UnsupportedWindows);
            details.Add($"Build {profile.Build} abaixo do mínimo {minimum}.");
        }
        if (operation.Compatibility.MaximumBuild is not null &&
            int.TryParse(operation.Compatibility.MaximumBuild, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var maximum) && profile.Build > maximum)
        {
            blockers.Add(BlockerCode.UnsupportedWindows);
            details.Add($"Build {profile.Build} acima do máximo {maximum}.");
        }

        if (!catalog.TryGet(operation.Id, out var catalogued) || catalogued is null ||
            catalogued.Version != operation.Version)
        {
            blockers.Add(BlockerCode.PlanMismatch);
            details.Add("Versão da operação não corresponde ao catálogo fechado.");
        }

        if (operation.Scope != OperationScope.CurrentUser)
        {
            blockers.Add(BlockerCode.ElevationRequired);
            details.Add("A T1 opera somente no escopo do usuário atual, sem elevação.");
        }

        foreach (var target in operation.Targets)
        {
            if (!OperationTargetRoots.IsAllowed(target.Root))
            {
                blockers.Add(BlockerCode.TargetOutsideCatalog);
                details.Add($"Alvo fora das raízes permitidas: {target.Identity}.");
            }
            else if (string.Equals(target.Root, OperationTargetRoots.CurrentUser, StringComparison.Ordinal) &&
                     (!OperationTargetRoots.IsWellFormedCurrentUserTarget(target) ||
                      !HkcuCatalogGuard.IsAllowed(target)))
            {
                blockers.Add(BlockerCode.TargetOutsideCatalog);
                details.Add($"Alvo HKCU malformado ou fora do catálogo fechado de tweaks: {target.Identity}.");
            }
        }

        if (operation.Availability != OperationAvailability.Enabled)
        {
            blockers.Add(BlockerCode.OperationBlocked);
            details.Add("Operação desabilitada ou bloqueada no catálogo.");
        }

        if (SafetyGates.RiskGate(operation) is { } riskBlocker)
        {
            blockers.Add(riskBlocker);
            details.Add(operation.Risk == RiskLevel.High
                ? "Risco alto recusado no backend."
                : "Operação sem classificação validada recusada no backend.");
        }

        if (profile.BlockedOperationIds.Contains(operation.Id, StringComparer.OrdinalIgnoreCase) ||
            profile.BlockedCategories.Contains(operation.Category, StringComparer.OrdinalIgnoreCase))
        {
            blockers.Add(BlockerCode.PolicyConflict);
            details.Add("Política restritiva bloqueia a operação.");
        }

        foreach (var dependency in operation.Dependencies)
        {
            if (!profile.AppliedOperations.Contains(dependency, StringComparer.Ordinal))
            {
                blockers.Add(BlockerCode.DependencyMissing);
                details.Add($"Dependência ausente: {dependency}.");
            }
        }

        var observed = new List<ObservedTargetState>();
        foreach (var target in operation.Targets)
        {
            try
            {
                var state = await reader.ReadAsync(target, cancellationToken);
                observed.Add(state);
                if (!state.IsKnown)
                {
                    blockers.Add(BlockerCode.AccessDenied);
                    details.Add($"Estado do alvo não observável: {state.TargetIdentity}.");
                }
                else if (string.Equals(target.Root, OperationTargetRoots.CurrentUser, StringComparison.Ordinal) &&
                         !state.TargetExists)
                {
                    blockers.Add(BlockerCode.Unrecoverable);
                    details.Add($"{state.TargetIdentity}: chave HKCU ausente; o adaptador não cria chaves.");
                }
                if (!string.IsNullOrWhiteSpace(state.Limitation))
                    limitations.Add($"{state.TargetIdentity}: {state.Limitation}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                blockers.Add(BlockerCode.AccessDenied);
                details.Add($"Falha de leitura em {target.Identity}: {error.Message}");
                limitations.Add($"Leitura de {target.Identity} indisponível: {error.Message}");
                observed.Add(ObservedTargetState.Unknown(target.Identity,
                    DateTimeOffset.UtcNow, "Leitura indisponível."));
            }
        }

        foreach (var state in observed.Where(item => item.IsKnown))
        {
            if (state.Value is { Exists: true, Kind: "None" })
            {
                blockers.Add(BlockerCode.Unrecoverable);
                details.Add($"{state.TargetIdentity}: REG_NONE não é serializável com segurança.");
            }
            if (state.Value is { Exists: true, Data: null })
            {
                blockers.Add(BlockerCode.Unrecoverable);
                details.Add($"{state.TargetIdentity}: valor definido sem conteúdo legível.");
            }
        }

        var observedByIdentity = observed.ToDictionary(item => item.TargetIdentity, StringComparer.Ordinal);
        foreach (var planned in operation.Changes)
        {
            if (!observedByIdentity.TryGetValue(planned.Target.Identity, out var observedTarget) ||
                !observedTarget.IsKnown || !observedTarget.TargetExists ||
                observedTarget.Value is not { Exists: true } observedValue ||
                string.Equals(observedValue.Kind, planned.DesiredValue.Kind, StringComparison.Ordinal))
                continue;
            blockers.Add(BlockerCode.Unrecoverable);
            details.Add($"{planned.Target.Identity}: tipo observado {observedValue.Kind} diverge do " +
                $"tipo esperado {planned.DesiredValue.Kind}; conversão automática recusada.");
        }

        if (!operation.Protection.RequiresBackup)
        {
            blockers.Add(BlockerCode.ProtectionUnavailable);
            details.Add("A operação não declarou backup obrigatório.");
        }
        if (operation.Protection.RequiresFullTarget)
        {
            blockers.Add(BlockerCode.ProtectionUnavailable);
            details.Add("Backup de chave inteira ainda não é representável na T1.");
        }

        ProtectionStatus probe;
        try
        {
            probe = await protection.ProbeAsync(operation.Protection, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            probe = new ProtectionStatus(false, error.Message,
                ["Sonda de proteção indisponível: " + error.Message]);
        }
        limitations.AddRange(probe.Limitations);
        if (operation.Protection.RequiresRestorePoint && !probe.RestorePointAvailable)
        {
            blockers.Add(BlockerCode.ProtectionUnavailable);
            details.Add(string.IsNullOrWhiteSpace(probe.Reason)
                ? "Ponto de restauração exigido e indisponível."
                : probe.Reason!);
        }

        var hash = SafetyHashing.ObservedStateHash(observed);
        return new PreflightResult(blockers.Count == 0, blockers.Distinct().ToList(), details,
            observed, hash, catalog.Version, profile.Source, limitations.Distinct().ToList());
    }
}
