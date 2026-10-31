using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SentinelCore.Infrastructure.Windows;

public static class WindowsShell
{
    // Uses a PIDL instead of command-line parsing. No shell command is constructed.
    public static Task OpenReportLocationAsync(string reportPath)
    {
        var path = ResolveExistingFilePath(reportPath);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false; var item = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2)); initialized = true;
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out item, 0, out _));
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally
            {
                if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
                if (initialized) CoUninitialize();
            }
        }) { IsBackground = true, Name = "SentinelCore report location" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    internal static string ResolveExistingFilePath(string filePath)
    {
        var path = Path.GetFullPath(filePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Relatório não encontrado. Reabra o aplicativo para reconstruí-lo.", path);
        // Packaged hosts can redirect LocalAppData. Explorer runs outside that context.
        // Resolve the existing file handle so the Shell receives its actual on-disk location.
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= buffer.Capacity) throw new IOException("Caminho físico excede o limite suportado.");
        var physical = buffer.ToString();
        if (physical.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + physical[8..];
        return physical.StartsWith(@"\\?\", StringComparison.Ordinal) ? physical[4..] : physical;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr item, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);
}
