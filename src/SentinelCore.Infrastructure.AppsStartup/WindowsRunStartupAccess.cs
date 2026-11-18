using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

/// <summary>Não composto. TxR compara e escreve no Registro; o journal de arquivo é uma transação separada.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunStartupAccess : IRunStartupAccess
{
    public bool IsSynthetic => false;
    private static void Guard(StartupRegistryIdentity identity)
    {
        if (!AppsOperationalExecutionGate.PermitsExecution) throw new InvalidOperationException("gate");
        if (identity.Hive != "CurrentUser" || identity.Path != RunKeyStartupProvider.RunPath ||
            identity.View != "Registry64" || identity.ValueKind is not ("String" or "ExpandString") ||
            string.IsNullOrWhiteSpace(identity.ValueName) || identity.ValueName.Contains('\0'))
            throw new InvalidOperationException("unsupported-run-identity");
    }

    public RunValueState Read(StartupRegistryIdentity identity)
    {
        Guard(identity);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(identity.Path, writable: false) ?? throw new IOException("run-key-unavailable");
        return ReadValue(key, identity);
    }

    private static RunValueState ReadValue(RegistryKey key, StartupRegistryIdentity identity)
    {
        if (!key.GetValueNames().Contains(identity.ValueName, StringComparer.OrdinalIgnoreCase)) return new(false, null, null, true);
        var kind = key.GetValueKind(identity.ValueName);
        var value = key.GetValue(identity.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new(true, value as string, kind.ToString(), kind is RegistryValueKind.String or RegistryValueKind.ExpandString && value is string);
    }

    public bool DeleteIfUnchanged(StartupRegistryIdentity identity, RunValueState expected) =>
        Change(identity, expected, null);

    public bool RestoreIfAbsent(StartupRegistryIdentity identity, string content) =>
        Change(identity, new(false, null, null, true), content);

    private static bool Change(StartupRegistryIdentity identity, RunValueState expected, string? restoration)
    {
        Guard(identity);
        using var transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 15000, "SentinelCore single Run value");
        if (transaction.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var error = RegOpenKeyTransactedW(root.Handle, identity.Path, 0, 0x0001 | 0x0002 | 0x0100,
            out var handle, transaction, IntPtr.Zero);
        if (error != 0) { handle?.Dispose(); throw new Win32Exception(error); }
        using (handle)
        using (var key = RegistryKey.FromHandle(handle, RegistryView.Registry64))
        {
            if (ReadValue(key, identity) != expected) return false;
            if (restoration is null) key.DeleteValue(identity.ValueName, throwOnMissingValue: true);
            else key.SetValue(identity.ValueName, restoration, identity.ValueKind == "String" ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            if (!CommitTransaction(transaction)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return true;
    }

    [DllImport("KtmW32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr unitOfWork, uint options,
        uint isolationLevel, uint isolationFlags, uint timeout, string description);
    [DllImport("KtmW32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CommitTransaction(SafeFileHandle transaction);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyTransactedW(SafeRegistryHandle root, string subKey, uint options, int access,
        out SafeRegistryHandle result, SafeFileHandle transaction, IntPtr extendedParameter);
}
