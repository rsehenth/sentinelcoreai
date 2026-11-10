using System.Security;
using Microsoft.Win32;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Modifications;

public static class HkcuContext
{
    public static string? ReadHash()
    {
        if (!OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return null;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User is { } sid
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{Environment.MachineName}\n{sid.Value}")))
                : null;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public sealed record HkcuPolicyEvaluation(
    IReadOnlyList<string> BlockedOperationIds,
    IReadOnlyList<string> BlockedCategories,
    IReadOnlyList<string> Limitations);

public interface IHkcuPolicyProbe
{
    HkcuPolicyEvaluation Evaluate();
}

/// <summary>
/// Sonda somente leitura de políticas documentadas que possam impedir a preferência. Nenhuma política é
/// alterada, habilitada ou contornada. Caminhos e valores são fixos e não vêm da interface.
/// </summary>
public sealed class HkcuPolicyProbe : IHkcuPolicyProbe
{
    private const string DarkModeOperationId = "SC-TW-APP-04";
    private const string NoDispAppearancePage = "NoDispAppearancePage";
    private const string NoFolderOptions = "NoFolderOptions";
    private const string PoliciesSystemPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string UserPoliciesSystemPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PoliciesExplorerPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string UserPoliciesExplorerPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    public HkcuPolicyEvaluation Evaluate()
    {
        var blocked = new List<string>();
        var limitations = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            limitations.Add("Políticas de aparência não avaliadas fora do Windows.");
            return new HkcuPolicyEvaluation(blocked, [], limitations);
        }

        var machineAppearance = ReadDWord(Registry.LocalMachine, PoliciesSystemPath, NoDispAppearancePage,
            "política de máquina");
        var userAppearance = ReadDWord(Registry.CurrentUser, UserPoliciesSystemPath, NoDispAppearancePage,
            "política do usuário");
        AddLimitation(limitations, machineAppearance);
        AddLimitation(limitations, userAppearance);
        if (HkcuPolicyDecision.BlocksAppearance(machineAppearance) ||
            HkcuPolicyDecision.BlocksAppearance(userAppearance))
        {
            blocked.Add(DarkModeOperationId);
            limitations.Add("Política NoDispAppearancePage impede alterações de aparência ou não pôde ser " +
                "interpretada; a operação de modo escuro é bloqueada e a política não é contornada.");
        }

        var machineFolder = ReadDWord(Registry.LocalMachine, PoliciesExplorerPath, NoFolderOptions,
            "política de pasta");
        var userFolder = ReadDWord(Registry.CurrentUser, UserPoliciesExplorerPath, NoFolderOptions,
            "política de pasta do usuário");
        AddLimitation(limitations, machineFolder);
        AddLimitation(limitations, userFolder);
        if (machineFolder.Value == 1 || userFolder.Value == 1)
            limitations.Add("Política NoFolderOptions restringe as opções de pasta na interface; " +
                "o Safety Engine não contorna a política e o efeito visual pode não ser observável.");

        return new HkcuPolicyEvaluation(blocked, [], limitations.Distinct().ToList());
    }

    private static void AddLimitation(List<string> limitations, HkcuPolicyRead read)
    {
        if (read.Limitation is not null) limitations.Add(read.Limitation);
    }

    private static HkcuPolicyRead ReadDWord(RegistryKey root, string path, string name, string description)
    {
        if (!OperatingSystem.IsWindows())
            return HkcuPolicyRead.Indeterminate($"{description} {name} não avaliada fora do Windows.");
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) return HkcuPolicyRead.Absent();
            if (!key.GetValueNames().Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
                return HkcuPolicyRead.Absent();
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is int number
                ? HkcuPolicyRead.Of(number)
                : HkcuPolicyRead.Indeterminate(
                    $"A {description} {name} não é um DWORD; valor não interpretado.");
        }
        catch (SecurityException error)
        {
            return HkcuPolicyRead.Indeterminate(
                $"Acesso negado ao ler a {description} {name}: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            return HkcuPolicyRead.Indeterminate(
                $"Acesso negado ao ler a {description} {name}: {error.Message}");
        }
        catch (Exception error) when (error is IOException or ArgumentException or ObjectDisposedException)
        {
            return HkcuPolicyRead.Indeterminate(
                $"A {description} {name} não pôde ser lida: {error.Message}");
        }
    }
}

/// <summary>
/// Leitura de uma política documentada. <see cref="IsUnreadable"/> distingue uma política comprovadamente
/// ausente de uma política presente que não pôde ser interpretada por tipo incorreto ou acesso negado.
/// </summary>
public sealed record HkcuPolicyRead(int? Value, bool IsUnreadable, string? Limitation)
{
    public static HkcuPolicyRead Absent() => new(null, false, null);
    public static HkcuPolicyRead Of(int value) => new(value, false, null);
    public static HkcuPolicyRead Indeterminate(string limitation) => new(null, true, limitation);
}

/// <summary>
/// Decisão pura e verificável por testes sintéticos: aparência é bloqueada quando a política está habilitada
/// (`1`) ou quando o valor não pôde ser interpretado (fail-closed). O adaptador não altera a política.
/// </summary>
public static class HkcuPolicyDecision
{
    public static bool BlocksAppearance(HkcuPolicyRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read.IsUnreadable || read.Value == 1;
    }
}

/// <summary>
/// Perfil real do Windows. Não escreve nada; acrescenta bloqueios de política e, quando a composição está
/// em modo desabilitado, bloqueia no backend todas as operações reais da T2A.
/// </summary>
public sealed class Windows11SystemProfileReader : ISystemProfileReader
{
    private readonly IHkcuPolicyProbe policy;
    private readonly bool realExecutionEnabled;
    private readonly IReadOnlyList<string> gatedOperationIds;
    private readonly Func<bool> isWindows;
    private readonly Func<int> readBuild;
    private readonly Func<bool> isPrivileged;
    private readonly TimeProvider time;

    public Windows11SystemProfileReader(IHkcuPolicyProbe policy, bool realExecutionEnabled,
        IReadOnlyList<string> gatedOperationIds, Func<bool> isWindows, Func<int> readBuild,
        Func<bool> isPrivileged, TimeProvider? time = null)
    {
        this.policy = policy;
        this.realExecutionEnabled = realExecutionEnabled;
        this.gatedOperationIds = gatedOperationIds;
        this.isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        this.readBuild = readBuild ?? throw new ArgumentNullException(nameof(readBuild));
        this.isPrivileged = isPrivileged ?? throw new ArgumentNullException(nameof(isPrivileged));
        this.time = time ?? TimeProvider.System;
    }

    public SystemProfile Read()
    {
        var windows = isWindows();
        var build = readBuild();
        var windows11 = windows && build >= 22000;
        var privileged = isPrivileged();
        var limitations = new List<string>();
        var blocked = new List<string>();
        IReadOnlyList<string> blockedCategories = [];

        try
        {
            var evaluation = policy.Evaluate();
            blocked.AddRange(evaluation.BlockedOperationIds);
            blockedCategories = evaluation.BlockedCategories;
            limitations.AddRange(evaluation.Limitations);
        }
        catch (Exception error)
        {
            blocked.AddRange(gatedOperationIds);
            limitations.Add("Sonda de políticas indisponível; operações reais bloqueadas por segurança: " +
                error.Message);
        }

        if (!realExecutionEnabled)
        {
            blocked.AddRange(gatedOperationIds);
            limitations.Add("Modo real controlado desabilitado nesta distribuição; " +
                "as operações cadastradas permanecem somente leitura/simulação até a validação real.");
        }

        if (windows && privileged)
            limitations.Add("Aplicativo em execução elevada; elevação não é necessária e a execução real " +
                "deve ser evitada nesse estado.");

        return new SystemProfile(
            windows11 ? $"Windows 11 build {build}" : $"Windows build {build}",
            build,
            windows11,
            privileged,
            blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            blockedCategories,
            [],
            "Windows API (somente leitura)",
            time.GetUtcNow(),
            limitations.Distinct().ToList());
    }
}

/// <summary>
/// Proteção da T2A: o backup por valor é obrigatório, mas nenhum ponto de restauração é criado ou exigido.
/// Quando uma operação futura exigir ponto, a sonda responde indisponível em vez de contornar a política.
/// </summary>
public sealed class HkcuProtectionProvider : IProtectionProvider
{
    public Task<ProtectionStatus> ProbeAsync(ProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (policy.RequiresRestorePoint)
            return Task.FromResult(new ProtectionStatus(false,
                "Ponto de restauração exigido, mas não implementado na T2A.", []));
        return Task.FromResult(new ProtectionStatus(true, null,
            ["Backup do estado observado por valor; ponto de restauração não é criado nesta fase."]));
    }
}
