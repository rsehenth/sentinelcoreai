using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Preflight do Repair Center. Separa o que pode ser demonstrado em simulação do que exigiria uma execução
/// real privilegiada. Nenhuma elevação é solicitada e nenhum processo é iniciado.
/// </summary>
public sealed class RepairPreflightEngine
{
    private readonly IRepairCatalog catalog;
    private readonly IRepairSystemProfileReader profileReader;
    private readonly IRepairVolumeProvider volumes;
    private readonly IRepairToolLocator tools;
    private readonly TimeProvider time;

    public RepairPreflightEngine(IRepairCatalog catalog, IRepairSystemProfileReader profileReader,
        IRepairVolumeProvider volumes, IRepairToolLocator tools, TimeProvider? time = null)
    {
        this.catalog = catalog;
        this.profileReader = profileReader;
        this.volumes = volumes;
        this.tools = tools;
        this.time = time ?? TimeProvider.System;
    }

    public Task<RepairPreflightResult> EvaluateAsync(RepairOperationDescriptor operation,
        string? volumeId, CancellationToken cancellationToken = default) =>
        EvaluateAsync(operation, volumeId, brokered: false, cancellationToken);

    internal Task<RepairPreflightResult> EvaluateBrokeredAsync(RepairOperationDescriptor operation,
        string? volumeId, CancellationToken cancellationToken) =>
        EvaluateAsync(operation, volumeId, brokered: RepairCenter.BrokerExecutionEnabled, cancellationToken);

    private async Task<RepairPreflightResult> EvaluateAsync(RepairOperationDescriptor operation,
        string? volumeId, bool brokered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var simulationBlockers = new List<RepairBlocker>();
        var realBlockers = new List<RepairBlocker>();
        var details = new List<string>();
        var limitations = new List<string>();

        var profile = ReadProfile(limitations);
        if (!profile.IsWindows11)
        {
            Add(simulationBlockers, realBlockers, RepairBlocker.UnsupportedWindows);
            details.Add("Windows 11 é obrigatório para o Repair Center.");
        }

        if (!catalog.TryGet(operation.Id, out var catalogued) || catalogued is null ||
            catalogued.Version != operation.Version)
        {
            Add(simulationBlockers, realBlockers, RepairBlocker.CatalogMismatch);
            details.Add("Operação ou versão não corresponde ao catálogo fechado.");
        }

        if (operation.IsDocumentedOnly || operation.Availability != RepairAvailability.Available)
        {
            Add(simulationBlockers, realBlockers, RepairBlocker.OperationUnavailable);
            details.Add("Operação documentada, desabilitada ou indisponível nesta fase.");
        }

        if (operation.Risk is RiskLevel.High or RiskLevel.NotValidated)
        {
            Add(simulationBlockers, realBlockers, RepairBlocker.RiskBlocked);
            details.Add("Risco alto ou não validado é recusado no backend.");
        }

        var tool = ResolveTool(operation.Tool, simulationBlockers, realBlockers, details, limitations);

        RepairVolume? volume = null;
        if (operation.RequiresVolume)
        {
            if (!RepairVolumeIdPolicy.TryNormalize(volumeId, out var normalized))
            {
                Add(simulationBlockers, realBlockers, RepairBlocker.VolumeRequired);
                details.Add("Selecione um volume local validado; texto livre e caminhos não são aceitos.");
            }
            else
            {
                volume = await FindVolumeAsync(normalized, simulationBlockers, realBlockers, details,
                    limitations, cancellationToken);
            }
        }
        else if (!string.IsNullOrWhiteSpace(volumeId))
        {
            Add(simulationBlockers, realBlockers, RepairBlocker.VolumeUnavailable);
            details.Add("Esta operação não aceita volume; o argumento extra é recusado.");
        }

        if (volume is not null)
        {
            if (!volume.IsLocalFixed)
            {
                Add(simulationBlockers, realBlockers, RepairBlocker.VolumeNotLocal);
                details.Add("Somente volumes locais fixos são aceitos.");
            }
            if (!volume.IsReady)
            {
                Add(simulationBlockers, realBlockers, RepairBlocker.VolumeUnavailable);
                details.Add("O volume selecionado não está pronto.");
            }
            var requiresNtfs = operation.FixedArguments.Any(argument =>
                string.Equals(argument, "/scan", StringComparison.OrdinalIgnoreCase));
            if (requiresNtfs && !volume.IsNtfs)
            {
                Add(simulationBlockers, realBlockers, RepairBlocker.IncompatibleFileSystem);
                details.Add("A verificação online /scan exige sistema de arquivos NTFS.");
            }
            limitations.AddRange(volume.Limitations);
        }

        if (brokered)
        {
            if (!profile.Is64Bit) realBlockers.Add(RepairBlocker.UnsupportedWindows);
            if (operation.RequiresVolume && volume?.VolumeIdentity is null)
                realBlockers.Add(RepairBlocker.VolumeUnavailable);
            details.Add(operation.RequiresElevation && !profile.IsElevated
                ? "O broker solicitará consentimento UAC após a confirmação e a gravação da intenção."
                : "O broker revalidará os privilégios antes do início.");
        }
        else
        {
            realBlockers.Add(RepairBlocker.RealExecutionDisabled);
            details.Add(RepairGuidance.RealExecutionUnavailable);
            if (operation.RequiresElevation)
            {
                realBlockers.Add(RepairBlocker.ElevationBrokerUnavailable);
                details.Add("A execução real exigiria elevação temporária por uma fronteira privilegiada futura.");
            }
        }

        limitations.AddRange(profile.Limitations.Where(item => !brokered || item != RepairGuidance.ElevationLimitation));
        limitations.AddRange(tool.Limitations);
        if (!brokered) limitations.Add(RepairGuidance.ElevationLimitation);
        limitations.Add(RepairGuidance.NoRollback);
        limitations.Add(RepairGuidance.ExitCodeLimitation);
        limitations.Add(RepairGuidance.CancellationLimitation);
        if (operation.MayChangeSystem)
        {
            limitations.Add("A operação pode produzir efeitos parciais; não há rollback convencional.");
        }

        var observedHash = RepairHashing.PreflightHash(profile, tool, volume, catalog.Version,
            catalog.ContentHash, operation.Id, operation.Version);
        var simulationAllowed = simulationBlockers.Count == 0;
        var realAllowed = brokered && simulationAllowed && realBlockers.Count == 0;
        return new RepairPreflightResult(simulationAllowed, realAllowed,
            realBlockers.Distinct().ToList(), simulationBlockers.Distinct().ToList(),
            details.Distinct().ToList(), profile, volume, tool, observedHash, time.GetUtcNow(),
            limitations.Distinct().ToList());
    }

    private RepairSystemProfile ReadProfile(List<string> limitations)
    {
        try
        {
            return profileReader.Read();
        }
        catch (Exception error)
        {
            limitations.Add("Perfil do sistema indisponível: " + error.Message);
            return new RepairSystemProfile("Indisponível", 0, false, false, false, "unavailable",
                time.GetUtcNow(), ["Perfil do sistema não pôde ser lido."]);
        }
    }

    private RepairToolLocation ResolveTool(RepairTool tool, List<RepairBlocker> simulationBlockers,
        List<RepairBlocker> realBlockers, List<string> details, List<string> limitations)
    {
        try
        {
            var location = tools.Resolve(tool);
            if (!location.Available)
            {
                Add(simulationBlockers, realBlockers,
                    location.Error is not null && location.Error.Contains("caminho", StringComparison.OrdinalIgnoreCase)
                        ? RepairBlocker.ToolPathRejected
                        : RepairBlocker.ToolNotFound);
                details.Add(location.Error ?? $"{RepairToolNames.Executable(tool)} não encontrado.");
            }
            limitations.AddRange(location.Limitations);
            return location;
        }
        catch (Exception error)
        {
            Add(simulationBlockers, realBlockers, RepairBlocker.ToolPathRejected);
            details.Add("Localizador do executável oficial recusou a resolução: " + error.Message);
            limitations.Add("Executável oficial não pôde ser validado: " + error.Message);
            return RepairToolLocation.Missing(tool, error.Message);
        }
    }

    private async Task<RepairVolume?> FindVolumeAsync(string volumeId, List<RepairBlocker> simulationBlockers,
        List<RepairBlocker> realBlockers, List<string> details, List<string> limitations,
        CancellationToken cancellationToken)
    {
        try
        {
            var available = await volumes.GetVolumesAsync(cancellationToken);
            var volume = available.FirstOrDefault(item =>
                string.Equals(item.Id, volumeId, StringComparison.OrdinalIgnoreCase));
            if (volume is null)
            {
                Add(simulationBlockers, realBlockers, RepairBlocker.VolumeUnavailable);
                details.Add($"Volume {volumeId} não está na lista validada de volumes locais.");
            }
            return volume;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Add(simulationBlockers, realBlockers, RepairBlocker.VolumeUnavailable);
            details.Add("Lista de volumes indisponível: " + error.Message);
            limitations.Add("Não foi possível validar volumes locais: " + error.Message);
            return null;
        }
    }

    private static void Add(List<RepairBlocker> simulationBlockers, List<RepairBlocker> realBlockers,
        RepairBlocker blocker)
    {
        if (!simulationBlockers.Contains(blocker)) simulationBlockers.Add(blocker);
        if (!realBlockers.Contains(blocker)) realBlockers.Add(blocker);
    }
}

/// <summary>
/// Constrói a invocação a partir do catálogo e de um volume previamente validado. Não existe sobrecarga que
/// aceite comando, executável, caminho ou lista de argumentos fornecida pelo chamador.
/// </summary>
public static class RepairInvocationBuilder
{
    public static RepairInvocation Build(RepairOperationDescriptor operation, RepairVolume? volume,
        RepairToolLocation tool)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.IsDocumentedOnly)
            throw new InvalidOperationException("Operação documental não pode ser preparada para execução.");
        if (!operation.RequiresVolume && volume is not null)
            throw new InvalidOperationException("Operação não aceita volume.");
        if (operation.RequiresVolume && volume is null)
            throw new InvalidOperationException("Operação exige um volume validado.");

        var arguments = operation.FixedArguments.Select(argument =>
            argument == RepairOperationDescriptor.VolumePlaceholder
                ? volume!.Id
                : argument).ToList();
        if (operation.RequiresVolume && !RepairVolumeIdPolicy.IsWellFormed(arguments[0]))
            throw new InvalidOperationException("Volume inválido na composição do argumento.");
        if (arguments.Any(argument => !RepairArgumentPolicy.IsAllowed(operation.Tool, argument)))
            throw new InvalidOperationException("Argumento fora da allowlist oficial.");

        return new RepairInvocation(operation.Tool, operation.ExecutableName, tool.Path ?? string.Empty,
            arguments);
    }

    public static bool Matches(RepairOperationDescriptor operation, RepairInvocation invocation,
        RepairVolume? volume, RepairToolLocation tool, out string reason)
    {
        if (invocation.Tool != operation.Tool || !string.Equals(invocation.ExecutableName,
                operation.ExecutableName, StringComparison.Ordinal))
        {
            reason = "Executável ou ferramenta diverge do catálogo.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(tool.Path) &&
            !string.Equals(invocation.ExecutablePath, tool.Path, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Caminho do executável diverge do caminho oficial validado.";
            return false;
        }
        if (invocation.Arguments.Count != operation.FixedArguments.Count)
        {
            reason = "Quantidade de argumentos diverge do catálogo.";
            return false;
        }
        for (var index = 0; index < operation.FixedArguments.Count; index++)
        {
            var expected = operation.FixedArguments[index] == RepairOperationDescriptor.VolumePlaceholder
                ? volume?.Id
                : operation.FixedArguments[index];
            if (!string.Equals(invocation.Arguments[index], expected, StringComparison.Ordinal))
            {
                reason = "Argumento diverge do catálogo fechado.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
}
