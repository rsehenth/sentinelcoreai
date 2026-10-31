using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using Xunit;

namespace SentinelCore.Tests;

public sealed class HardwareNormalizerTests
{
    [Fact]
    public void StableIdIsDeterministicAndHidesIdentity()
    {
        var first = HardwareNormalizers.StableId("disk", "SERIAL-SECRET-123");
        var second = HardwareNormalizers.StableId("disk", "SERIAL-SECRET-123");
        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first);
        Assert.Equal(7 + 24, first.Length);
        Assert.DoesNotContain("SERIAL-SECRET-123", first);
        Assert.NotEqual(first, HardwareNormalizers.StableId("volume", "SERIAL-SECRET-123"));
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2482&SUBSYS_88881043&REV_A1\4&2D4B4B&0&0008", @"PCI\VEN_10DE&DEV_2482&SUBSYS_88881043&REV_A1")]
    [InlineData(@"USB\VID_046D&PID_C52B&REV_1201\5&2A1B3C4D&0&1", @"USB\VID_046D&PID_C52B&REV_1201")]
    [InlineData(@"HID\VID_046D&PID_C52B&MI_01&COL01\7&1A2B3C4D&0&0000", @"HID\VID_046D&PID_C52B&MI_01&COL01")]
    public void HardwareIdKeepsVendorAndModelWithoutInstanceSuffix(string instanceId, string expected) =>
        Assert.Equal(expected, HardwareNormalizers.HardwareId(instanceId));

    [Fact]
    public void HardwareIdHandlesMissingValues()
    {
        Assert.Null(HardwareNormalizers.HardwareId(null));
        Assert.Null(HardwareNormalizers.HardwareId("   "));
        Assert.Equal("DISPLAY", HardwareNormalizers.HardwareId(@"DISPLAY\DELA1B5\5&1234&0&UID4352"));
    }

    [Theory]
    [InlineData(@"ROOT\NET\0000", null, true)]
    [InlineData(@"SWD\MMDEVAPI\{00000000-0000-0000-0000-000000000000}", null, true)]
    [InlineData(@"PCI\VEN_10DE&DEV_2482", null, false)]
    [InlineData(null, "Microsoft Basic Display Adapter", true)]
    [InlineData(null, "Hyper-V Video", true)]
    [InlineData(null, "NVIDIA GeForce RTX 4080", false)]
    public void VirtualDetectionUsesInstanceRootAndKnownSoftwareNames(string? instanceId, string? name, bool expected) =>
        Assert.Equal(expected, HardwareNormalizers.IsVirtual(instanceId, name));

    [Fact]
    public void PresenceDistinguishesPresentVirtualDisconnectedAndUnknown()
    {
        var present = Entry(presence: DevicePresence.Present);
        var disconnected = Entry(presence: DevicePresence.Disconnected);
        Assert.Equal(DevicePresence.Present, HardwareNormalizers.Presence(present));
        Assert.Equal(DevicePresence.Disconnected, HardwareNormalizers.Presence(disconnected));
        Assert.Equal(DevicePresence.Unknown, HardwareNormalizers.Presence(null));
        Assert.Equal(DevicePresence.Virtual, HardwareNormalizers.Presence(present, virtualDevice: true));
    }

    [Theory]
    [InlineData(0UL, "x86")]
    [InlineData(5UL, "ARM")]
    [InlineData(6UL, "IA64")]
    [InlineData(9UL, "x64")]
    [InlineData(12UL, "ARM64")]
    [InlineData(99UL, "Arquitetura 99")]
    [InlineData(null, null)]
    public void CpuArchitectureMapsKnownAndUnknownValues(ulong? value, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.CpuArchitecture(value));

    [Theory]
    [InlineData(18UL, "DDR")]
    [InlineData(24UL, "DDR3")]
    [InlineData(26UL, "DDR4")]
    [InlineData(34UL, "DDR5")]
    [InlineData(35UL, "LPDDR5")]
    [InlineData(99UL, "Tipo SMBIOS 99")]
    [InlineData(null, null)]
    public void MemoryTypeMapsRawSmbiosCodes(ulong? value, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.MemoryType(value));

    [Theory]
    [InlineData(12UL, "SODIMM")]
    [InlineData(8UL, "DIMM")]
    [InlineData(99UL, "Formato 99")]
    [InlineData(null, null)]
    public void MemoryFormFactorMapsWmiValues(ulong? value, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.MemoryFormFactor(value));

    [Theory]
    [InlineData(3UL, "HDD")]
    [InlineData(4UL, "SSD")]
    [InlineData(5UL, "SCM")]
    [InlineData(99UL, "Mídia 99")]
    public void MediaTypeMapsStorageApiValues(ulong? value, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.MediaType(value));

    [Theory]
    [InlineData(11UL, "SATA")]
    [InlineData(17UL, "NVMe")]
    [InlineData(14UL, "Virtual")]
    [InlineData(99UL, "Barramento 99")]
    public void BusTypeMapsStorageApiValues(ulong? value, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.BusType(value));

    [Theory]
    [InlineData(0UL, "Healthy (reportado)")]
    [InlineData(2UL, "Unhealthy (reportado)")]
    [InlineData(5UL, "Unknown (reportado)")]
    [InlineData(99UL, "Saúde 99 (reportado)")]
    public void HealthStatusQualifiesReportedValues(ulong? value, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.HealthStatus(value));

    [Theory]
    [InlineData("2", "OK")]
    [InlineData("2,6", "OK, Error")]
    [InlineData("12", "No Contact")]
    [InlineData("99", "Estado 99")]
    [InlineData(null, null)]
    public void OperationalStatusMapsNumericArray(string? raw, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.OperationalStatus(raw));

    [Theory]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F0", true, "Wireless LAN", "wifi")]
    [InlineData("Realtek PCIe GbE Family Controller", @"PCI\VEN_10EC&DEV_8168", true, "Ethernet 802.3", "ethernet")]
    [InlineData("WireGuard Tunnel", @"ROOT\NET\0001", false, null, "vpn")]
    [InlineData("Hyper-V Virtual Ethernet Adapter", @"ROOT\VMS_MP\0000", false, "Ethernet 802.3", "virtual")]
    [InlineData("Bluetooth Device (Personal Area Network)", @"USB\VID_8087&PID_0026", null, "Ethernet 802.3", "bluetooth")]
    [InlineData("WAN Miniport (PPTP)", @"ROOT\MS_PPTPMINIPORT\0000", false, "WAN", "vpn")]
    public void NetworkKindClassifiesAdapters(string name, string instanceId, bool? physical, string? adapterType,
        string expected) =>
        Assert.Equal(expected, HardwareNormalizers.NetworkKind(name, instanceId, physical, adapterType, null));

    [Theory]
    [InlineData(5U, "HDMI")]
    [InlineData(10U, "DisplayPort (externo)")]
    [InlineData(11U, "DisplayPort (embutido)")]
    [InlineData(0x80000000U, "Interno")]
    [InlineData(0xFFFFFFFFU, "Não informado (FORCE_UINT)")]
    [InlineData(99U, "Tecnologia 99")]
    public void ConnectionTypeMapsDisplayConfigValues(uint value, string expected) =>
        Assert.Equal(expected, HardwareNormalizers.ConnectionType(value));

    [Theory]
    [InlineData(0x10AC, "DEL")]
    [InlineData(0x4C2D, "SAM")]
    [InlineData(0, null)]
    public void EdidManufacturerDecodesFiveBitLetters(int value, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.EdidManufacturer((ushort)value));

    [Theory]
    [InlineData(60000U, 1000U, "60")]
    [InlineData(59951U, 1000U, "59.951")]
    [InlineData(60U, 0U, null)]
    public void RefreshRateUsesRationalAndHandlesZeroDenominator(uint numerator, uint denominator, string? expected) =>
        Assert.Equal(expected, HardwareNormalizers.RefreshHz(numerator, denominator));

    [Fact]
    public void JoinAsciiStopsAtNullCharacters()
    {
        Assert.Equal("DEL", HardwareNormalizers.JoinAscii([(ushort)'D', (ushort)'E', (ushort)'L', 0, 0]));
        Assert.Equal("", HardwareNormalizers.JoinAscii(null));
        Assert.Equal("", HardwareNormalizers.JoinAscii([]));
    }

    [Theory]
    [InlineData("USB", "USB xHCI Compliant Host Controller", true, "usb-controller")]
    [InlineData("USB", "USB Root Hub (USB 3.0)", true, "usb-hub")]
    [InlineData("Keyboard", "HID Keyboard Device", true, "keyboard")]
    [InlineData("Mouse", "HID-compliant mouse", true, "mouse")]
    [InlineData("Camera", "Integrated Webcam", true, "webcam")]
    [InlineData("Image", "USB Video Device", true, "webcam")]
    [InlineData("Bluetooth", "Intel(R) Wireless Bluetooth(R)", true, "bluetooth")]
    [InlineData("HIDClass", "HID-compliant consumer control device", true, "hid")]
    [InlineData("Printer", "USB Printing Support", true, "usb-device")]
    public void UsbScopeClassifiesPeripheralClasses(string className, string description, bool expected,
        string expectedKind)
    {
        var entry = Entry(className: className, description: description);
        Assert.Equal(expected, PnpScope.TryUsbKind(entry, out var kind));
        Assert.Equal(expectedKind, kind);
    }

    [Theory]
    [InlineData("MEDIA", @"USB\VID_1234&PID_5678\5&1")]
    [InlineData("DiskDrive", @"USB\VID_1234&PID_5678\5&2")]
    [InlineData("Net", @"USB\VID_1234&PID_5678\5&3")]
    [InlineData("Display", @"USB\VID_1234&PID_5678\5&4")]
    [InlineData("System", @"ACPI\PNP0A08\0")]
    public void UsbScopeDoesNotClaimCategoriesOwnedByOtherCollectors(string className, string instanceId)
    {
        Assert.False(PnpScope.TryUsbKind(Entry(instanceId: instanceId, className: className), out _));
        Assert.False(PnpScope.IsOther(Entry(instanceId: instanceId, className: className)));
    }

    [Fact]
    public void OtherScopeKeepsUnclassifiedDevicesAndExcludesUsbPeripherals()
    {
        Assert.True(PnpScope.IsOther(Entry(className: "Battery", instanceId: @"ACPI\PNP0C0A\0")));
        Assert.True(PnpScope.IsOther(Entry(className: "Printer", instanceId: @"LPTENUM\PRINTER\1")));
        Assert.False(PnpScope.IsOther(Entry(className: "Keyboard", instanceId: @"USB\VID_1234&PID_5678\5&1")));
    }

    [Fact]
    public void PeripheralItemKeepsMissingDriverFieldsAndAddsLimitations()
    {
        var binding = new DriverBinding(@"USB\VID_1234&PID_5678\5&1", "Device", null, null, null, null, null, null, null);
        var item = PeripheralItems.Create(HardwareCategory.Usb, "usb-device",
            Entry(instanceId: @"USB\VID_1234&PID_5678\5&1", className: "Printer", presence: DevicePresence.Disconnected),
            binding);
        Assert.Equal(DevicePresence.Disconnected, item.Presence);
        Assert.Contains(item.Limitations, message => message.Contains("não presente"));
        Assert.Contains(item.Limitations, message => message.Contains("Versão do driver"));
        Assert.Null(item.DriverVersion);
        Assert.Equal("Printer", item.Property("pnp_class"));
        Assert.Equal(@"USB\VID_1234&PID_5678", item.HardwareId);
    }

    [Theory]
    [InlineData("C", "C:")]
    [InlineData("d", "D:")]
    [InlineData("67", "C:")]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DriveLetterNormalizesCharactersAndCodes(string? value, string? expected) =>
        Assert.Equal(expected, StorageCollector.DriveLetter(value));

    [Theory]
    [InlineData("ABC123", "ABC123")]
    [InlineData(" to be filled by o.e.m. ", null)]
    [InlineData("UNKNOWN", null)]
    [InlineData("N/A", null)]
    [InlineData("0", null)]
    [InlineData(null, null)]
    public void CleanSerialRejectsPlaceholders(string? value, string? expected) =>
        Assert.Equal(expected, StorageCollector.CleanSerial(value));

    [Fact]
    public void ReferenceIdExtractsDeviceIdFromAssociationPath()
    {
        Assert.Equal(@"\\.\PHYSICALDRIVE0",
            StorageCollector.ReferenceId(@"Win32_DiskDrive.DeviceID=""\\.\PHYSICALDRIVE0"""));
        Assert.Equal("C:", StorageCollector.ReferenceId(@"Win32_LogicalDisk.DeviceID=""C:"""));
        Assert.Null(StorageCollector.ReferenceId(null));
        Assert.Null(StorageCollector.ReferenceId("sem referência"));
    }

    [Theory]
    [InlineData(7200U, "7200")]
    [InlineData(0U, null)]
    [InlineData(uint.MaxValue, null)]
    [InlineData(null, null)]
    public void SpindleSpeedTreatsZeroAndSentinelAsUnavailable(uint? value, string? expected) =>
        Assert.Equal(expected, StorageCollector.SpindleSpeed(value));

    [Theory]
    [InlineData(2U, "Removível")]
    [InlineData(3U, "Fixo")]
    [InlineData(4U, "Rede")]
    [InlineData(99U, "Tipo 99")]
    [InlineData(null, null)]
    public void VolumeDriveTypeMapsWindowsValues(uint? value, string? expected) =>
        Assert.Equal(expected, StorageCollector.VolumeDriveType(value));

    [Theory]
    [InlineData(0, 0u, DevicePresence.Present, null)]
    [InlineData(0, 22u, DevicePresence.Present, "Desativado (CM_PROB_DISABLED)")]
    [InlineData(0, 28u, DevicePresence.Present, "Driver não instalado (CM_PROB_FAILED_INSTALL)")]
    [InlineData(13, 0u, DevicePresence.Disconnected, null)]
    [InlineData(31, 0u, DevicePresence.Unknown, "CM_Get_DevNode_Status falhou (31)")]
    public void DevNodeStatusMapsPresenceWithoutPlatformStatusBits(int result, uint problemNumber,
        DevicePresence expected, string? expectedProblem)
    {
        var presence = PnpDeviceIndex.PresenceFromStatus(result, problemNumber, out var problem);
        Assert.Equal(expected, presence);
        Assert.Equal(expectedProblem, problem);
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#GSM5B7F#5&2A3B&0&UID4352#{e6f07b5f-ee97-40a6-ba40-6a2b1a1a2f3c}",
        @"DISPLAY\GSM5B7F\5&2A3B&0&UID4352_0", true)]
    [InlineData(@"\\?\DISPLAY#GSM5B7F#5&2A3B&0&UID4352#{e6f07b5f-ee97-40a6-ba40-6a2b1a1a2f3c}",
        @"DISPLAY\GSM5B7F\5&2A3B&0&UID9999_0", false)]
    [InlineData(null, @"DISPLAY\GSM5B7F\5&2A3B&0&UID4352_0", false)]
    [InlineData(@"\\?\DISPLAY#GSM5B7F#5&2A3B&0&UID4352#{guid}", null, false)]
    public void MonitorInstanceMatchesDisplayConfigDevicePath(string? devicePath, string? instanceName,
        bool expected) => Assert.Equal(expected, MonitorCollector.InstanceMatches(devicePath, instanceName));

    [Theory]
    [InlineData("AUS", "LXF", true)]
    [InlineData("aus", "AUS", false)]
    [InlineData("AUS", null, false)]
    [InlineData(null, "LXF", false)]
    [InlineData(" ", "LXF", false)]
    public void MonitorManufacturerMismatchOnlyWhenBothCodesDiffer(string? wmiManufacturer, string? edidCode,
        bool expected) => Assert.Equal(expected, MonitorCollector.ManufacturerMismatch(wmiManufacturer, edidCode));

    private static PnpDeviceEntry Entry(string instanceId = @"USB\VID_1234&PID_5678\5&1",
        string? className = "USB", string? description = "USB Device", string? manufacturer = "Fabricante",
        DevicePresence presence = DevicePresence.Present, string? problem = null) =>
        new(instanceId, className, description, manufacturer, "Service", presence, problem);
}
