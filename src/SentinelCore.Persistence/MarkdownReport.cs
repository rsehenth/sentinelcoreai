using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public static class MarkdownReport
{
    public static string Render(AuditRecord audit)
    {
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Relatório de auditoria");
        text.AppendLine($"\n- Versão: {audit.SoftwareVersion}\n- Formato: {audit.FormatVersion}\n- ID: {audit.Id:D}\n- Tipo: {audit.Kind}\n- Status: {audit.Status}");
        text.AppendLine($"- Início UTC: {audit.StartedUtc:O}\n- Conclusão UTC: {audit.CompletedUtc:O}\n- Horário local: {audit.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\n- Duração: {audit.Duration.TotalSeconds:F2} s");
        text.AppendLine("\n## Escopo e interpretação\n\nInventário observado pelas interfaces genéricas do Windows, sem testes de estresse ou reparos. " +
            "Esta auditoria não avalia integralmente estabilidade, drivers ou integridade do hardware. " +
            "Estados OK/Healthy são informações reportadas, não garantia de integridade. " +
            "Memória disponível e espaço livre representam apenas o instante da coleta. " +
            "Indisponível não significa ausência de problema. Nenhum diagnóstico avançado foi realizado.");
        foreach (var result in audit.Results)
        {
            text.AppendLine($"\n## {Safe(result.CollectorId)} — {result.Status}\n");
            text.AppendLine($"Fonte: {Safe(result.Source)}  \nColetor: {Safe(result.CollectorVersion)}  \nInício UTC: {result.StartedUtc:O}  \nConclusão UTC: {result.CompletedUtc:O}  \nDuração: {result.Duration.TotalSeconds:F2} s\n");
            foreach (var (label, value) in Fields(result.Data)) text.AppendLine($"- {label}: {Safe(value)}");
            if (result.Data is null) text.AppendLine("- Dados: Indisponível");
            if (result.Limitations.Count > 0)
            {
                text.AppendLine("\n### Dados indisponíveis e limitações\n");
                foreach (var limitation in result.Limitations) text.AppendLine($"- {Safe(limitation)}");
            }
            else text.AppendLine("\nNenhuma indisponibilidade registrada pelo coletor. As limitações de escopo acima continuam aplicáveis.");
        }
        return text.ToString();
    }

    public static IEnumerable<(string Label, string? Value)> Fields(InventoryData? data)
    {
        if (data?.OperatingSystem is { } os)
        {
            yield return ("Windows", os.Name); yield return ("Edição", os.Edition); yield return ("Versão", os.Version);
            yield return ("Build", os.Build); yield return ("Arquitetura", os.Architecture);
            yield return ("Última inicialização UTC", os.LastBootUtc?.ToString("O"));
        }
        if (data?.Processors is { } processors)
            foreach (var cpu in processors)
            {
                yield return ("Fabricante da CPU", cpu.Manufacturer); yield return ("Modelo da CPU", cpu.Model);
                yield return ("Núcleos físicos", cpu.PhysicalCores?.ToString()); yield return ("Processadores lógicos", cpu.LogicalProcessors?.ToString());
            }
        if (data?.Memory is { } memory)
        {
            yield return ("Memória física instalada", Units.GiB(memory.InstalledBytes));
            yield return ("Memória física disponível na coleta", Units.GiB(memory.AvailableBytes));
        }
        if (data?.Board is { } board)
        {
            yield return ("Fabricante da placa-mãe", board.Manufacturer); yield return ("Modelo da placa-mãe", board.Model);
            yield return ("Fabricante da BIOS", board.BiosManufacturer); yield return ("Versão da BIOS/UEFI", board.BiosVersion);
            yield return ("Data informada pelo firmware UTC", board.FirmwareDateUtc?.ToString("O"));
        }
        if (data?.Gpus is { } gpus)
            foreach (var gpu in gpus)
            {
                yield return ("GPU", gpu.Name); yield return ("Fabricante da GPU", gpu.Manufacturer); yield return ("Driver da GPU", gpu.DriverVersion);
            }
        if (data?.Disks is { } disks)
            foreach (var disk in disks)
            {
                yield return ("Disco físico", disk.Device); yield return ("Modelo do disco", disk.Model);
                yield return ("Capacidade do disco", Units.GiB(disk.CapacityBytes)); yield return ("Interface reportada", disk.Interface);
                yield return ("Estado reportado pelo Windows", disk.ReportedStatus);
            }
        if (data?.Volumes is { } volumes)
            foreach (var volume in volumes)
            {
                yield return ("Volume", volume.Device); yield return ("Sistema de arquivos", volume.FileSystem);
                yield return ("Capacidade do volume", Units.GiB(volume.CapacityBytes)); yield return ("Espaço livre na coleta", Units.GiB(volume.FreeBytes));
            }
    }
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "Indisponível"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Replace("<", "&lt;").Replace(">", "&gt;");
}
