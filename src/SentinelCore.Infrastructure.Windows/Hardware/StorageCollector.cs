using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed class StorageCollector : HardwareCollectorBase
{
    public override string Id => "storage";
    public override HardwareCategory Category => HardwareCategory.Storage;
    public override string Source =>
        @"WMI root\Microsoft\Windows\Storage (MSFT_*) + root\cimv2 (Win32_DiskDrive/Partition/LogicalDisk)";
    private const int MaximumItems = 600;

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var storage = new WmiRead(CancellationToken, @"root\Microsoft\Windows\Storage");
        var win32Disks = wmi.Query(
            "SELECT DeviceID,Index,Model,Manufacturer,SerialNumber,PNPDeviceID,FirmwareRevision,Size,InterfaceType,MediaType,Status FROM Win32_DiskDrive",
            allowEmpty: true);
        var win32ByIndex = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var row in win32Disks)
            if (wmi.Int32Opt(row, "Index") is { } index) win32ByIndex[index] = row;
        var disks = storage.Query(
            "SELECT DeviceId,FriendlyName,Manufacturer,Model,SerialNumber,MediaType,BusType,Size,FirmwareVersion,HealthStatus,OperationalStatus,SpindleSpeed FROM MSFT_PhysicalDisk",
            allowEmpty: true);
        List<HardwareItem> items;
        if (disks.Count > 0)
            items = ReadAdvanced(storage, wmi, context, disks, win32ByIndex, limitations);
        else
        {
            limitations.Add("MSFT_PhysicalDisk não retornou discos; relação disco/partição/volume reconstruída apenas com APIs Win32.");
            items = ReadFallback(wmi, context, limitations);
        }
        limitations.AddRange(storage.Limitations.Distinct());
        if (items.Count > MaximumItems)
        {
            limitations.Add($"Inventário de armazenamento limitado a {MaximumItems} itens.");
            items = items[..MaximumItems];
        }
        return items;
    }

    private List<HardwareItem> ReadAdvanced(WmiRead storage, WmiRead wmi, HardwareContext context,
        List<Dictionary<string, object?>> disks, Dictionary<int, Dictionary<string, object?>> win32ByIndex,
        List<string> limitations)
    {
        var items = new List<HardwareItem>();
        var counters = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        var reliability = storage.Query(
            "SELECT DeviceId,Wear,Temperature,TemperatureMax,PowerOnHours,ReadErrorsTotal,WriteErrorsTotal,ReadErrorsUncorrected,WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter",
            allowEmpty: true);
        foreach (var row in reliability)
            if (storage.TextOpt(row, "DeviceId") is { } key) counters[key] = row;
        if (counters.Count == 0)
            limitations.Add("Contadores de confiabilidade não foram expostos para os discos desta coleta.");

        var diskStableByNumber = new Dictionary<uint, string>();
        var diskStableByIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in disks)
        {
            var diskNumber = storage.UInt32Opt(row, "DeviceId");
            var win32 = diskNumber is { } number && win32ByIndex.TryGetValue((int)number, out var found) ? found : null;
            var serial = CleanSerial(storage.TextOpt(row, "SerialNumber")) ?? CleanSerial(wmi.TextOpt(win32, "SerialNumber"));
            var model = Coalesce(storage.TextOpt(row, "Model"), wmi.TextOpt(win32, "Model"));
            var manufacturer = Coalesce(storage.TextOpt(row, "Manufacturer"), wmi.TextOpt(win32, "Manufacturer"));
            var size = storage.UInt64Opt(row, "Size") ?? wmi.UInt64Opt(win32, "Size");
            var firmware = Coalesce(storage.TextOpt(row, "FirmwareVersion"), wmi.TextOpt(win32, "FirmwareRevision"));
            var pnpId = wmi.TextOpt(win32, "PNPDeviceID");
            var identity = serial ?? pnpId ?? string.Join("|", model, manufacturer, firmware, size);
            var stableId = HardwareNormalizers.StableId("disk", identity);
            if (diskNumber is { } diskKey) diskStableByNumber[diskKey] = stableId;
            if (pnpId is not null) diskStableByIndex[pnpId] = stableId;
            var entry = context.Pnp.Find(pnpId);
            var binding = context.Drivers.Find(pnpId);
            var itemLimitations = new List<string>
            {
                "Saúde e contadores de confiabilidade são reportados pelo sistema; não representam garantia de integridade física."
            };
            if (serial is null && pnpId is null)
                itemLimitations.Add("Serial e identificador PnP indisponíveis; identidade do disco pode ser ambígua.");
            var properties = new List<HardwareProperty>();
            Add(properties, "capacity_bytes", HardwareNormalizers.Number(size));
            Add(properties, "media_type", HardwareNormalizers.MediaType(storage.UInt64Opt(row, "MediaType")));
            Add(properties, "bus_type", HardwareNormalizers.BusType(storage.UInt64Opt(row, "BusType")));
            Add(properties, "firmware_version", firmware);
            Add(properties, "health_reported", HardwareNormalizers.HealthStatus(storage.UInt64Opt(row, "HealthStatus")));
            Add(properties, "operational_status", HardwareNormalizers.OperationalStatus(
                NumberList(storage.UInt16ArrayOpt(row, "OperationalStatus"))));
            Add(properties, "spindle_rpm", SpindleSpeed(storage.UInt32Opt(row, "SpindleSpeed")));
            Add(properties, "disk_number", HardwareNormalizers.Number(diskNumber), true);
            if (diskNumber is { } counterKey && counters.TryGetValue(counterKey.ToString(CultureInfo.InvariantCulture), out var counter))
            {
                Add(properties, "reliability_wear", storage.TextOpt(counter, "Wear"), true);
                Add(properties, "reliability_temperature_c", storage.TextOpt(counter, "Temperature"), true);
                Add(properties, "reliability_temperature_max_c", storage.TextOpt(counter, "TemperatureMax"), true);
                Add(properties, "reliability_power_on_hours", storage.TextOpt(counter, "PowerOnHours"), true);
                Add(properties, "reliability_read_errors_total", storage.TextOpt(counter, "ReadErrorsTotal"), true);
                Add(properties, "reliability_write_errors_total", storage.TextOpt(counter, "WriteErrorsTotal"), true);
                Add(properties, "reliability_read_errors_uncorrected", storage.TextOpt(counter, "ReadErrorsUncorrected"), true);
                Add(properties, "reliability_write_errors_uncorrected", storage.TextOpt(counter, "WriteErrorsUncorrected"), true);
            }
            items.Add(new HardwareItem(Category, "disk", stableId,
                Coalesce(storage.TextOpt(row, "FriendlyName"), model) ?? "Disco físico", manufacturer, model,
                HardwareNormalizers.HardwareId(pnpId), binding?.Provider, binding?.Version,
                binding?.DateUtc, binding?.DeviceClass, binding?.InfName, firmware,
                entry is null ? null : entry.Problem ?? "OK",
                entry?.Presence ?? DevicePresence.Present, properties, itemLimitations));
        }

        ReadPartitionsAndVolumes(storage, diskStableByNumber, items, limitations);
        return items;
    }

    private void ReadPartitionsAndVolumes(WmiRead storage, Dictionary<uint, string> diskStableByNumber,
        List<HardwareItem> items, List<string> limitations)
    {
        var partitions = storage.Query(
            "SELECT DiskNumber,PartitionNumber,DriveLetter,Size,Offset,GptType,MbrType,IsBoot,IsSystem,IsActive,AccessPaths FROM MSFT_Partition",
            allowEmpty: true);
        var volumes = storage.Query(
            "SELECT DriveLetter,DriveType,FileSystem,Size,SizeRemaining,Path,HealthStatus,OperationalStatus FROM MSFT_Volume",
            allowEmpty: true);
        var volumeInfos = new List<(Dictionary<string, object?> Row, string StableId, string? Letter, string? Path)>();
        var volumeByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var volumeByLetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in volumes)
        {
            var driveType = storage.UInt32Opt(row, "DriveType");
            if (driveType is not (2 or 3)) continue;
            var path = NormalizeVolumePath(storage.TextOpt(row, "Path"));
            var letter = DriveLetter(storage.TextOpt(row, "DriveLetter"));
            var fileSystem = storage.TextOpt(row, "FileSystem");
            var size = storage.UInt64Opt(row, "Size");
            var identity = path ?? (letter is null ? $"{fileSystem}|{size}" : "letter:" + letter);
            var stableId = HardwareNormalizers.StableId("volume", identity);
            volumeInfos.Add((row, stableId, letter, path));
            if (path is not null) volumeByPath[path] = stableId;
            if (letter is not null) volumeByLetter[letter] = stableId;
        }
        var partitionByVolume = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skippedPartitions = 0;
        foreach (var row in partitions)
        {
            var diskNumber = storage.UInt32Opt(row, "DiskNumber");
            if (diskNumber is null || !diskStableByNumber.TryGetValue(diskNumber.Value, out var diskStable))
            {
                skippedPartitions++;
                continue;
            }
            var offset = storage.UInt64Opt(row, "Offset");
            var number = storage.UInt32Opt(row, "PartitionNumber");
            var identity = offset?.ToString(CultureInfo.InvariantCulture)
                           ?? (number?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
            var stableId = HardwareNormalizers.StableId("partition", diskStable + "|" + identity);
            var letter = DriveLetter(storage.TextOpt(row, "DriveLetter"));
            var volumeStable = letter is not null && volumeByLetter.TryGetValue(letter, out var byLetter)
                ? byLetter : MatchAccessPath(storage.ArrayTextOpt(row, "AccessPaths"), volumeByPath);
            if (volumeStable is not null) partitionByVolume[volumeStable] = stableId;
            var itemLimitations = new List<string>();
            if (offset is null) itemLimitations.Add("Deslocamento da partição indisponível; identidade baseada no número da partição.");
            if (volumeStable is null) itemLimitations.Add("Volume correspondente não identificado (partição sem letra ou volume não montado).");
            var properties = new List<HardwareProperty>();
            Add(properties, "size_bytes", HardwareNormalizers.Number(storage.UInt64Opt(row, "Size")));
            Add(properties, "offset_bytes", HardwareNormalizers.Number(offset));
            Add(properties, "partition_number", HardwareNormalizers.Number(number));
            Add(properties, "type", Coalesce(storage.TextOpt(row, "GptType"), storage.TextOpt(row, "MbrType")));
            AddFlag(properties, "is_boot", storage.BoolOpt(row, "IsBoot"));
            AddFlag(properties, "is_system", storage.BoolOpt(row, "IsSystem"));
            AddFlag(properties, "is_active", storage.BoolOpt(row, "IsActive"));
            Add(properties, "disk_stable_id", diskStable);
            Add(properties, "volume_stable_id", volumeStable);
            items.Add(new HardwareItem(Category, "partition", stableId,
                number is { } value ? $"Partição {value}" : "Partição", null, null, null, null, null, null, null,
                null, null, null, DevicePresence.Present, properties, itemLimitations));
        }
        if (skippedPartitions > 0)
            limitations.Add($"{skippedPartitions} partição(ões) de discos não inventariados foram ignoradas.");

        foreach (var (row, stableId, letter, path) in volumeInfos)
        {
            var itemLimitations = new List<string>();
            if (path is null) itemLimitations.Add("Caminho de volume indisponível; identidade pode mudar se a letra mudar.");
            var partitionStable = partitionByVolume.GetValueOrDefault(stableId);
            var diskStable = partitionStable is null
                ? null : items.FirstOrDefault(item => item.Kind == "partition" && item.StableId == partitionStable)?.Property("disk_stable_id");
            if (partitionStable is null) itemLimitations.Add("Partição associada não identificada nesta coleta.");
            var properties = new List<HardwareProperty>();
            Add(properties, "file_system", storage.TextOpt(row, "FileSystem"));
            Add(properties, "drive_type", VolumeDriveType(storage.UInt32Opt(row, "DriveType")));
            Add(properties, "drive_letter", letter);
            Add(properties, "size_bytes", HardwareNormalizers.Number(storage.UInt64Opt(row, "Size")));
            Add(properties, "free_bytes", HardwareNormalizers.Number(storage.UInt64Opt(row, "SizeRemaining")), true);
            Add(properties, "health_reported", HardwareNormalizers.HealthStatus(storage.UInt64Opt(row, "HealthStatus")));
            Add(properties, "operational_status", HardwareNormalizers.OperationalStatus(
                NumberList(storage.UInt16ArrayOpt(row, "OperationalStatus"))));
            Add(properties, "partition_stable_id", partitionStable);
            Add(properties, "disk_stable_id", diskStable);
            items.Add(new HardwareItem(Category, "volume", stableId,
                letter is null ? "Volume" : $"Volume {letter}", null, null, null, null, null, null, null, null,
                null, null, DevicePresence.Present, properties, itemLimitations));
        }
        if (partitions.Count > 0 && volumes.Count == 0)
            limitations.Add("MSFT_Volume não retornou volumes; espaço livre e associação a partições ficaram indisponíveis.");
    }

    private List<HardwareItem> ReadFallback(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var disks = wmi.Query(
            "SELECT DeviceID,Index,Model,Manufacturer,SerialNumber,PNPDeviceID,FirmwareRevision,Size,InterfaceType,MediaType,Status FROM Win32_DiskDrive",
            allowEmpty: true);
        var partitions = wmi.Query(
            "SELECT DeviceID,DiskIndex,Index,Size,StartingOffset,Type,Bootable FROM Win32_DiskPartition", allowEmpty: true);
        var logicalDisks = wmi.Query(
            "SELECT DeviceID,DriveType,FileSystem,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3", allowEmpty: true);
        var driveLinks = wmi.Query("SELECT Antecedent,Dependent FROM Win32_DiskDriveToDiskPartition", allowEmpty: true);
        var logicalLinks = wmi.Query("SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition", allowEmpty: true);
        var partitionsByDisk = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in driveLinks)
        {
            var disk = ReferenceId(row.GetValueOrDefault("Antecedent"));
            var partition = ReferenceId(row.GetValueOrDefault("Dependent"));
            if (disk is null || partition is null) continue;
            if (!partitionsByDisk.TryGetValue(disk, out var list)) partitionsByDisk[disk] = list = [];
            list.Add(partition);
        }
        var logicalByPartition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in logicalLinks)
        {
            var partition = ReferenceId(row.GetValueOrDefault("Antecedent"));
            var logical = ReferenceId(row.GetValueOrDefault("Dependent"));
            if (partition is not null && logical is not null) logicalByPartition[partition] = logical;
        }
        limitations.Add("Fallback Win32 lista apenas volumes fixos; indicadores de confiabilidade não estão disponíveis.");
        var items = new List<HardwareItem>();
        var diskStableByDeviceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in disks)
        {
            var deviceId = wmi.TextOpt(row, "DeviceID");
            var serial = CleanSerial(wmi.TextOpt(row, "SerialNumber"));
            var pnpId = wmi.TextOpt(row, "PNPDeviceID");
            var model = wmi.TextOpt(row, "Model");
            var manufacturer = wmi.TextOpt(row, "Manufacturer");
            var size = wmi.UInt64Opt(row, "Size");
            var firmware = wmi.TextOpt(row, "FirmwareRevision");
            var identity = serial ?? pnpId ?? string.Join("|", model, manufacturer, size);
            var stableId = HardwareNormalizers.StableId("disk", identity);
            if (deviceId is not null) diskStableByDeviceId[deviceId] = stableId;
            var entry = context.Pnp.Find(pnpId);
            var binding = context.Drivers.Find(pnpId);
            var properties = new List<HardwareProperty>();
            Add(properties, "capacity_bytes", HardwareNormalizers.Number(size));
            Add(properties, "interface_type", wmi.TextOpt(row, "InterfaceType"));
            Add(properties, "media_type", wmi.TextOpt(row, "MediaType"));
            Add(properties, "firmware_version", firmware);
            Add(properties, "status_reported", wmi.TextOpt(row, "Status"));
            Add(properties, "disk_number", HardwareNormalizers.Number(wmi.UInt32Opt(row, "Index")), true);
            var itemLimitations = new List<string>
            {
                "Estado reportado pelo Windows; não representa garantia de integridade física.",
                "Fallback Win32: indicadores de confiabilidade e saúde não disponíveis."
            };
            if (serial is null && pnpId is null)
                itemLimitations.Add("Serial e identificador PnP indisponíveis; identidade do disco pode ser ambígua.");
            items.Add(new HardwareItem(Category, "disk", stableId, model ?? "Disco físico", manufacturer, model,
                HardwareNormalizers.HardwareId(pnpId), binding?.Provider, binding?.Version, binding?.DateUtc,
                binding?.DeviceClass, binding?.InfName, firmware,
                entry is null ? null : entry.Problem ?? "OK", entry?.Presence ?? DevicePresence.Present,
                properties, itemLimitations));
        }
        var volumeByPartition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in logicalDisks)
        {
            var deviceId = wmi.TextOpt(row, "DeviceID");
            if (deviceId is null) continue;
            var stableId = HardwareNormalizers.StableId("volume", "fallback|" + deviceId);
            var properties = new List<HardwareProperty>();
            Add(properties, "file_system", wmi.TextOpt(row, "FileSystem"));
            Add(properties, "drive_type", VolumeDriveType(wmi.UInt32Opt(row, "DriveType")));
            Add(properties, "drive_letter", deviceId);
            Add(properties, "size_bytes", HardwareNormalizers.Number(wmi.UInt64Opt(row, "Size")));
            Add(properties, "free_bytes", HardwareNormalizers.Number(wmi.UInt64Opt(row, "FreeSpace")), true);
            var itemLimitations = new List<string>
            {
                "Fallback Win32: identidade do volume baseada na letra de unidade; pode mudar."
            };
            items.Add(new HardwareItem(Category, "volume", stableId, $"Volume {deviceId}", null, null, null, null,
                null, null, null, null, null, null, DevicePresence.Present, properties, itemLimitations));
            volumeByPartition[deviceId] = stableId;
        }
        foreach (var row in partitions)
        {
            var partitionDeviceId = wmi.TextOpt(row, "DeviceID");
            var diskIndex = wmi.Int32Opt(row, "DiskIndex");
            if (partitionDeviceId is null || diskIndex is null) continue;
            var diskDeviceId = disks.FirstOrDefault(disk => wmi.Int32Opt(disk, "Index") == diskIndex) is { } diskRow
                ? wmi.TextOpt(diskRow, "DeviceID") : null;
            var diskStable = diskDeviceId is null ? null : diskStableByDeviceId.GetValueOrDefault(diskDeviceId);
            if (diskStable is null) continue;
            var offset = wmi.UInt64Opt(row, "StartingOffset");
            var index = wmi.UInt32Opt(row, "Index");
            var identity = offset?.ToString(CultureInfo.InvariantCulture)
                           ?? (index?.ToString(CultureInfo.InvariantCulture) ?? partitionDeviceId);
            var stableId = HardwareNormalizers.StableId("partition", diskStable + "|" + identity);
            var volume = logicalByPartition.GetValueOrDefault(partitionDeviceId);
            var volumeStable = volume is null ? null : volumeByPartition.GetValueOrDefault(volume);
            var itemLimitations = new List<string>();
            if (volumeStable is null) itemLimitations.Add("Volume correspondente não identificado nesta coleta.");
            var properties = new List<HardwareProperty>();
            Add(properties, "size_bytes", HardwareNormalizers.Number(wmi.UInt64Opt(row, "Size")));
            Add(properties, "offset_bytes", HardwareNormalizers.Number(offset));
            Add(properties, "partition_number", HardwareNormalizers.Number(index));
            Add(properties, "type", wmi.TextOpt(row, "Type"));
            AddFlag(properties, "is_boot", wmi.BoolOpt(row, "Bootable"));
            Add(properties, "disk_stable_id", diskStable);
            Add(properties, "volume_stable_id", volumeStable);
            items.Add(new HardwareItem(Category, "partition", stableId,
                index is { } number ? $"Partição {number}" : "Partição", null, null, null, null, null, null, null,
                null, null, null, DevicePresence.Present, properties, itemLimitations));
        }
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.Kind != "volume" || item.Property("partition_stable_id") is not null) continue;
            var partition = items.FirstOrDefault(candidate => candidate.Kind == "partition" &&
                                                             candidate.Property("volume_stable_id") == item.StableId);
            if (partition is null) continue;
            var properties = item.Properties.Concat(
            [
                new HardwareProperty("partition_stable_id", partition.StableId),
                new HardwareProperty("disk_stable_id", partition.Property("disk_stable_id") ?? "")
            ]).Where(property => !string.IsNullOrWhiteSpace(property.Value)).ToArray();
            items[index] = item with { Properties = properties };
        }
        return items;
    }

    private static string? MatchAccessPath(string? accessPaths, Dictionary<string, string> volumeByPath)
    {
        if (string.IsNullOrWhiteSpace(accessPaths)) return null;
        foreach (var candidate in accessPaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (NormalizeVolumePath(candidate) is { } path && volumeByPath.TryGetValue(path, out var stable))
                return stable;
        return null;
    }

    internal static string? ReferenceId(object? value)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        const string marker = "DeviceID=\"";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = text.IndexOf('"', start);
        return end > start ? text[start..end] : null;
    }

    internal static string? CleanSerial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().ToUpperInvariant();
        if (text.Length > 128) return null;
        return text is "UNKNOWN" or "NONE" or "N/A" or "0" or "DEFAULT STRING" or "TO BE FILLED BY O.E.M." or
            "TO BE FILLED BY OEM" or "NOT APPLICABLE" ? null : text;
    }

    private static string? Coalesce(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second.Trim()) : first.Trim();

    // MSFT_PhysicalDisk devolve 0xFFFFFFFF quando a rotação é desconhecida e 0 para
    // mídia não rotacional; ambos significam indisponível, não "0 RPM".
    internal static string? SpindleSpeed(uint? value) =>
        value is null or 0 or uint.MaxValue ? null : HardwareNormalizers.Number(value);

    private static string? NormalizeVolumePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('\\').ToUpperInvariant();

    internal static string? DriveLetter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Trim('\0', ' ');
        if (text is "" or "0") return null;
        if (text.Length == 1 && char.IsLetter(text[0])) return char.ToUpperInvariant(text[0]) + ":";
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) &&
               code is >= 65 and <= 90 ? (char)code + ":" : null;
    }

    private static string? NumberList(ushort[]? values) =>
        values is null || values.Length == 0 ? null : string.Join(",", values);

    internal static string? VolumeDriveType(uint? value) => value switch
    {
        0 => "Desconhecido",
        1 => "Sem raiz",
        2 => "Removível",
        3 => "Fixo",
        4 => "Rede",
        5 => "CD-ROM",
        6 => "RAM",
        null => null,
        _ => $"Tipo {value}"
    };

    private static void AddFlag(List<HardwareProperty> properties, string name, bool? value)
    {
        if (value is not null) properties.Add(new(name, value.Value ? "Sim" : "Não"));
    }
}
