using System.Runtime.InteropServices;

namespace SentinelCore.Infrastructure.Windows;

internal sealed record DisplayPath(
    string? FriendlyName,
    string? ManufacturerCode,
    ushort ProductCode,
    uint OutputTechnology,
    uint? ConnectorInstance,
    string? GdiDeviceName,
    uint? Width,
    uint? Height,
    string? RefreshHz,
    bool? HdrSupported,
    bool? HdrEnabled,
    IReadOnlyList<string> SupportedModes,
    bool SupportedModesTruncated,
    string? DevicePath);

internal sealed class DisplayConfigReadResult
{
    public List<DisplayPath> Paths { get; } = [];
    public List<string> Limitations { get; } = [];
}

// Read-only use of the DisplayConfig API. QueryDisplayConfig reports active paths;
// EnumDisplaySettings is used only to enumerate modes the driver exposes.
internal static class DisplayConfigReader
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint QdcVirtualModeAware = 0x00000010;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidParameter = 87;
    private const uint GetSourceName = 1;
    private const uint GetTargetName = 2;
    private const uint GetAdvancedColorInfo = 9;
    private const uint SourceModeInfo = 1;
    private const uint EnumCurrentSettings = 0xFFFFFFFF;
    private const int MaximumModeEnumerations = 512;
    private const int MaximumSupportedModeGroups = 24;
    private const int MaximumDisplayPaths = 64;
    private const int MaximumDisplayModes = 512;

    public static DisplayConfigReadResult Read(CancellationToken cancellationToken)
    {
        var result = new DisplayConfigReadResult();
        var query = QueryActivePaths(result);
        if (query is null) return result;
        foreach (var path in query.Value.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.TargetInfo.TargetAvailable == 0) continue;
            var target = ReadTargetName(path, result);
            var source = ReadSourceName(path, result);
            var advanced = ReadAdvancedColor(path, result);
            var (width, height) = ReadActiveSize(path, query.Value.Modes, source, result);
            var refresh = path.TargetInfo.RefreshRate.Denominator == 0
                ? null
                : HardwareNormalizers.RefreshHz(path.TargetInfo.RefreshRate.Numerator,
                    path.TargetInfo.RefreshRate.Denominator);
            var modes = source is null ? ([], false) : EnumerateModes(source, result, cancellationToken);
            result.Paths.Add(new DisplayPath(target?.FriendlyName, target?.ManufacturerCode,
                target?.ProductCode ?? 0, path.TargetInfo.OutputTechnology, target?.ConnectorInstance, source,
                width, height, refresh, advanced?.Supported, advanced?.Enabled, modes.Item1, modes.Item2,
                target?.DevicePath));
        }
        if (result.Paths.Count == 0)
            result.Limitations.Add("Nenhum caminho de exibição ativo foi reportado pelo DisplayConfig.");
        return result;
    }

    private static (DisplayConfigPathInfo[] Paths, DisplayConfigModeInfo[] Modes)? QueryActivePaths(
        DisplayConfigReadResult result)
    {
        var flags = QdcOnlyActivePaths | QdcVirtualModeAware;
        var query = Execute(flags, result);
        if (query is null)
        {
            result.Limitations.Clear();
            query = Execute(QdcOnlyActivePaths, result);
        }
        return query;
    }

    private static (DisplayConfigPathInfo[] Paths, DisplayConfigModeInfo[] Modes)? Execute(uint flags,
        DisplayConfigReadResult result)
    {
        // QueryDisplayConfig desta versão do Windows exige buffers alocados; passar NULL
        // retorna ERROR_INVALID_PARAMETER. Os tamanhos vêm de GetDisplayConfigBufferSizes.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var sizeStatus = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (sizeStatus != ErrorSuccess)
            {
                result.Limitations.Add($"GetDisplayConfigBufferSizes indisponível ({sizeStatus}); monitores ativos não puderam ser identificados. Nenhuma elevação solicitada.");
                return null;
            }
            if (pathCount == 0) return ([], []);
            if (pathCount > MaximumDisplayPaths || modeCount > MaximumDisplayModes)
            {
                result.Limitations.Add($"DisplayConfig reportou {pathCount} caminho(s) e {modeCount} modo(s), acima do limite seguro da coleta.");
                return null;
            }
            var pathArray = new DisplayConfigPathInfo[pathCount];
            var modeArray = new DisplayConfigModeInfo[modeCount];
            var status = QueryDisplayConfig(flags, ref pathCount, pathArray, ref modeCount, modeArray, IntPtr.Zero);
            if (status == ErrorInsufficientBuffer) continue;
            if (status == ErrorInvalidParameter && flags != QdcOnlyActivePaths)
            {
                result.Limitations.Add("DisplayConfig recusou o modo virtual; consulta repetida apenas com caminhos ativos.");
                return null;
            }
            if (status != ErrorSuccess)
            {
                result.Limitations.Add($"QueryDisplayConfig falhou ao ler caminhos ativos ({status}).");
                return null;
            }
            return (pathArray[..(int)Math.Min(pathCount, (uint)pathArray.Length)],
                modeArray[..(int)Math.Min(modeCount, (uint)modeArray.Length)]);
        }
        result.Limitations.Add("A topologia de exibição mudou durante a leitura; caminhos ativos não foram confirmados.");
        return null;
    }

    private sealed record TargetName(string? FriendlyName, string? ManufacturerCode, ushort ProductCode,
        uint? ConnectorInstance, string? DevicePath);

    private static TargetName? ReadTargetName(DisplayConfigPathInfo path, DisplayConfigReadResult result)
    {
        var packet = new DisplayConfigTargetDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetTargetName,
                Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id
            }
        };
        var status = DisplayConfigGetDeviceInfo(ref packet);
        if (status != ErrorSuccess)
        {
            result.Limitations.Add($"Nome do monitor não exposto pelo DisplayConfig ({status}).");
            return null;
        }
        return new(packet.FriendlyName.TrimEnd('\0'), HardwareNormalizers.EdidManufacturer(packet.EdidManufactureId),
            packet.EdidProductCodeId, packet.ConnectorInstance, packet.DevicePath.TrimEnd('\0'));
    }

    private static string? ReadSourceName(DisplayConfigPathInfo path, DisplayConfigReadResult result)
    {
        var packet = new DisplayConfigSourceDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetSourceName,
                Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id
            }
        };
        var status = DisplayConfigGetDeviceInfo(ref packet);
        if (status != ErrorSuccess)
        {
            result.Limitations.Add($"Nome GDI da saída de vídeo indisponível ({status}).");
            return null;
        }
        var name = packet.GdiDeviceName.TrimEnd('\0');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static (bool? Supported, bool? Enabled)? ReadAdvancedColor(DisplayConfigPathInfo path,
        DisplayConfigReadResult result)
    {
        var packet = new DisplayConfigAdvancedColorInfo
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetAdvancedColorInfo,
                Size = (uint)Marshal.SizeOf<DisplayConfigAdvancedColorInfo>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id
            }
        };
        var status = DisplayConfigGetDeviceInfo(ref packet);
        if (status != ErrorSuccess)
        {
            result.Limitations.Add($"Informação de cor avançada/HDR indisponível ({status}).");
            return null;
        }
        return ((packet.Flags & 1) != 0, (packet.Flags & 2) != 0);
    }

    private static (uint? Width, uint? Height) ReadActiveSize(DisplayConfigPathInfo path,
        DisplayConfigModeInfo[] modes, string? source, DisplayConfigReadResult result)
    {
        var modeIndex = path.SourceInfo.ModeInfoIdx;
        if (modeIndex < modes.Length && modes[modeIndex].InfoType == SourceModeInfo)
        {
            var mode = modes[modeIndex].SourceMode;
            if (mode.Width > 0 && mode.Height > 0) return (mode.Width, mode.Height);
        }
        if (source is not null)
        {
            var current = new DevModeW { Size = (ushort)Marshal.SizeOf<DevModeW>() };
            if (EnumDisplaySettingsW(source, EnumCurrentSettings, ref current))
                return (current.PelsWidth > 0 ? current.PelsWidth : null,
                    current.PelsHeight > 0 ? current.PelsHeight : null);
        }
        result.Limitations.Add("Resolução ativa não exposta pelo DisplayConfig.");
        return (null, null);
    }

    private static (IReadOnlyList<string> Modes, bool Truncated) EnumerateModes(string deviceName,
        DisplayConfigReadResult result, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, SortedSet<uint>>(StringComparer.Ordinal);
        var truncated = false;
        for (var index = 0; index < MaximumModeEnumerations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mode = new DevModeW { Size = (ushort)Marshal.SizeOf<DevModeW>() };
            if (!EnumDisplaySettingsW(deviceName, (uint)index, ref mode)) break;
            if (mode.PelsWidth == 0 || mode.PelsHeight == 0) continue;
            var key = $"{mode.PelsWidth}x{mode.PelsHeight}";
            if (!groups.TryGetValue(key, out var rates))
            {
                if (groups.Count >= MaximumSupportedModeGroups) { truncated = true; continue; }
                groups[key] = rates = new SortedSet<uint>();
            }
            if (mode.DisplayFrequency > 0) rates.Add(mode.DisplayFrequency);
            if (index == MaximumModeEnumerations - 1) truncated = true;
        }
        if (truncated)
            result.Limitations.Add($"{deviceName}: modos suportados limitados a {MaximumSupportedModeGroups} resoluções; a lista não é exaustiva.");
        var modes = groups
            .Select(group => group.Value.Count == 0
                ? group.Key
                : $"{group.Key} @ {string.Join(", ", group.Value)} Hz")
            .ToArray();
        return (modes, truncated);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Region2D
    {
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public PointL Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HSyncFreq;
        public Rational VSyncFreq;
        public Region2D ActiveSize;
        public Region2D TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public SourceMode SourceMode;
        [FieldOffset(16)] public VideoSignalInfo TargetMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 220)]
    private struct DevModeW
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(168)] public uint BitsPerPel;
        [FieldOffset(172)] public uint PelsWidth;
        [FieldOffset(176)] public uint PelsHeight;
        [FieldOffset(184)] public uint DisplayFrequency;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[]? pathArray, ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[]? modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName packet);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName packet);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdvancedColorInfo packet);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, uint modeIndex, ref DevModeW devMode);
}
