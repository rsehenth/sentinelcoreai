using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal static class HardwareNormalizers
{
    public static string StableId(string kind, string identity) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\u001f" + identity)))
            .ToLowerInvariant()[..24];

    public static string? HardwareId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var parts = instanceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && (parts[1].StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) ||
                                  parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase)))
            return parts[0] + "\\" + parts[1];
        return parts.Length > 0 ? parts[0] : null;
    }

    public static bool IsVirtual(string? instanceId, string? name)
    {
        if (instanceId is not null)
        {
            if (instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                instanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (name is null) return false;
        return name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Hyper-V Video", StringComparison.OrdinalIgnoreCase);
    }

    public static DevicePresence Presence(PnpDeviceEntry? entry, bool? virtualDevice = null)
    {
        if (virtualDevice == true) return DevicePresence.Virtual;
        return entry?.Presence ?? DevicePresence.Unknown;
    }

    public static string? CpuArchitecture(ulong? value) => value switch
    {
        0 => "x86",
        1 => "MIPS",
        2 => "Alpha",
        3 => "PowerPC",
        5 => "ARM",
        6 => "IA64",
        9 => "x64",
        12 => "ARM64",
        null => null,
        _ => $"Arquitetura {value}"
    };

    public static string? MemoryType(ulong? value) => value switch
    {
        1 => "Other",
        2 => "Unknown",
        3 => "DRAM",
        4 => "EDRAM",
        5 => "VRAM",
        6 => "SRAM",
        7 => "RAM",
        8 => "ROM",
        9 => "FLASH",
        10 => "EEPROM",
        11 => "FEPROM",
        12 => "EPROM",
        13 => "CDRAM",
        14 => "3DRAM",
        15 => "SDRAM",
        16 => "SGRAM",
        17 => "RDRAM",
        18 => "DDR",
        19 => "DDR2",
        20 => "DDR2 FB-DIMM",
        24 => "DDR3",
        25 => "FBD2",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        31 => "Logical non-volatile device",
        32 => "HBM",
        33 => "HBM2",
        34 => "DDR5",
        35 => "LPDDR5",
        null => null,
        _ => $"Tipo SMBIOS {value}"
    };

    public static string? MemoryFormFactor(ulong? value) => value switch
    {
        1 => "Other",
        2 => "SIP",
        3 => "DIP",
        4 => "ZIP",
        5 => "SOJ",
        6 => "Proprietary",
        7 => "SIMM",
        8 => "DIMM",
        9 => "TSOP",
        10 => "PGA",
        11 => "RIMM",
        12 => "SODIMM",
        13 => "SRIMM",
        14 => "SMD",
        15 => "SSMP",
        16 => "QFP",
        17 => "TQFP",
        18 => "SOIC",
        19 => "LCC",
        20 => "PLCC",
        21 => "BGA",
        22 => "FPBGA",
        23 => "LGA",
        24 => "FB-DIMM",
        null => null,
        _ => $"Formato {value}"
    };

    public static string? BusType(ulong? value) => value switch
    {
        0 => "Unknown",
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        null => null,
        _ => $"Barramento {value}"
    };

    public static string? MediaType(ulong? value) => value switch
    {
        0 => "Unspecified",
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        null => null,
        _ => $"Mídia {value}"
    };

    public static string? HealthStatus(ulong? value) => value switch
    {
        0 => "Healthy (reportado)",
        1 => "Warning (reportado)",
        2 => "Unhealthy (reportado)",
        5 => "Unknown (reportado)",
        null => null,
        _ => $"Saúde {value} (reportado)"
    };

    public static string? OperationalStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value switch
            {
                "2" => "OK",
                "3" => "Degraded",
                "4" => "Stress",
                "5" => "Predictive Failure",
                "6" => "Error",
                "7" => "Non-Recoverable Error",
                "8" => "Starting",
                "9" => "Stopping",
                "10" => "Stopped",
                "11" => "In Service",
                "12" => "No Contact",
                "13" => "Lost Communication",
                "14" => "Aborted",
                "15" => "Dormant",
                "16" => "Supporting Entity in Error",
                "17" => "Completed",
                "18" => "Power Mode",
                "19" => "Relocating",
                _ => $"Estado {value}"
            });
        return string.Join(", ", values);
    }

    public static string NetworkKind(string? name, string? instanceId, bool? physical, string? adapterType,
        string? service)
    {
        var text = string.Join(" ", name, adapterType, service);
        if (text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            instanceId?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true) return "bluetooth";
        if (text.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
            new[] { "WireGuard", "Wintun", "Tap", "RasPppoe", "SSTP", "OpenVPN", "CloudflareWARP" }
                .Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase))) return "vpn";
        if (physical == false || instanceId?.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) == true ||
            instanceId?.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) == true) return "virtual";
        if (text.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("802.11", StringComparison.OrdinalIgnoreCase)) return "wifi";
        if (text.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) return "ethernet";
        return "other";
    }

    public static string ConnectionType(uint value) => value switch
    {
        0 => "VGA (HD15)",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        8 => "D-JPN",
        9 => "SDI",
        10 => "DisplayPort (externo)",
        11 => "DisplayPort (embutido)",
        12 => "UDI (externo)",
        13 => "UDI (embutido)",
        14 => "SDTV dongle",
        15 => "Miracast",
        16 => "Indireto (com fio)",
        17 => "Virtual",
        0x80000000 => "Interno",
        0xFFFFFFFF => "Não informado (FORCE_UINT)",
        _ => $"Tecnologia {value}"
    };

    public static string? EdidManufacturer(ushort value)
    {
        var first = (value >> 10) & 0x1F;
        var second = (value >> 5) & 0x1F;
        var third = value & 0x1F;
        return first is >= 1 and <= 26 && second is >= 1 and <= 26 && third is >= 1 and <= 26
            ? $"{(char)('A' + first - 1)}{(char)('A' + second - 1)}{(char)('A' + third - 1)}"
            : null;
    }

    public static string? RefreshHz(uint numerator, uint denominator)
    {
        if (denominator == 0) return null;
        return (numerator / (double)denominator).ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string? Number(ulong? value) => value?.ToString(CultureInfo.InvariantCulture);
    public static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    public static string? Number(uint? value) => value?.ToString(CultureInfo.InvariantCulture);
    public static string? Date(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string JoinAscii(ushort[]? values)
    {
        if (values is null || values.Length == 0) return "";
        var text = new string(values.Where(v => v != 0).Select(v => (char)v).ToArray()).Trim();
        return text;
    }
}
