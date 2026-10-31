using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed class MonitorCollector : HardwareCollectorBase
{
    public override string Id => "monitor";
    public override HardwareCategory Category => HardwareCategory.Monitor;
    public override string Source => "DisplayConfig (QueryDisplayConfig) + WMI root\\wmi: WmiMonitorID";
    protected override string WmiScope => @"root\wmi";

    protected override List<HardwareItem> Collect(WmiRead wmi, HardwareContext context, List<string> limitations)
    {
        var display = DisplayConfigReader.Read(CancellationToken);
        limitations.AddRange(display.Limitations);
        var rows = wmi.Query("SELECT InstanceName,ManufacturerName,UserFriendlyName,ProductCodeID FROM WmiMonitorID",
            allowEmpty: true);
        var matched = new bool[rows.Count];
        var items = new List<HardwareItem>();
        foreach (var path in display.Paths)
        {
            var rowIndex = MatchRow(path, rows, wmi);
            var row = rowIndex >= 0 ? rows[rowIndex] : null;
            if (rowIndex >= 0) matched[rowIndex] = true;
            var wmiFriendly = HardwareNormalizers.JoinAscii(wmi.UInt16ArrayOpt(row, "UserFriendlyName"));
            var wmiManufacturer = HardwareNormalizers.JoinAscii(wmi.UInt16ArrayOpt(row, "ManufacturerName"));
            var friendly = Coalesce(path.FriendlyName, wmiFriendly) ?? "Monitor";
            var manufacturer = Coalesce(wmiManufacturer, path.ManufacturerCode) ?? path.ManufacturerCode;
            var productCode = Coalesce(HardwareNormalizers.JoinAscii(wmi.UInt16ArrayOpt(row, "ProductCodeID")),
                path.ProductCode == 0 ? null : path.ProductCode.ToString("X4"));
            var itemLimitations = new List<string>();
            string identity;
            if (!string.IsNullOrWhiteSpace(path.DevicePath)) identity = path.DevicePath;
            else if (wmi.TextOpt(row, "InstanceName") is { } instance) identity = instance;
            else
            {
                identity = $"{friendly}|{path.OutputTechnology}|{path.ConnectorInstance}|{path.Width}x{path.Height}";
                itemLimitations.Add("Identidade do monitor baseada em nome e conexão; pode ser ambígua.");
            }
            if (row is null)
                itemLimitations.Add("Fabricante e modelo não confirmados pelo WmiMonitorID.");
            if (ManufacturerMismatch(wmiManufacturer, path.ManufacturerCode))
                itemLimitations.Add(
                    $"Código de fabricante do EDID ({path.ManufacturerCode}) difere do WmiMonitorID ({wmiManufacturer}); marca não confirmada.");
            if (path.HdrSupported == true)
                itemLimitations.Add("Cor avançada/HDR reportada pelo sistema; não representa certificação do monitor.");
            if (path.SupportedModes.Count == 0)
                itemLimitations.Add("Modos suportados não puderam ser enumerados para esta saída.");
            var properties = new List<HardwareProperty>();
            Add(properties, "connector", HardwareNormalizers.ConnectionType(path.OutputTechnology));
            Add(properties, "connector_instance", HardwareNormalizers.Number(path.ConnectorInstance));
            Add(properties, "active_width", HardwareNormalizers.Number(path.Width));
            Add(properties, "active_height", HardwareNormalizers.Number(path.Height));
            Add(properties, "active_refresh_hz", path.RefreshHz);
            Add(properties, "hdr_supported", Flag(path.HdrSupported));
            Add(properties, "hdr_enabled", Flag(path.HdrEnabled));
            Add(properties, "supported_resolutions", path.SupportedModes.Count == 0
                ? null : string.Join("; ", path.SupportedModes));
            Add(properties, "supported_resolution_count", HardwareNormalizers.Number((uint)path.SupportedModes.Count));
            Add(properties, "edid_manufacturer_code", path.ManufacturerCode);
            Add(properties, "edid_product_code", productCode);
            Add(properties, "gdi_device", path.GdiDeviceName);
            items.Add(new HardwareItem(Category, "monitor",
                HardwareNormalizers.StableId("monitor", identity), friendly, manufacturer, friendly, productCode,
                null, null, null, null, null, null, null, DevicePresence.Present, properties, itemLimitations));
        }
        var unmatched = matched.Count(value => !value);
        if (unmatched > 0)
            limitations.Add($"{unmatched} monitor(es) enumerado(s) pelo WmiMonitorID sem caminho ativo correspondente; estado não determinado.");
        if (rows.Count == 0 && display.Paths.Count > 0)
            limitations.Add("WmiMonitorID não expôs fabricante/modelo; dados de exibição vieram apenas do DisplayConfig.");
        return items;
    }

    private static int MatchRow(DisplayPath path, List<Dictionary<string, object?>> rows, WmiRead wmi)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (InstanceMatches(path.DevicePath, wmi.TextOpt(rows[index], "InstanceName"))) return index;
        }
        return -1;
    }

    // WmiMonitorID usa "DISPLAY\\...\\<instância>_<saída>" enquanto o DevicePath do
    // DisplayConfig usa "\\?\DISPLAY#...#<instância>#{GUID}". A instância sem o sufixo
    // _<saída> é o prefixo comum.
    internal static bool InstanceMatches(string? devicePath, string? instanceName)
    {
        var path = NormalizePath(devicePath);
        var instance = NormalizePath(instanceName);
        if (path.Length == 0 || instance.Length == 0) return false;
        var underscore = instance.LastIndexOf('_');
        if (underscore > 0 && underscore < instance.Length - 1 &&
            int.TryParse(instance[(underscore + 1)..], out _)) instance = instance[..underscore];
        return path.StartsWith(instance, StringComparison.OrdinalIgnoreCase);
    }

    // O ManufacturerName do WmiMonitorID é decodificado do mesmo ID de fabricante do
    // EDID; divergência indica EDID regravado, painel substituído ou firmware
    // inconsistente. Registrar a ambiguidade em vez de escolher um dos valores.
    internal static bool ManufacturerMismatch(string? wmiManufacturer, string? edidCode) =>
        !string.IsNullOrWhiteSpace(wmiManufacturer) && !string.IsNullOrWhiteSpace(edidCode) &&
        !string.Equals(wmiManufacturer.Trim(), edidCode.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
        return text.Replace('#', '\\').TrimEnd('\\');
    }

    private static string? Coalesce(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second.Trim()) : first.Trim();

    private static string? Flag(bool? value) => value is null ? null : value.Value ? "Sim" : "Não";
}
