using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SentinelCore.Infrastructure.Windows;

public sealed record StorageIdentity(string LogicalPath, string? PhysicalPath, bool Redirected, string? Limitation);

// Packaged hosts can redirect LocalAppData without changing the logical path the app sees.
// This resolver only queries the final path of an existing directory; it never moves or creates data.
public static class WindowsStorageIdentity
{
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    public static StorageIdentity Resolve(string logicalPath)
    {
        string logical;
        try
        {
            logical = Path.GetFullPath(logicalPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(logicalPath, null, false, $"Caminho lógico inválido ({error.GetType().Name}).");
        }

        if (!Directory.Exists(logical))
            return new(logical, null, false, "Diretório de dados ainda não existe.");

        using var handle = CreateFileW(logical, 0,
            (uint)(FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return new(logical, null, false, new Win32Exception(Marshal.GetLastWin32Error()).Message);

        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            return new(logical, null, false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (length >= buffer.Capacity)
            return new(logical, null, false, "Caminho físico excede o limite suportado.");

        var physical = Normalize(buffer.ToString());
        var redirected = !string.Equals(Normalize(logical), physical, StringComparison.OrdinalIgnoreCase);
        return new(logical, physical, redirected, null);
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length,
        uint flags);
}
