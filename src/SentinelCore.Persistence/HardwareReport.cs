using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public static class HardwareReport
{
    public static string Render(HardwareSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Inventário de hardware");
        text.AppendLine($"\n- Versão: {snapshot.SoftwareVersion}\n- Formato: {snapshot.FormatVersion}\n- Esquema: {snapshot.SchemaVersion}" +
            $"\n- ID: {snapshot.Id:D}\n- Status: {snapshot.Status}");
        text.AppendLine($"- Início UTC: {snapshot.StartedUtc:O}\n- Conclusão UTC: {snapshot.CompletedUtc:O}" +
            $"\n- Horário local: {snapshot.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" +
            $"\n- Duração: {snapshot.Duration.TotalSeconds:F2} s\n- Coletores: {snapshot.Results.Count}");
        text.AppendLine("\n## Escopo e interpretação\n\nInventário somente de leitura obtido por interfaces locais do Windows. " +
            "Não altera configurações, drivers ou firmware. Estados OK/Healthy são informações reportadas, " +
            "não garantia de integridade. Identificadores são hashes locais de identidades técnicas; " +
            "dados indisponíveis permanecem nulos. A ausência de um item não é conclusiva quando a categoria foi parcial ou falhou. " +
            "Nenhum diagnóstico, reparo ou correlação causal foi executado.");
        foreach (var result in snapshot.Results)
        {
            text.AppendLine($"\n## {CategoryName(result.Category)} — {result.Status}\n");
            text.AppendLine($"Fonte: {Safe(result.Source)}  \nColetor: {Safe(result.CollectorVersion)}  " +
                $"\nInício UTC: {result.StartedUtc:O}  \nConclusão UTC: {result.CompletedUtc:O}  " +
                $"\nDuração: {result.Duration.TotalSeconds:F2} s\n");
            if (result.Items.Count == 0) text.AppendLine("- Nenhum item observado nesta categoria.");
            foreach (var item in result.Items)
            {
                text.AppendLine($"\n### {Safe(item.Name ?? item.Kind)} ({Safe(item.Kind)})");
                text.AppendLine($"- Identidade técnica (hash local): {Safe(item.StableId)}");
                if (!string.IsNullOrWhiteSpace(item.Manufacturer)) text.AppendLine($"- Fabricante: {Safe(item.Manufacturer)}");
                if (!string.IsNullOrWhiteSpace(item.Model)) text.AppendLine($"- Modelo: {Safe(item.Model)}");
                if (!string.IsNullOrWhiteSpace(item.HardwareId)) text.AppendLine($"- Hardware ID: {Safe(item.HardwareId)}");
                if (!string.IsNullOrWhiteSpace(item.FirmwareVersion)) text.AppendLine($"- Firmware: {Safe(item.FirmwareVersion)}");
                text.AppendLine($"- Presença: {item.Presence}");
                text.AppendLine($"- Estado reportado pelo Windows: {Safe(item.WindowsState)}");
                if (!string.IsNullOrWhiteSpace(item.DriverProvider) || !string.IsNullOrWhiteSpace(item.DriverVersion))
                    text.AppendLine($"- Driver: {Safe(item.DriverProvider)} {Safe(item.DriverVersion)}" +
                        (item.DriverDateUtc is { } date ? $" ({date.ToUniversalTime():yyyy-MM-dd})" : ""));
                if (!string.IsNullOrWhiteSpace(item.DriverClass)) text.AppendLine($"- Classe do driver: {Safe(item.DriverClass)}");
                if (!string.IsNullOrWhiteSpace(item.DriverInfName)) text.AppendLine($"- INF: {Safe(item.DriverInfName)}");
                foreach (var property in item.Properties)
                    text.AppendLine($"- {Safe(property.Name)}: {Safe(property.Value)}{(property.Volatile ? " (volátil, não comparado)" : "")}");
                foreach (var limitation in item.Limitations) text.AppendLine($"- Limitação: {Safe(limitation)}");
            }
            if (result.Limitations.Count > 0)
            {
                text.AppendLine("\n### Limitações do coletor\n");
                foreach (var limitation in result.Limitations) text.AppendLine($"- {Safe(limitation)}");
            }
        }
        return text.ToString();
    }

    public static string CategoryName(HardwareCategory category) => category switch
    {
        HardwareCategory.Cpu => "Processador",
        HardwareCategory.Memory => "Memória",
        HardwareCategory.Board => "Placa-mãe e BIOS",
        HardwareCategory.Gpu => "Placa de vídeo",
        HardwareCategory.Monitor => "Monitores",
        HardwareCategory.Storage => "Armazenamento",
        HardwareCategory.Network => "Rede",
        HardwareCategory.Audio => "Áudio",
        HardwareCategory.Usb => "USB e periféricos",
        HardwareCategory.Driver => "Drivers",
        HardwareCategory.Other => "Outros dispositivos",
        _ => category.ToString()
    };

    public static string StatusName(HardwareCollectionStatus status) => status switch
    {
        HardwareCollectionStatus.Running => "Em andamento",
        HardwareCollectionStatus.Completed => "Concluída",
        HardwareCollectionStatus.Partial => "Parcial",
        HardwareCollectionStatus.Failed => "Falhou",
        HardwareCollectionStatus.Cancelled => "Cancelada",
        HardwareCollectionStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "Indisponível"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Replace("<", "&lt;").Replace(">", "&gt;");
}
