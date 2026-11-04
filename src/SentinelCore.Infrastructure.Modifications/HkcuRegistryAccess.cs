using System.Globalization;
using System.Security;
using Microsoft.Win32;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Modifications;

public enum HkcuReadStatus
{
    Success,
    MissingKey,
    MissingValue,
    AccessDenied,
    InvalidTarget,
    Unsupported,
    Error
}

public sealed record HkcuValueRead(HkcuReadStatus Status, string Kind, string? Data, string? Error)
{
    public static HkcuValueRead Ok(string kind, string? data) =>
        new(HkcuReadStatus.Success, kind, data, null);
    public static HkcuValueRead MissingKey() =>
        new(HkcuReadStatus.MissingKey, RegistryValueKinds.Unknown, null, null);
    public static HkcuValueRead MissingValue() =>
        new(HkcuReadStatus.MissingValue, RegistryValueKinds.DWord, null, null);
    public static HkcuValueRead Denied(string error) =>
        new(HkcuReadStatus.AccessDenied, RegistryValueKinds.Unknown, null, error);
    public static HkcuValueRead Invalid(string error) =>
        new(HkcuReadStatus.InvalidTarget, RegistryValueKinds.Unknown, null, error);
    public static HkcuValueRead NotSupported(string? error = null) =>
        new(HkcuReadStatus.Unsupported, RegistryValueKinds.Unknown, null,
            error ?? "Registro do Windows indisponível nesta plataforma.");
    public static HkcuValueRead Failed(string error) =>
        new(HkcuReadStatus.Error, RegistryValueKinds.Unknown, null, error);
}

public enum HkcuWriteStatus
{
    Success,
    AccessDenied,
    MissingKey,
    InvalidTarget,
    Unsupported,
    Failed
}

public sealed record HkcuWriteResult(HkcuWriteStatus Status, string? Error)
{
    public static HkcuWriteResult Ok() => new(HkcuWriteStatus.Success, null);
    public static HkcuWriteResult Denied(string error) => new(HkcuWriteStatus.AccessDenied, error);
    public static HkcuWriteResult Missing() => new(HkcuWriteStatus.MissingKey, "Chave de destino ausente.");
    public static HkcuWriteResult Invalid(string error) => new(HkcuWriteStatus.InvalidTarget, error);
    public static HkcuWriteResult NotSupported(string? error = null) =>
        new(HkcuWriteStatus.Unsupported, error ?? "Registro do Windows indisponível nesta plataforma.");
    public static HkcuWriteResult Failed(string error) => new(HkcuWriteStatus.Failed, error);
}

/// <summary>
/// Fronteira mínima de acesso ao Registro. Somente o adaptador real implementa esta interface sobre HKCU;
/// os testes usam uma implementação em memória e nunca tocam o Registro do computador de desenvolvimento.
/// </summary>
public interface IHkcuRegistryAccess
{
    HkcuValueRead Read(OperationTarget target);
    HkcuWriteResult SetDWord(OperationTarget target, int value);
    HkcuWriteResult DeleteValue(OperationTarget target);
}

/// <summary>
/// Acesso real, estritamente limitado a HKCU do usuário atual e aos pares exatos de <see cref="HkcuTargetPolicy"/>.
/// Não cria chaves, não remove chaves, não abre HKLM para escrita e não executa comandos ou scripts.
/// </summary>
public sealed class HkcuRegistryAccess : IHkcuRegistryAccess
{
    public HkcuValueRead Read(OperationTarget target)
    {
        if (!HkcuTargetPolicy.IsAllowed(target)) return HkcuValueRead.Invalid(HkcuTargetPolicy.RejectionReason(target));
        if (!OperatingSystem.IsWindows()) return HkcuValueRead.NotSupported();
        if (!Environment.Is64BitProcess)
            return HkcuValueRead.NotSupported(
                "Processo não x64: o adaptador recusa o redirecionamento WOW6432Node do Registro.");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(target.Path, writable: false);
            if (key is null) return HkcuValueRead.MissingKey();
            if (!key.GetValueNames().Any(name =>
                    string.Equals(name, target.ValueName, StringComparison.OrdinalIgnoreCase)))
                return HkcuValueRead.MissingValue();

            var kind = key.GetValueKind(target.ValueName!);
            var value = key.GetValue(target.ValueName!, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return kind switch
            {
                RegistryValueKind.DWord => HkcuValueRead.Ok(RegistryValueKinds.DWord,
                    value is int number
                        ? number.ToString(CultureInfo.InvariantCulture)
                        : Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
                RegistryValueKind.String => HkcuValueRead.Ok(RegistryValueKinds.String, value as string),
                RegistryValueKind.ExpandString => HkcuValueRead.Ok(RegistryValueKinds.ExpandString, value as string),
                _ => HkcuValueRead.Ok(kind.ToString(), null)
            };
        }
        catch (SecurityException error) { return HkcuValueRead.Denied(error.Message); }
        catch (UnauthorizedAccessException error) { return HkcuValueRead.Denied(error.Message); }
        catch (Exception error) when (error is IOException or ArgumentException or ObjectDisposedException)
        {
            return HkcuValueRead.Failed(error.Message);
        }
    }

    public HkcuWriteResult SetDWord(OperationTarget target, int value)
    {
        if (!HkcuTargetPolicy.IsAllowed(target)) return HkcuWriteResult.Invalid(HkcuTargetPolicy.RejectionReason(target));
        if (!OperatingSystem.IsWindows()) return HkcuWriteResult.NotSupported();
        if (!Environment.Is64BitProcess)
            return HkcuWriteResult.NotSupported(
                "Processo não x64: o adaptador recusa o redirecionamento WOW6432Node do Registro.");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(target.Path, writable: true);
            if (key is null) return HkcuWriteResult.Missing();
            key.SetValue(target.ValueName!, value, RegistryValueKind.DWord);
            return HkcuWriteResult.Ok();
        }
        catch (SecurityException error) { return HkcuWriteResult.Denied(error.Message); }
        catch (UnauthorizedAccessException error) { return HkcuWriteResult.Denied(error.Message); }
        catch (Exception error) when (error is IOException or ArgumentException or ObjectDisposedException)
        {
            return HkcuWriteResult.Failed(error.Message);
        }
    }

    public HkcuWriteResult DeleteValue(OperationTarget target)
    {
        if (!HkcuTargetPolicy.IsAllowed(target)) return HkcuWriteResult.Invalid(HkcuTargetPolicy.RejectionReason(target));
        if (!OperatingSystem.IsWindows()) return HkcuWriteResult.NotSupported();
        if (!Environment.Is64BitProcess)
            return HkcuWriteResult.NotSupported(
                "Processo não x64: o adaptador recusa o redirecionamento WOW6432Node do Registro.");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(target.Path, writable: true);
            if (key is null) return HkcuWriteResult.Missing();
            key.DeleteValue(target.ValueName!, throwOnMissingValue: false);
            return HkcuWriteResult.Ok();
        }
        catch (SecurityException error) { return HkcuWriteResult.Denied(error.Message); }
        catch (UnauthorizedAccessException error) { return HkcuWriteResult.Denied(error.Message); }
        catch (Exception error) when (error is IOException or ArgumentException or ObjectDisposedException)
        {
            return HkcuWriteResult.Failed(error.Message);
        }
    }
}
