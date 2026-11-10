using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Debloat.Native;

internal static class AppxDependencyReader
{
    private const uint PackageFilterHead = 0x10;
    private const uint PackageFilterDirect = 0x20;
    private const uint PackageFilterResource = 0x40;
    private const uint PackageFilterBundle = 0x80;
    private const uint PackageFilterOptional = 0x20000;
    private const uint Filters = PackageFilterHead | PackageFilterDirect | PackageFilterResource |
        PackageFilterBundle | PackageFilterOptional;
    private const int InsufficientBuffer = 122;
    private const uint MaximumBytes = 4 * 1024 * 1024;
    private const uint MaximumRows = 641;

    // The unpackaged desktop app uses AppModel to read the static graph by exact full name.
    internal static IReadOnlyList<string>? Read(string fullName)
    {
        if (!DebloatNativeIdentity.Safe(fullName)) return null;
        var openResult = OpenPackageInfoByFullName(fullName, 0, out var reference);
        using (reference)
        {
            if (openResult != 0 || reference.IsInvalid) return null;
            uint size = 0;
            if (GetPackageInfo(reference, Filters, ref size, IntPtr.Zero, out _) != InsufficientBuffer ||
                size is 0 or > MaximumBytes) return null;
            var capacity = size;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetPackageInfo(reference, Filters, ref size, buffer, out var count) != 0 ||
                    size > capacity || count is 0 or > MaximumRows) return null;
                var stride = Marshal.SizeOf<PackageInfo>();
                var rowsLength = checked((int)count * stride);
                if (rowsLength > size) return null;
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<PackageInfo>(IntPtr.Add(buffer, checked((int)i * stride)));
                    var name = ReadFullName(row.FullName, buffer, rowsLength, (int)size);
                    if (!DebloatNativeIdentity.Safe(name) || !names.Add(name!)) return null;
                }
                return names.Remove(fullName) ? names.Order(StringComparer.Ordinal).ToArray() : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static string? ReadFullName(IntPtr address, IntPtr buffer, int rowsLength, int size)
    {
        var offset = address.ToInt64() - buffer.ToInt64();
        if (offset < rowsLength || offset > size - sizeof(char) || offset % sizeof(char) != 0) return null;
        var limit = Math.Min((size - (int)offset) / sizeof(char), DebloatNativeLimits.MaximumText + 1);
        for (var length = 0; length < limit; length++)
            if (Marshal.ReadInt16(address, length * sizeof(char)) == 0)
                return Marshal.PtrToStringUni(address, length);
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackageInfo
    {
        private uint reserved;
        private uint flags;
        private IntPtr path;
        public IntPtr FullName;
        private IntPtr familyName;
        private PackageId id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackageId
    {
        private uint reserved;
        private uint architecture;
        private ulong version;
        private IntPtr name;
        private IntPtr publisher;
        private IntPtr resourceId;
        private IntPtr publisherId;
    }

    private sealed class PackageInfoReference() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => ClosePackageInfo(handle) == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int OpenPackageInfoByFullName(string fullName, uint reserved, out PackageInfoReference reference);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageInfo(PackageInfoReference reference, uint flags,
        ref uint length, IntPtr buffer, out uint count);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ClosePackageInfo(IntPtr reference);
}
