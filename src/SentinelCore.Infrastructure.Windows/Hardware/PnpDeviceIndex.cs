using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed record PnpDeviceEntry(string InstanceId, string? ClassName, string? Description, string? Manufacturer,
    string? Service, DevicePresence Presence, string? Problem)
{
    public bool IsPresent => Presence == DevicePresence.Present;
}

// One SetupAPI pass per snapshot. Present and non-present devnodes are included so
// disconnected devices are not mistaken for removed hardware.
internal sealed class PnpDeviceIndex
{
    private const uint DigcfAllClasses = 0x0004;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpMfg = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpService = 0x00000004;
    private const uint ErrorNoMoreItems = 259;
    private const int CrSuccess = 0;
    private const int CrNoSuchDevinst = 13;
    private const int MaximumDevices = 4096;
    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(15);

    private PnpDeviceIndex(IReadOnlyList<PnpDeviceEntry> devices, IReadOnlyList<string> limitations)
    {
        Devices = devices;
        Limitations = limitations;
        ByInstanceId = devices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PnpDeviceEntry> Devices { get; }
    public IReadOnlyDictionary<string, PnpDeviceEntry> ByInstanceId { get; }
    public IReadOnlyList<string> Limitations { get; }

    public PnpDeviceEntry? Find(string? instanceId) =>
        instanceId is not null && ByInstanceId.TryGetValue(instanceId, out var entry) ? entry : null;

    public static PnpDeviceIndex Build(CancellationToken cancellationToken)
    {
        var devices = new List<PnpDeviceEntry>();
        var limitations = new List<string>();
        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfAllClasses);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            limitations.Add($"SetupAPI indisponível ({Marshal.GetLastWin32Error()}); presença e estado de dispositivos não puderam ser confirmados.");
            return new(devices, limitations);
        }
        var timer = Stopwatch.StartNew();
        try
        {
            var data = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= MaximumDevices)
                {
                    limitations.Add($"Enumeração PnP limitada a {MaximumDevices} dispositivos.");
                    break;
                }
                if (timer.Elapsed > EnumerationBudget)
                {
                    limitations.Add("Enumeração PnP interrompida após 15 s para não prolongar a coleta.");
                    break;
                }
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    if ((uint)Marshal.GetLastWin32Error() != ErrorNoMoreItems)
                        limitations.Add($"Enumeração PnP encerrada inesperadamente ({Marshal.GetLastWin32Error()}).");
                    break;
                }
                var instanceId = ReadInstanceId(set, ref data);
                if (string.IsNullOrWhiteSpace(instanceId)) continue;
                var presence = ReadPresence(data.DevInst, out var problem);
                devices.Add(new PnpDeviceEntry(instanceId, ReadProperty(set, ref data, SpdrpClass),
                    ReadProperty(set, ref data, SpdrpFriendlyName) ?? ReadProperty(set, ref data, SpdrpDeviceDesc),
                    ReadProperty(set, ref data, SpdrpMfg), ReadProperty(set, ref data, SpdrpService), presence, problem));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return new(devices, limitations);
    }

    private static string? ReadInstanceId(IntPtr set, ref SpDevInfoData data)
    {
        var buffer = new StringBuilder(1024);
        if (!SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Capacity, out var required))
        {
            if (required <= buffer.Capacity) return null;
            buffer = new StringBuilder((int)required);
            if (!SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Capacity, out _)) return null;
        }
        return buffer.ToString();
    }

    private static string? ReadProperty(IntPtr set, ref SpDevInfoData data, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out var required);
        if (required == 0) return null;
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size))
            return null;
        if (size < 2) return null;
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length))
            .Split('\0')[0].Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DevicePresence ReadPresence(uint devInst, out string? problem)
    {
        var result = CM_Get_DevNode_Status(out _, out var problemNumber, devInst, 0);
        return PresenceFromStatus(result, problemNumber, out problem);
    }

    // A devnode enumerada com sucesso existe na árvore atual; CM_Get_DevNode_Status
    // devolve CR_NO_SUCH_DEVINST para dispositivos não presentes. O status DN_* não
    // possui bit de presença (0x1 é DN_ROOT_ENUMERATED).
    internal static DevicePresence PresenceFromStatus(int result, uint problemNumber, out string? problem)
    {
        problem = null;
        if (result == CrNoSuchDevinst) return DevicePresence.Disconnected;
        if (result != CrSuccess)
        {
            problem = $"CM_Get_DevNode_Status falhou ({result})";
            return DevicePresence.Unknown;
        }
        if (problemNumber != 0) problem = ProblemName(problemNumber);
        return DevicePresence.Present;
    }

    internal static string ProblemName(uint code) => code switch
    {
        0 => "OK",
        22 => "Desativado (CM_PROB_DISABLED)",
        24 => "Não presente (CM_PROB_DEVICE_NOT_THERE)",
        28 => "Driver não instalado (CM_PROB_FAILED_INSTALL)",
        _ => $"Problema de dispositivo {code}"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint memberIndex, ref SpDevInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevInfoData data, StringBuilder buffer,
        int bufferSize, out int requiredSize);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SpDevInfoData data, uint property,
        out uint propertyType, [Out] byte[]? buffer, uint bufferSize, out uint requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
}
