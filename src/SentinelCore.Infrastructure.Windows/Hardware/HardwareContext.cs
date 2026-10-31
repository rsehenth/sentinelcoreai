using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed record DriverBinding(string DeviceId, string? DeviceName, string? Version, DateTimeOffset? DateUtc,
    string? Provider, string? InfName, string? DeviceClass, bool? Signed, string? Signer);

// Win32_PnPSignedDriver describes drivers bound to PnP devices. Drivers installed in
// the driver store but not associated with a device are outside this phase.
internal sealed class DriverIndex
{
    private DriverIndex(IReadOnlyDictionary<string, DriverBinding> byDeviceId, IReadOnlyList<string> limitations)
    {
        ByDeviceId = byDeviceId;
        Limitations = limitations;
    }

    public IReadOnlyDictionary<string, DriverBinding> ByDeviceId { get; }
    public IReadOnlyList<string> Limitations { get; }

    public DriverBinding? Find(string? deviceId) =>
        deviceId is not null && ByDeviceId.TryGetValue(deviceId, out var binding) ? binding : null;

    public static DriverIndex Build(CancellationToken cancellationToken)
    {
        var wmi = new WmiRead(cancellationToken);
        var rows = wmi.Query("SELECT DeviceID,DeviceName,DriverVersion,DriverDate,Manufacturer,InfName,DeviceClass,IsSigned,Signer FROM Win32_PnPSignedDriver",
            allowEmpty: true);
        var bindings = new Dictionary<string, DriverBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var deviceId = wmi.TextOpt(row, "DeviceID");
            if (string.IsNullOrWhiteSpace(deviceId)) continue;
            bindings[deviceId] = new DriverBinding(deviceId, wmi.TextOpt(row, "DeviceName"),
                wmi.TextOpt(row, "DriverVersion"), wmi.DateOpt(row, "DriverDate"), wmi.TextOpt(row, "Manufacturer"),
                wmi.TextOpt(row, "InfName"), wmi.TextOpt(row, "DeviceClass"), wmi.BoolOpt(row, "IsSigned"),
                wmi.TextOpt(row, "Signer"));
        }
        return new(bindings, wmi.Limitations.Distinct().ToArray());
    }
}

internal sealed class HardwareContext(CancellationToken cancellationToken)
{
    private PnpDeviceIndex? pnp;
    private DriverIndex? drivers;

    public PnpDeviceIndex Pnp => pnp ??= PnpDeviceIndex.Build(cancellationToken);
    public DriverIndex Drivers => drivers ??= DriverIndex.Build(cancellationToken);

    public static HardwareContext Get(IHardwareCollectionSession session, CancellationToken cancellationToken) =>
        session.GetOrAdd("windows-hardware-context", () => new HardwareContext(cancellationToken));
}
