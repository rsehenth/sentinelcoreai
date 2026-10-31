using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

public static class WindowsHardwareCollectors
{
    public static IReadOnlyList<IHardwareCollector> Create() =>
    [
        new CpuCollector(),
        new MemoryCollector(),
        new BoardCollector(),
        new GpuCollector(),
        new MonitorCollector(),
        new StorageCollector(),
        new NetworkCollector(),
        new AudioCollector(),
        new UsbPeripheralCollector(),
        new DriverCollector(),
        new OtherDeviceCollector()
    ];
}

internal abstract class HardwareCollectorBase : IHardwareCollector
{
    public abstract string Id { get; }
    public virtual string Version => "1.0.0";
    public abstract HardwareCategory Category { get; }
    public abstract string Source { get; }
    protected virtual string WmiScope => @"root\cimv2";
    protected virtual bool UsesWmi => true;
    protected CancellationToken CancellationToken { get; private set; }

    public Task<HardwareCollectorResult> CollectAsync(Guid snapshotId, IHardwareCollectionSession session,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var limitations = new List<string>();
        var items = new List<HardwareItem>();
        HardwareCollectionStatus status;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationToken = cancellationToken;
            var context = HardwareContext.Get(session, cancellationToken);
            var wmi = new WmiRead(cancellationToken, WmiScope);
            items = Collect(wmi, context, limitations);
            limitations.AddRange(wmi.Limitations);
            status = limitations.Count == 0 ? HardwareCollectionStatus.Completed
                : UsesWmi && wmi.SuccessfulQueries == 0 && items.Count == 0 ? HardwareCollectionStatus.Failed
                : HardwareCollectionStatus.Partial;
        }
        catch (NotSupportedException error)
        {
            status = HardwareCollectionStatus.Unsupported;
            limitations.Add($"Coleta não suportada neste sistema ({error.Message}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = HardwareCollectionStatus.Cancelled;
            limitations.Add("Coleta cancelada. Consultas em andamento podem aguardar o timeout do provedor.");
        }
        catch (Exception error)
        {
            status = HardwareCollectionStatus.Failed;
            limitations.Add($"Falha ({error.GetType().Name}): {error.Message}");
        }
        return new HardwareCollectorResult(Id, Version, Category, Source, started, DateTimeOffset.UtcNow,
            timer.Elapsed, status, items, limitations.Distinct().ToArray());
    });

    protected abstract List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations);

    protected static void Add(List<HardwareProperty> properties, string name, string? value, bool isVolatile = false)
    {
        if (!string.IsNullOrWhiteSpace(value)) properties.Add(new(name, value, isVolatile));
    }

    protected static string? DriverState(PnpDeviceEntry? entry, string? fallback = null)
    {
        if (entry?.Problem is { } problem) return problem;
        return fallback;
    }
}

internal sealed class CpuCollector : HardwareCollectorBase
{
    public override string Id => "cpu";
    public override HardwareCategory Category => HardwareCategory.Cpu;
    public override string Source => @"WMI root\cimv2: Win32_Processor";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var rows = wmi.Query("SELECT DeviceID,Name,Manufacturer,Architecture,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,CurrentClockSpeed,L2CacheSize,L3CacheSize,SocketDesignation,PNPDeviceID,Status FROM Win32_Processor");
        var items = new List<HardwareItem>();
        foreach (var row in rows)
        {
            var name = wmi.Text(row, "Name");
            var pnpId = wmi.TextOpt(row, "PNPDeviceID");
            var deviceId = wmi.TextOpt(row, "DeviceID");
            var identity = pnpId ?? deviceId ?? name ?? Guid.NewGuid().ToString("N");
            var itemLimitations = new List<string>();
            if (pnpId is null) itemLimitations.Add("Identidade da CPU baseada em DeviceID/nome; pode ser ambígua.");
            var cores = wmi.Int32Opt(row, "NumberOfCores");
            var logical = wmi.Int32Opt(row, "NumberOfLogicalProcessors");
            if (cores is null) itemLimitations.Add("Número de núcleos físicos indisponível.");
            if (logical is null) itemLimitations.Add("Número de processadores lógicos indisponível.");
            var entry = context.Pnp.Find(pnpId);
            var binding = context.Drivers.Find(pnpId);
            var properties = new List<HardwareProperty>();
            Add(properties, "architecture", HardwareNormalizers.CpuArchitecture(wmi.UInt64Opt(row, "Architecture")));
            Add(properties, "physical_cores", HardwareNormalizers.Number(cores));
            Add(properties, "logical_processors", HardwareNormalizers.Number(logical));
            Add(properties, "max_clock_mhz", HardwareNormalizers.Number(wmi.UInt32Opt(row, "MaxClockSpeed")));
            Add(properties, "current_clock_mhz", HardwareNormalizers.Number(wmi.UInt32Opt(row, "CurrentClockSpeed")), true);
            Add(properties, "l2_cache_kb", HardwareNormalizers.Number(wmi.UInt32Opt(row, "L2CacheSize")));
            Add(properties, "l3_cache_kb", HardwareNormalizers.Number(wmi.UInt32Opt(row, "L3CacheSize")));
            Add(properties, "socket", wmi.TextOpt(row, "SocketDesignation"));
            Add(properties, "device_id", deviceId);
            items.Add(new HardwareItem(Category, "cpu", HardwareNormalizers.StableId("cpu", identity), name,
                wmi.TextOpt(row, "Manufacturer"), name, HardwareNormalizers.HardwareId(pnpId),
                binding?.Provider, binding?.Version ?? wmi.TextOpt(row, "DriverVersion"),
                binding?.DateUtc, binding?.DeviceClass, binding?.InfName, null,
                DriverState(entry, wmi.TextOpt(row, "Status")), entry?.Presence ?? DevicePresence.Present,
                properties, itemLimitations));
        }
        return items;
    }
}

internal sealed class MemoryCollector : HardwareCollectorBase
{
    public override string Id => "memory";
    public override HardwareCategory Category => HardwareCategory.Memory;
    public override string Source => @"WMI root\cimv2: Win32_PhysicalMemory + Win32_PhysicalMemoryArray + Win32_ComputerSystem";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var modules = wmi.Query("SELECT Tag,BankLabel,DeviceLocator,Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber,SMBIOSMemoryType,FormFactor FROM Win32_PhysicalMemory");
        var array = wmi.Query("SELECT MemoryDevices,MaxCapacity,MaxCapacityEx FROM Win32_PhysicalMemoryArray", allowEmpty: true).FirstOrDefault();
        var computer = wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", allowEmpty: true).FirstOrDefault();
        ulong? total = null;
        var allCapacities = modules.Count > 0;
        ulong sum = 0;
        foreach (var row in modules)
        {
            var capacity = wmi.UInt64(row, "Capacity");
            if (capacity is null) { allCapacities = false; continue; }
            try { sum = checked(sum + capacity.Value); }
            catch (OverflowException) { allCapacities = false; limitations.Add("Soma das capacidades dos módulos excedeu o limite suportado."); }
        }
        if (allCapacities) total = sum;
        else
        {
            total = wmi.UInt64Opt(computer, "TotalPhysicalMemory");
            if (total is not null) limitations.Add("Capacidade instalada total veio de Win32_ComputerSystem porque a soma dos módulos estava incompleta.");
        }
        var slots = wmi.UInt32Opt(array, "MemoryDevices");
        var maxCapacityKiB = wmi.UInt64Opt(array, "MaxCapacityEx") ?? wmi.UInt64Opt(array, "MaxCapacity");
        ulong? maxCapacity = null;
        if (maxCapacityKiB is { } kib)
        {
            try { maxCapacity = checked(kib * 1024UL); }
            catch (OverflowException) { limitations.Add("Capacidade máxima de memória excedeu o limite suportado."); }
        }
        var items = new List<HardwareItem>();
        var summaryProperties = new List<HardwareProperty>();
        Add(summaryProperties, "installed_bytes", HardwareNormalizers.Number(total));
        Add(summaryProperties, "module_count", HardwareNormalizers.Number(modules.Count));
        Add(summaryProperties, "slot_count", HardwareNormalizers.Number(slots));
        Add(summaryProperties, "max_capacity_bytes", HardwareNormalizers.Number(maxCapacity));
        Add(summaryProperties, "available_bytes", HardwareNormalizers.Number(
            wmi.UInt64Opt(wmi.Query("SELECT FreePhysicalMemory FROM Win32_OperatingSystem", allowEmpty: true).FirstOrDefault(), "FreePhysicalMemory") is { } freeKiB
                ? SafeKiB(freeKiB) : null), true);
        items.Add(new HardwareItem(Category, "memory-summary", HardwareNormalizers.StableId("memory", "summary"),
            "Memória física", null, null, null, null, null, null, null, null, null, null, DevicePresence.Present,
            summaryProperties, []));
        for (var index = 0; index < modules.Count; index++)
        {
            var row = modules[index];
            var locator = wmi.TextOpt(row, "DeviceLocator");
            var bank = wmi.TextOpt(row, "BankLabel");
            var part = wmi.TextOpt(row, "PartNumber");
            var capacity = wmi.UInt64(row, "Capacity");
            var identity = string.Join("|", locator, bank, part, capacity);
            var itemLimitations = new List<string>();
            if (locator is null && bank is null) itemLimitations.Add("Slot do módulo não informado; identidade pode ser ambígua.");
            var properties = new List<HardwareProperty>();
            Add(properties, "capacity_bytes", HardwareNormalizers.Number(capacity));
            Add(properties, "speed_mhz", HardwareNormalizers.Number(wmi.UInt32Opt(row, "Speed")));
            Add(properties, "configured_speed_mhz", HardwareNormalizers.Number(wmi.UInt32Opt(row, "ConfiguredClockSpeed")));
            Add(properties, "memory_type", HardwareNormalizers.MemoryType(wmi.UInt64Opt(row, "SMBIOSMemoryType")));
            Add(properties, "form_factor", HardwareNormalizers.MemoryFormFactor(wmi.UInt64Opt(row, "FormFactor")));
            Add(properties, "slot", locator);
            Add(properties, "bank", bank);
            Add(properties, "part_number", part);
            items.Add(new HardwareItem(Category, "memory-module",
                HardwareNormalizers.StableId("memory-module", identity), part ?? locator ?? $"Módulo {index + 1}",
                wmi.TextOpt(row, "Manufacturer"), part, null, null, null, null, null, null, null, null,
                DevicePresence.Present, properties, itemLimitations));
        }
        return items;
    }

    private static ulong? SafeKiB(ulong? value)
    {
        if (value is null) return null;
        try { return checked(value.Value * 1024UL); }
        catch (OverflowException) { return null; }
    }
}

internal sealed class BoardCollector : HardwareCollectorBase
{
    public override string Id => "board-bios";
    public override HardwareCategory Category => HardwareCategory.Board;
    public override string Source => @"WMI root\cimv2: Win32_BaseBoard + Win32_BIOS + Win32_ComputerSystem";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var board = wmi.Query("SELECT Manufacturer,Product,Version FROM Win32_BaseBoard", allowEmpty: true).FirstOrDefault();
        var bios = wmi.Query("SELECT Manufacturer,Name,SMBIOSBIOSVersion,Version,ReleaseDate,SMBIOSMajorVersion,SMBIOSMinorVersion,Status FROM Win32_BIOS", allowEmpty: true).FirstOrDefault();
        var system = wmi.Query("SELECT Manufacturer,Model,SystemFamily FROM Win32_ComputerSystem", allowEmpty: true).FirstOrDefault();
        var items = new List<HardwareItem>();
        var boardManufacturer = wmi.TextOpt(board, "Manufacturer");
        var boardProduct = wmi.TextOpt(board, "Product");
        var boardVersion = wmi.TextOpt(board, "Version");
        var boardIdentity = string.Join("|", boardManufacturer, boardProduct, boardVersion);
        if (board is not null && (boardManufacturer is null || boardProduct is null))
            limitations.Add("Fabricante ou modelo da placa-mãe indisponível; identidade pode ser ambígua.");
        var boardProperties = new List<HardwareProperty>();
        Add(boardProperties, "board_version", boardVersion);
        items.Add(new HardwareItem(Category, "motherboard", HardwareNormalizers.StableId("board", boardIdentity),
            boardProduct ?? "Placa-mãe", boardManufacturer, boardProduct, null, null, null, null, null, null, null,
            null, DevicePresence.Present, boardProperties, []));
        var biosVersion = wmi.TextOpt(bios, "SMBIOSBIOSVersion") ?? wmi.TextOpt(bios, "Version");
        var firmwareDate = wmi.DateOpt(bios, "ReleaseDate");        var biosIdentity = boardIdentity + "|" + biosVersion + "|" + HardwareNormalizers.Date(firmwareDate);
        var smbiosMajor = wmi.UInt32Opt(bios, "SMBIOSMajorVersion");
        var smbiosMinor = wmi.UInt32Opt(bios, "SMBIOSMinorVersion");
        var biosProperties = new List<HardwareProperty>();
        Add(biosProperties, "bios_version_full", wmi.TextOpt(bios, "Version"));
        Add(biosProperties, "bios_name", wmi.TextOpt(bios, "Name"));
        Add(biosProperties, "firmware_date", HardwareNormalizers.Date(firmwareDate));
        if (smbiosMajor is not null && smbiosMinor is not null)
            Add(biosProperties, "smbios_version", $"{smbiosMajor}.{smbiosMinor}");
        items.Add(new HardwareItem(Category, "bios", HardwareNormalizers.StableId("bios", biosIdentity),
            wmi.TextOpt(bios, "Name") ?? "BIOS/UEFI", wmi.TextOpt(bios, "Manufacturer"), biosVersion, null, null, null,
            null, null, null, biosVersion,
            wmi.TextOpt(bios, "Status") is { } biosStatus ? $"Estado reportado: {biosStatus}" : null,
            DevicePresence.Present, biosProperties, []));
        var systemManufacturer = wmi.TextOpt(system, "Manufacturer");
        var systemModel = wmi.TextOpt(system, "Model");
        var systemFamily = wmi.TextOpt(system, "SystemFamily");
        items.Add(new HardwareItem(Category, "system",
            HardwareNormalizers.StableId("system", string.Join("|", systemManufacturer, systemModel, systemFamily)),
            string.Join(" ", new[] { systemManufacturer, systemModel }.Where(v => !string.IsNullOrWhiteSpace(v))),
            systemManufacturer, systemModel, null, null, null, null, null, null, null, null,
            DevicePresence.Present, [new("system_family", systemFamily ?? "")], []));
        return items;
    }
}

internal sealed class GpuCollector : HardwareCollectorBase
{
    public override string Id => "gpu";
    public override HardwareCategory Category => HardwareCategory.Gpu;
    public override string Source => @"WMI root\cimv2: Win32_VideoController + Win32_PnPSignedDriver";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var rows = wmi.Query("SELECT Name,AdapterCompatibility,DriverVersion,DriverDate,Status,PNPDeviceID,VideoProcessor,AdapterRAM,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,Availability,ConfigManagerErrorCode FROM Win32_VideoController");
        var items = new List<HardwareItem>();
        foreach (var row in rows)
        {
            var name = wmi.Text(row, "Name") ?? "Adaptador gráfico";
            var pnpId = wmi.TextOpt(row, "PNPDeviceID");
            var identity = pnpId ?? name;
            var itemLimitations = new List<string>();
            if (pnpId is null) itemLimitations.Add("Identidade da GPU baseada no nome; pode ser ambígua.");
            var isVirtual = HardwareNormalizers.IsVirtual(pnpId, name);
            var entry = context.Pnp.Find(pnpId);
            var binding = context.Drivers.Find(pnpId);
            var properties = new List<HardwareProperty>();
            Add(properties, "video_processor", wmi.TextOpt(row, "VideoProcessor"));
            var adapterRam = wmi.UInt64Opt(row, "AdapterRAM");
            Add(properties, "adapter_ram_wmi_bytes", HardwareNormalizers.Number(adapterRam));
            if (adapterRam is not null)
                itemLimitations.Add("AdapterRAM do WMI é o valor reportado pelo driver, sujeito a limite de 32 bits; não substitui a VRAM real do adaptador.");
            Add(properties, "current_width", HardwareNormalizers.Number(wmi.UInt32Opt(row, "CurrentHorizontalResolution")), true);
            Add(properties, "current_height", HardwareNormalizers.Number(wmi.UInt32Opt(row, "CurrentVerticalResolution")), true);
            Add(properties, "current_refresh_hz", HardwareNormalizers.Number(wmi.UInt32Opt(row, "CurrentRefreshRate")), true);
            Add(properties, "availability", wmi.UInt32Opt(row, "Availability")?.ToString());
            Add(properties, "config_manager_error_code", wmi.UInt32Opt(row, "ConfigManagerErrorCode")?.ToString());
            items.Add(new HardwareItem(Category, "gpu", HardwareNormalizers.StableId("gpu", identity), name,
                wmi.TextOpt(row, "AdapterCompatibility"), name, HardwareNormalizers.HardwareId(pnpId),
                binding?.Provider, binding?.Version ?? wmi.TextOpt(row, "DriverVersion"),
                binding?.DateUtc ?? wmi.DateOpt(row, "DriverDate"), binding?.DeviceClass, binding?.InfName, null,
                DriverState(entry, wmi.TextOpt(row, "Status")),
                HardwareNormalizers.Presence(entry, isVirtual), properties, itemLimitations));
        }
        return items;
    }
}
