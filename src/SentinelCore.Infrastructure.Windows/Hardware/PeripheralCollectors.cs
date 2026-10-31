using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed class NetworkCollector : HardwareCollectorBase
{
    public override string Id => "network";
    public override HardwareCategory Category => HardwareCategory.Network;
    public override string Source =>
        @"WMI root\cimv2: Win32_NetworkAdapter + Win32_PnPSignedDriver (sem MAC, IP ou tráfego)";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var rows = wmi.Query(
            "SELECT DeviceID,Name,NetConnectionID,Manufacturer,PNPDeviceID,AdapterType,PhysicalAdapter,NetEnabled,NetConnectionStatus,ServiceName FROM Win32_NetworkAdapter");
        var items = new List<HardwareItem>();
        foreach (var row in rows)
        {
            var name = wmi.Text(row, "Name");
            var pnpId = wmi.TextOpt(row, "PNPDeviceID");
            var kind = HardwareNormalizers.NetworkKind(name, pnpId, wmi.BoolOpt(row, "PhysicalAdapter"),
                wmi.TextOpt(row, "AdapterType"), wmi.TextOpt(row, "ServiceName"));
            var isVirtual = kind is "virtual" or "vpn";
            var entry = context.Pnp.Find(pnpId);
            var binding = context.Drivers.Find(pnpId);
            var enabled = wmi.BoolOpt(row, "NetEnabled");
            var itemLimitations = new List<string>();
            if (pnpId is null) itemLimitations.Add("Sem PNPDeviceID; presença e driver podem estar indisponíveis.");
            if (context.Pnp.Find(pnpId) is null && pnpId is not null)
                itemLimitations.Add("Dispositivo PnP correspondente não enumerado; presença indeterminada.");
            var properties = new List<HardwareProperty>();
            Add(properties, "adapter_type", wmi.TextOpt(row, "AdapterType"));
            Add(properties, "physical_adapter", wmi.BoolOpt(row, "PhysicalAdapter") is { } physical
                ? (physical ? "Sim" : "Não") : null);
            Add(properties, "connection_name", wmi.TextOpt(row, "NetConnectionID"));
            Add(properties, "enabled", enabled is null ? null : enabled.Value ? "Sim" : "Não");
            Add(properties, "connection_status", ConnectionStatus(wmi.UInt32Opt(row, "NetConnectionStatus")));
            Add(properties, "service_name", wmi.TextOpt(row, "ServiceName"));
            items.Add(new HardwareItem(Category, kind, HardwareNormalizers.StableId("network",
                    pnpId ?? wmi.TextOpt(row, "DeviceID") ?? name ?? Guid.NewGuid().ToString("N")),
                name ?? "Adaptador de rede", wmi.TextOpt(row, "Manufacturer"), name,
                HardwareNormalizers.HardwareId(pnpId), binding?.Provider, binding?.Version, binding?.DateUtc,
                binding?.DeviceClass, binding?.InfName, null,
                entry?.Problem ?? (enabled is false ? "Desativado (reportado)" : enabled is true ? "Habilitado" : null),
                HardwareNormalizers.Presence(entry, isVirtual), properties, itemLimitations));
        }
        return items;
    }

    private static string? ConnectionStatus(uint? value) => value switch
    {
        0 => "Desconectado",
        1 => "Conectando",
        2 => "Conectado",
        3 => "Desconectando",
        4 => "Hardware ausente (reportado)",
        5 => "Hardware desativado (reportado)",
        6 => "Falha de hardware (reportada)",
        7 => "Mídia desconectada",
        8 => "Autenticando",
        9 => "Autenticação concluída",
        10 => "Falha de autenticação",
        11 => "Endereço inválido",
        12 => "Credenciais necessárias",
        null => null,
        _ => $"Estado {value}"
    };
}

internal sealed class AudioCollector : HardwareCollectorBase
{
    public override string Id => "audio";
    public override HardwareCategory Category => HardwareCategory.Audio;
    public override string Source =>
        @"WMI root\cimv2: Win32_SoundDevice + Win32_PnPEntity (PNPClass=AudioEndpoint) + Win32_PnPSignedDriver";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var devices = wmi.Query(
            "SELECT Name,Manufacturer,PNPDeviceID,Status,ConfigManagerErrorCode FROM Win32_SoundDevice", allowEmpty: true);
        var endpoints = wmi.Query(
            "SELECT Name,PNPDeviceID,Manufacturer,Status,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='AudioEndpoint'",
            allowEmpty: true);
        if (devices.Count == 0 && endpoints.Count == 0)
            limitations.Add("Nenhum dispositivo de áudio foi exposto pelas consultas locais.");
        var items = new List<HardwareItem>();
        foreach (var row in devices)
        {
            var pnpId = wmi.TextOpt(row, "PNPDeviceID");
            var name = wmi.TextOpt(row, "Name") ?? "Dispositivo de áudio";
            var entry = context.Pnp.Find(pnpId);
            var binding = context.Drivers.Find(pnpId);
            var itemLimitations = new List<string>();
            if (pnpId is null) itemLimitations.Add("Sem PNPDeviceID; driver e presença podem estar indisponíveis.");
            var properties = new List<HardwareProperty>();
            Add(properties, "status_reported", wmi.TextOpt(row, "Status"));
            Add(properties, "config_manager_error_code", wmi.TextOpt(row, "ConfigManagerErrorCode"));
            Add(properties, "pnp_class", entry?.ClassName);
            items.Add(new HardwareItem(Category, "audio-device",
                HardwareNormalizers.StableId("audio", pnpId ?? name + "|" + wmi.TextOpt(row, "Manufacturer")),
                name, wmi.TextOpt(row, "Manufacturer"), null, HardwareNormalizers.HardwareId(pnpId),
                binding?.Provider, binding?.Version, binding?.DateUtc, binding?.DeviceClass, binding?.InfName, null,
                entry?.Problem, HardwareNormalizers.Presence(entry, HardwareNormalizers.IsVirtual(pnpId, name)),
                properties, itemLimitations));
        }
        foreach (var row in endpoints)
        {
            var pnpId = wmi.TextOpt(row, "PNPDeviceID");
            var name = wmi.TextOpt(row, "Name") ?? "Endpoint de áudio";
            var entry = context.Pnp.Find(pnpId);
            var binding = context.Drivers.Find(pnpId);
            var isVirtual = HardwareNormalizers.IsVirtual(pnpId, name);
            var itemLimitations = new List<string>();
            if (pnpId is null) itemLimitations.Add("Sem PNPDeviceID; presença do endpoint indeterminada.");
            var properties = new List<HardwareProperty>();
            Add(properties, "status_reported", wmi.TextOpt(row, "Status"));
            Add(properties, "config_manager_error_code", wmi.TextOpt(row, "ConfigManagerErrorCode"));
            Add(properties, "pnp_class", entry?.ClassName ?? "AudioEndpoint");
            items.Add(new HardwareItem(Category, "audio-endpoint",
                HardwareNormalizers.StableId("audio-endpoint", pnpId ?? name),
                name, wmi.TextOpt(row, "Manufacturer"), null, HardwareNormalizers.HardwareId(pnpId),
                binding?.Provider, binding?.Version, binding?.DateUtc, binding?.DeviceClass, binding?.InfName, null,
                entry?.Problem, HardwareNormalizers.Presence(entry, isVirtual), properties, itemLimitations));
        }
        return items;
    }
}

internal sealed class UsbPeripheralCollector : HardwareCollectorBase
{
    public override string Id => "usb-peripheral";
    public override HardwareCategory Category => HardwareCategory.Usb;
    public override string Source => "SetupAPI + CFGMGR32 (dispositivos PnP presentes, virtuais e não presentes)";
    protected override bool UsesWmi => false;

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        limitations.AddRange(context.Pnp.Limitations);
        limitations.AddRange(context.Drivers.Limitations);
        var items = new List<HardwareItem>();
        var capped = false;
        foreach (var entry in Ordered(context.Pnp.Devices))
        {
            if (!PnpScope.TryUsbKind(entry, out var kind)) continue;
            if (items.Count >= 400) { capped = true; break; }
            items.Add(PeripheralItems.Create(Category, kind, entry, context.Drivers.Find(entry.InstanceId)));
        }
        if (capped) limitations.Add("Inventário USB/periféricos limitado a 400 dispositivos.");
        return items;
    }

    internal static IEnumerable<PnpDeviceEntry> Ordered(IReadOnlyList<PnpDeviceEntry> devices) =>
        devices.OrderBy(device => device.Presence == DevicePresence.Present ? 0 : 1)
            .ThenBy(device => device.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Description, StringComparer.OrdinalIgnoreCase);
}

internal sealed class OtherDeviceCollector : HardwareCollectorBase
{
    public override string Id => "other-device";
    public override HardwareCategory Category => HardwareCategory.Other;
    public override string Source => "SetupAPI + CFGMGR32 (dispositivos PnP não classificados nas demais categorias)";
    protected override bool UsesWmi => false;

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        limitations.AddRange(context.Pnp.Limitations);
        limitations.AddRange(context.Drivers.Limitations);
        var items = new List<HardwareItem>();
        var capped = false;
        foreach (var entry in UsbPeripheralCollector.Ordered(context.Pnp.Devices))
        {
            if (!PnpScope.IsOther(entry)) continue;
            if (items.Count >= 250) { capped = true; break; }
            items.Add(PeripheralItems.Create(Category, "pnp-device", entry, context.Drivers.Find(entry.InstanceId)));
        }
        if (capped) limitations.Add("Inventário de outros dispositivos limitado a 250 itens.");
        return items;
    }
}

internal static class PnpScope
{
    private static readonly HashSet<string> AudioClasses = new(StringComparer.OrdinalIgnoreCase)
        { "MEDIA", "AudioEndpoint" };
    private static readonly HashSet<string> StorageClasses = new(StringComparer.OrdinalIgnoreCase)
        { "DiskDrive", "Volume", "VolumeSnapshot", "HDC", "SCSIAdapter", "StorageVolume" };
    private static readonly HashSet<string> CoreClasses = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Processor" };

    public static bool IsAudio(PnpDeviceEntry entry) =>
        AudioClasses.Contains(entry.ClassName ?? "") ||
        entry.InstanceId.StartsWith(@"SWD\MMDEVAPI", StringComparison.OrdinalIgnoreCase);

    public static bool IsStorage(PnpDeviceEntry entry) => StorageClasses.Contains(entry.ClassName ?? "");

    public static bool IsNetwork(PnpDeviceEntry entry) =>
        string.Equals(entry.ClassName, "Net", StringComparison.OrdinalIgnoreCase);

    public static bool IsDisplay(PnpDeviceEntry entry) =>
        string.Equals(entry.ClassName, "Display", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(entry.ClassName, "Monitor", StringComparison.OrdinalIgnoreCase);

    public static bool IsCore(PnpDeviceEntry entry) => CoreClasses.Contains(entry.ClassName ?? "");

    public static bool TryUsbKind(PnpDeviceEntry entry, out string kind)
    {
        kind = "";
        var className = entry.ClassName ?? "";
        var description = entry.Description ?? "";
        if (className.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            kind = description.Contains("Host Controller", StringComparison.OrdinalIgnoreCase) ? "usb-controller"
                : description.Contains("Hub", StringComparison.OrdinalIgnoreCase) ? "usb-hub" : "usb-controller";
            return true;
        }
        if (className.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) { kind = "keyboard"; return true; }
        if (className.Equals("Mouse", StringComparison.OrdinalIgnoreCase)) { kind = "mouse"; return true; }
        if (className.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Image", StringComparison.OrdinalIgnoreCase)) { kind = "webcam"; return true; }
        if (className.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)) { kind = "bluetooth"; return true; }
        if (className.Equals("HIDClass", StringComparison.OrdinalIgnoreCase)) { kind = "hid"; return true; }
        if (entry.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && !IsAudio(entry) &&
            !IsStorage(entry) && !IsNetwork(entry) && !IsDisplay(entry) && !IsCore(entry))
        {
            kind = "usb-device";
            return true;
        }
        return false;
    }

    public static bool IsOther(PnpDeviceEntry entry) => !IsAudio(entry) && !IsStorage(entry) && !IsNetwork(entry) &&
        !IsDisplay(entry) && !IsCore(entry) && !TryUsbKind(entry, out _);
}

internal static class PeripheralItems
{
    public static HardwareItem Create(HardwareCategory category, string kind, PnpDeviceEntry entry,
        DriverBinding? binding)
    {
        var virtualDevice = HardwareNormalizers.IsVirtual(entry.InstanceId, entry.Description);
        var itemLimitations = new List<string>();
        if (entry.Presence == DevicePresence.Disconnected)
            itemLimitations.Add("Dispositivo não presente; o driver associado não confirma uso atual.");
        if (entry.Presence == DevicePresence.Unknown)
            itemLimitations.Add("Presença do dispositivo não pôde ser confirmada.");
        if (entry.ClassName is null) itemLimitations.Add("Classe PnP não exposta pelo SetupAPI.");
        if (entry.ClassName is not null && binding is not null && binding.Version is null)
            itemLimitations.Add("Versão do driver não exposta.");
        var properties = new List<HardwareProperty>();
        if (!string.IsNullOrWhiteSpace(entry.ClassName)) properties.Add(new("pnp_class", entry.ClassName!));
        if (!string.IsNullOrWhiteSpace(entry.Service)) properties.Add(new("service", entry.Service!));
        if (binding?.Signed is { } signed) properties.Add(new("driver_signed_reported", signed ? "Sim" : "Não"));
        if (!string.IsNullOrWhiteSpace(binding?.Signer)) properties.Add(new("driver_signer", binding!.Signer!));
        return new HardwareItem(category, kind,
            HardwareNormalizers.StableId("pnp", entry.InstanceId),
            entry.Description ?? entry.ClassName ?? "Dispositivo PnP",
            entry.Manufacturer, null, HardwareNormalizers.HardwareId(entry.InstanceId),
            binding?.Provider, binding?.Version, binding?.DateUtc,
            binding?.DeviceClass, binding?.InfName, null,
            entry.Problem, HardwareNormalizers.Presence(entry, virtualDevice), properties, itemLimitations);
    }
}
