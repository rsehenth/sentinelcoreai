using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed class DriverCollector : HardwareCollectorBase
{
    public override string Id => "driver";
    public override HardwareCategory Category => HardwareCategory.Driver;
    public override string Source => @"WMI root\cimv2: Win32_PnPSignedDriver + SetupAPI (associação por DeviceID)";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        limitations.AddRange(context.Drivers.Limitations);
        var items = new List<HardwareItem>();
        var capped = false;
        foreach (var entry in UsbPeripheralCollector.Ordered(context.Pnp.Devices))
        {
            var binding = context.Drivers.Find(entry.InstanceId);
            if (binding is null) continue;
            if (items.Count >= 500) { capped = true; break; }
            var itemLimitations = new List<string>();
            if (entry.Presence == DevicePresence.Disconnected)
                itemLimitations.Add("Dispositivo não presente; o driver associado pode não estar em uso.");
            if (entry.Presence == DevicePresence.Unknown) itemLimitations.Add("Presença não confirmada.");
            if (binding.Version is null) itemLimitations.Add("Versão do driver não exposta pelo provedor.");
            if (binding.DateUtc is null) itemLimitations.Add("Data do driver não exposta.");
            if (binding.InfName is null) itemLimitations.Add("Nome do INF não exposto.");
            if (binding.Signed is null) itemLimitations.Add("Informação de assinatura não exposta.");
            var properties = new List<HardwareProperty>();
            Add(properties, "driver_device_name", binding.DeviceName);
            Add(properties, "driver_provider", binding.Provider);
            Add(properties, "driver_signed_reported", binding.Signed is null ? null : binding.Signed.Value ? "Sim" : "Não");
            Add(properties, "driver_signer", binding.Signer);
            Add(properties, "pnp_class", entry.ClassName);
            Add(properties, "presence", entry.Presence switch
            {
                DevicePresence.Present => "Presente",
                DevicePresence.Virtual => "Virtual",
                DevicePresence.Disconnected => "Desconectado",
                _ => "Indeterminado"
            });
            Add(properties, "device_manufacturer", entry.Manufacturer);
            items.Add(new HardwareItem(Category, "driver",
                HardwareNormalizers.StableId("driver", entry.InstanceId),
                entry.Description ?? binding.DeviceName ?? entry.ClassName ?? "Driver",
                entry.Manufacturer, null, HardwareNormalizers.HardwareId(entry.InstanceId),
                binding.Provider, binding.Version, binding.DateUtc, binding.DeviceClass, binding.InfName, null,
                entry.Problem,
                HardwareNormalizers.Presence(entry, HardwareNormalizers.IsVirtual(entry.InstanceId, entry.Description)),
                properties, itemLimitations));
        }
        if (capped) limitations.Add("Inventário de drivers limitado a 500 associações.");
        return items;
    }
}
