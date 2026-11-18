using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.AppsStartup;

/// <summary>DPAPI CurrentUser, sem UI. Não composto no App sintético; falha nunca recua para texto aberto.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppsSecretProtector : IAppsSecretProtector
{
    public bool IsSynthetic => false;
    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (input.Length is 0 or > 1048576) throw new ArgumentException("invalid-protection-size");
        var memory = Marshal.AllocHGlobal(input.Length);
        DataBlob output = default;
        try
        {
            Marshal.Copy(input, 0, memory, input.Length);
            var blob = new DataBlob { Size = input.Length, Data = memory };
            var success = protect
                ? CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (output.Size is <= 0 or > 2097152) throw new InvalidDataException("invalid-protected-state");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(memory, i, 0);
            Marshal.FreeHGlobal(memory);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
