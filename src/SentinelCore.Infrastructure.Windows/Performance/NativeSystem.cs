using System.Runtime.InteropServices;

namespace SentinelCore.Infrastructure.Windows.Performance;

internal static class NativeSystem
{
    private const int AllProcessorGroups = 0xFFFF;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    private static ulong Value(FileTime time) => ((ulong)time.High << 32) | time.Low;

    public static bool TryGetSystemTimes(out CpuCounterSample sample)
    {
        sample = default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return false;
        sample = new(Value(idle), Value(kernel), Value(user));
        return true;
    }

    public static bool TryGetMemory(out ulong installed, out ulong available)
    {
        installed = available = 0;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return false;
        installed = status.TotalPhys;
        available = status.AvailPhys;
        return true;
    }

    public static bool TryGetIoCounters(IntPtr handle, out ulong readBytes, out ulong writeBytes)
    {
        readBytes = writeBytes = 0;
        if (!GetProcessIoCounters(handle, out var counters)) return false;
        readBytes = counters.ReadTransferCount;
        writeBytes = counters.WriteTransferCount;
        return true;
    }

    public static bool TryGetProcessIoCounters(int pid, out ulong readBytes, out ulong writeBytes)
    {
        readBytes = writeBytes = 0;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return TryGetIoCounters(handle, out readBytes, out writeBytes);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static int LogicalProcessors()
    {
        try
        {
            var count = GetActiveProcessorCount(AllProcessorGroups);
            return count is > 0 and <= 4096 ? (int)count : Environment.ProcessorCount;
        }
        catch (EntryPointNotFoundException) { return Environment.ProcessorCount; }
    }

    public static ushort ProcessorGroupCount()
    {
        try
        {
            return GetActiveProcessorGroupCount();
        }
        catch (EntryPointNotFoundException) { return 1; }
    }
}
