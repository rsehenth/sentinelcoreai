using System.Diagnostics;
using System.Globalization;
using System.Management;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

public static class WindowsCollectors
{
    public static IReadOnlyList<IDiagnosticCollector> CreateBasic() =>
    [
        new WmiCollector("windows", "WMI root\\cimv2: Win32_OperatingSystem", ReadWindows),
        new WmiCollector("processor", "WMI root\\cimv2: Win32_Processor", ReadProcessors),
        new WmiCollector("memory", "WMI root\\cimv2: Win32_PhysicalMemory + Win32_OperatingSystem.FreePhysicalMemory", ReadMemory),
        new WmiCollector("board-bios", "WMI root\\cimv2: Win32_BaseBoard + Win32_BIOS", ReadBoard),
        new WmiCollector("graphics", "WMI root\\cimv2: Win32_VideoController", ReadGraphics),
        new WmiCollector("storage", "WMI root\\cimv2: Win32_DiskDrive + Win32_LogicalDisk (DriveType=3)", ReadStorage)
    ];

    private static InventoryData ReadWindows(WmiRead context)
    {
        var os = context.Query("SELECT Caption,OperatingSystemSKU,Version,BuildNumber,OSArchitecture,LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        var name = context.Text(os, "Caption");
        var sku = context.UInt64(os, "OperatingSystemSKU");
        return new(OperatingSystem: new(name, sku.HasValue ? $"{name ?? "Windows"} (SKU {sku})" : null,
            context.Text(os, "Version"), context.Text(os, "BuildNumber"), context.Text(os, "OSArchitecture"), context.Date(os, "LastBootUpTime")));
    }

    private static InventoryData ReadProcessors(WmiRead context) => new(Processors:
        context.Query("SELECT Manufacturer,Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor")
            .Select(row => new ProcessorInfo(context.Text(row, "Manufacturer"), context.Text(row, "Name"),
                context.Int32(row, "NumberOfCores"), context.Int32(row, "NumberOfLogicalProcessors"))).ToArray());

    private static InventoryData ReadMemory(WmiRead context)
    {
        var modules = context.Query("SELECT Capacity FROM Win32_PhysicalMemory");
        var sizes = modules.Select(row => context.UInt64(row, "Capacity")).ToArray();
        ulong? installed = null;
        if (sizes.Length > 0 && sizes.All(value => value.HasValue))
        {
            ulong total = 0;
            foreach (var size in sizes) total = checked(total + size!.Value);
            installed = total;
        }
        var os = context.Query("SELECT FreePhysicalMemory FROM Win32_OperatingSystem").FirstOrDefault();
        return new(Memory: new(installed, Units.KiBToBytes(context.UInt64(os, "FreePhysicalMemory"))));
    }

    private static InventoryData ReadBoard(WmiRead context)
    {
        var boards = context.Query("SELECT Manufacturer,Product FROM Win32_BaseBoard");
        var firmwares = context.Query("SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS");
        if (boards.Count > 1 || firmwares.Count > 1) context.Limitations.Add("Inventário básico apresenta apenas a primeira placa-mãe e BIOS expostas pelo Windows.");
        var board = boards.FirstOrDefault(); var bios = firmwares.FirstOrDefault();
        return new(Board: new(context.Text(board, "Manufacturer"), context.Text(board, "Product"), context.Text(bios, "Manufacturer"),
            context.Text(bios, "SMBIOSBIOSVersion"), context.Date(bios, "ReleaseDate")));
    }

    private static InventoryData ReadGraphics(WmiRead context) => new(Gpus:
        context.Query("SELECT Name,AdapterCompatibility,DriverVersion FROM Win32_VideoController")
            .Select(row => new GpuInfo(context.Text(row, "Name"), context.Text(row, "AdapterCompatibility"), context.Text(row, "DriverVersion"))).ToArray());

    private static InventoryData ReadStorage(WmiRead context)
    {
        var disks = context.Query("SELECT DeviceID,Model,Size,InterfaceType,Status FROM Win32_DiskDrive")
            .Select(row => new DiskInfo(context.Text(row, "DeviceID"), context.Text(row, "Model"), context.UInt64(row, "Size"),
                context.Text(row, "InterfaceType"), context.Text(row, "Status"))).ToArray();
        var volumes = context.Query("SELECT DeviceID,FileSystem,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3")
            .Select(row => new VolumeInfo(context.Text(row, "DeviceID"), context.Text(row, "FileSystem"),
                context.UInt64(row, "Size"), context.UInt64(row, "FreeSpace"))).ToArray();
        return new(Disks: disks, Volumes: volumes);
    }
}

internal sealed class WmiCollector(string id, string source, Func<WmiRead, InventoryData> read) : IDiagnosticCollector
{
    public string Id => id;
    public string Version => "1.0.0";
    public string Source => source;
    public Task<CollectorResult> CollectAsync(Guid auditId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var started = DateTimeOffset.UtcNow; var timer = Stopwatch.StartNew(); var context = new WmiRead(cancellationToken);
        InventoryData? data = null;
        var status = DiagnosticStatus.Running;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            data = read(context);
            status = context.Limitations.Count == 0 ? DiagnosticStatus.Completed
                : context.SuccessfulQueries == 0 ? DiagnosticStatus.Failed : DiagnosticStatus.Partial;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = DiagnosticStatus.Cancelled; context.Limitations.Add("Coleta cancelada. Consultas WMI em andamento podem aguardar o timeout do provedor.");
        }
        catch (Exception error)
        {
            status = DiagnosticStatus.Failed; context.Limitations.Add($"Falha ({error.GetType().Name}): {error.Message}");
        }
        return new CollectorResult(auditId, Id, Version, started, DateTimeOffset.UtcNow, timer.Elapsed, status, Source, data,
            context.Limitations.Distinct().ToArray());
    });
}

internal sealed class WmiRead(CancellationToken cancellationToken, string scope = @"root\cimv2")
{
    public List<string> Limitations { get; } = [];
    public int SuccessfulQueries { get; private set; }
    public ManagementException? LastError { get; private set; }

    public List<Dictionary<string, object?>> Query(string query, bool allowEmpty = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(8), ReturnImmediately = false, Rewindable = false });
            using var collection = searcher.Get();
            foreach (ManagementObject instance in collection)
            {
                using (instance)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rows.Count >= 256) { Limitations.Add("Consulta limitada a 256 instâncias para manter a auditoria básica limitada."); break; }
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in instance.Properties) row[property.Name] = property.Value;
                    rows.Add(row);
                }
            }
            SuccessfulQueries++;
            if (rows.Count == 0 && !allowEmpty) Limitations.Add($"Nenhuma informação exposta por {query.Split("FROM ")[1]}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (ManagementException error)
        {
            LastError = error;
            Limitations.Add($"Consulta somente de leitura indisponível ({error.GetType().Name}): {error.Message}. Nenhuma elevação solicitada.");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Limitations.Add($"Consulta somente de leitura indisponível ({error.GetType().Name}): {error.Message}. Nenhuma elevação solicitada.");
        }
        return rows;
    }

    private object? Value(Dictionary<string, object?>? row, string field, bool required)
    {
        if (row is not null && row.TryGetValue(field, out var value) && value is not null && value != DBNull.Value) return value;
        if (required) Limitations.Add($"{field}: indisponível na fonte WMI.");
        return null;
    }
    public string? Text(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var text = Value(row, field, required)?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(text)) return text;
        if (required) Limitations.Add($"{field}: valor vazio ou indisponível.");
        return null;
    }
    public string? TextOpt(Dictionary<string, object?>? row, string field) => Text(row, field, required: false);
    public ulong? UInt64(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var value = Value(row, field, required);
        if (value is null) return null;
        if (ulong.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
        Limitations.Add($"{field}: valor numérico inválido."); return null;
    }
    public ulong? UInt64Opt(Dictionary<string, object?>? row, string field) => UInt64(row, field, required: false);
    public int? Int32(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var value = UInt64(row, field, required);
        if (!value.HasValue) return null;
        if (value <= int.MaxValue) return (int)value.Value;
        Limitations.Add($"{field}: valor fora da faixa suportada."); return null;
    }
    public int? Int32Opt(Dictionary<string, object?>? row, string field) => Int32(row, field, required: false);
    public uint? UInt32(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var value = UInt64(row, field, required);
        if (!value.HasValue) return null;
        if (value <= uint.MaxValue) return (uint)value.Value;
        Limitations.Add($"{field}: valor fora da faixa suportada."); return null;
    }
    public uint? UInt32Opt(Dictionary<string, object?>? row, string field) => UInt32(row, field, required: false);
    public bool? Bool(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var value = Value(row, field, required);
        if (value is null) return null;
        if (value is bool boolean) return boolean;
        var text = value.ToString()?.Trim();
        if (bool.TryParse(text, out var parsed)) return parsed;
        if (text is "1" or "0") return text == "1";
        Limitations.Add($"{field}: valor booleano inválido."); return null;
    }
    public bool? BoolOpt(Dictionary<string, object?>? row, string field) => Bool(row, field, required: false);
    public DateTimeOffset? Date(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var value = Text(row, field, required);
        if (value is null) return null;
        try { return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(value)).ToUniversalTime(); }
        catch (ArgumentException) { Limitations.Add($"{field}: data WMI inválida ou incompleta."); return null; }
    }
    public DateTimeOffset? DateOpt(Dictionary<string, object?>? row, string field) => Date(row, field, required: false);
    public string? ArrayText(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var value = Value(row, field, required);
        if (value is null) return null;
        if (value is Array array)
        {
            var parts = new List<string>();
            foreach (var item in array)
                if (item is not null && item != DBNull.Value)
                    parts.Add(Convert.ToString(item, CultureInfo.InvariantCulture) ?? "");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        var text = value.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    public string? ArrayTextOpt(Dictionary<string, object?>? row, string field) => ArrayText(row, field, required: false);
    public ushort[]? UInt16Array(Dictionary<string, object?>? row, string field, bool required = true)
    {
        var value = Value(row, field, required);
        if (value is null) return null;
        if (value is ushort[] typed) return typed;
        if (value is Array array)
        {
            var numbers = new List<ushort>();
            foreach (var item in array)
                if (item is not null && item != DBNull.Value && ushort.TryParse(item.ToString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var number))
                    numbers.Add(number);
            return numbers.Count == 0 ? null : numbers.ToArray();
        }
        return null;
    }
    public ushort[]? UInt16ArrayOpt(Dictionary<string, object?>? row, string field) => UInt16Array(row, field, required: false);
}
