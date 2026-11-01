using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed record EventClassification(EventCategory Category, string Title, string GroupId,
    EvidenceSeverity Severity);

public static class EventCatalog
{
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    public static EventClassification ClassifyEvent(RawEventRecord record)
    {
        var provider = record.Provider ?? string.Empty;
        var id = record.EventId;

        if (Has(provider, "dcom") && id == 10016)
            return Classification(EventCategory.Other, "other:dcom-10016", "Permissões DCOM negadas",
                EvidenceSeverity.Informational);
        if (Has(provider, "display") && id == 4101)
            return Classification(EventCategory.Graphics, "graphics:tdr", "Reinício do driver de vídeo (TDR)",
                EvidenceSeverity.ObservedImpact);
        if (Has(provider, "kernel-power") && id == 41)
            return Classification(EventCategory.Power, "power:unexpected-shutdown", "Encerramento inesperado",
                EvidenceSeverity.ObservedImpact);
        if (provider.Equals("EventLog", IgnoreCase) && id == 6008)
            return Classification(EventCategory.Power, "power:unexpected-shutdown", "Encerramento inesperado",
                EvidenceSeverity.ObservedImpact);
        if (Has(provider, "nvlddmkm") || Has(provider, "nvidia"))
            return Classification(EventCategory.Graphics, "graphics:nvidia", "Eventos do driver NVIDIA",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "amdkmdap") || Has(provider, "amdkmdag"))
            return Classification(EventCategory.Graphics, "graphics:amd", "Eventos do driver AMD",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "igfx") || Has(provider, "intel graphics"))
            return Classification(EventCategory.Graphics, "graphics:intel", "Eventos do driver Intel",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "display"))
            return Classification(EventCategory.Graphics, "graphics:other", "Outros eventos gráficos",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "kernel-power"))
            return Classification(EventCategory.Power, "power:other", "Outros eventos de energia",
                EvidenceSeverity.PotentialImpact);
        if ((Has(provider, "disk") || Has(provider, "storahci") || Has(provider, "stornvme") ||
             Has(provider, "storport")) && id is 7 or 11 or 51 or 153)
            return Classification(EventCategory.Storage, "storage:disk-io", "Erros de E/S de disco",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "ntfs") && id is 55 or 98 or 130)
            return Classification(EventCategory.Storage, "storage:file-system", "Erros de sistema de arquivos",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "volsnap") && id == 36)
            return Classification(EventCategory.Storage, "storage:shadow-copy", "Falhas de cópia de sombra",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "tcpip") && id is 4231 or 4266)
            return Classification(EventCategory.Network, "network:tcpip-resource",
                "Esgotamento de recursos de conexão TCP/IP", EvidenceSeverity.PotentialImpact);
        if (Has(provider, "tcpip"))
            return Classification(EventCategory.Network, "network:tcpip", "Erros de TCP/IP",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "application error") && id == 1000)
            return Classification(EventCategory.Applications, "applications:crash", "Falhas de aplicativos",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "application hang") && id == 1002)
            return Classification(EventCategory.Applications, "applications:hang", "Aplicativos sem resposta",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, ".net runtime") && id == 1026)
            return Classification(EventCategory.Applications, "applications:runtime", "Falhas de runtime .NET",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "service control manager"))
            return Classification(EventCategory.Services, "services:startup", "Falhas de serviço",
                EvidenceSeverity.PotentialImpact);
        if (Has(provider, "kernel-pnp"))
            return Classification(EventCategory.Other, "other:device", "Eventos de dispositivo",
                EvidenceSeverity.PotentialImpact);

        return Classification(EventCategory.Other, "other:unknown", "Outros eventos observados",
            EvidenceSeverity.Unknown);
    }

    public static EventClassification ClassifyReport(RawWerReport report)
    {
        var type = report.EventType ?? string.Empty;
        var code = report.ProblemCode;

        if (type.Equals("AppCrash", IgnoreCase))
            return Classification(EventCategory.Applications, "applications:crash", "Falhas de aplicativos",
                EvidenceSeverity.ObservedImpact);
        if (type.Equals("AppHang", IgnoreCase))
            return Classification(EventCategory.Applications, "applications:hang", "Aplicativos sem resposta",
                EvidenceSeverity.ObservedImpact);
        if (type.Equals("BlueScreen", IgnoreCase))
            return Classification(EventCategory.Power, "power:bugcheck", "Verificação de bug (tela azul)",
                EvidenceSeverity.ObservedImpact);
        if (GraphicsEvidenceClassification.IsLiveKernelType(type))
        {
            return GraphicsEvidenceClassification.IsLiveKernelKind(
                GraphicsEvidenceClassification.ClassifyWer(type, code))
                ? Classification(EventCategory.Graphics, "graphics:live-kernel",
                    "Eventos de kernel ao vivo (gráficos)", EvidenceSeverity.ObservedImpact)
                : Classification(EventCategory.Other, "other:device", "Eventos de dispositivo",
                    EvidenceSeverity.PotentialImpact);
        }
        if (type.StartsWith("Critical", IgnoreCase) || type.StartsWith("NonCritical", IgnoreCase))
            return Classification(EventCategory.Other, "other:wer", "Relatórios do Windows Error Reporting",
                EvidenceSeverity.Informational);

        return Classification(EventCategory.Other, "other:wer", "Relatórios do Windows Error Reporting",
            EvidenceSeverity.Unknown);
    }

    public static string CategoryName(EventCategory category) => category switch
    {
        EventCategory.Graphics => "Gráficos",
        EventCategory.Power => "Energia",
        EventCategory.Storage => "Armazenamento",
        EventCategory.Network => "Rede",
        EventCategory.Applications => "Aplicativos",
        EventCategory.Services => "Serviços",
        _ => "Outros"
    };

    public static string SeverityName(EvidenceSeverity severity) => severity switch
    {
        EvidenceSeverity.Unknown => "Impacto desconhecido",
        EvidenceSeverity.Informational => "Informativo",
        EvidenceSeverity.PotentialImpact => "Impacto potencial",
        EvidenceSeverity.ObservedImpact => "Impacto observado",
        _ => severity.ToString()
    };

    public static string ConfidenceName(EvidenceConfidence confidence) => confidence switch
    {
        EvidenceConfidence.Low => "Baixa",
        EvidenceConfidence.Medium => "Média",
        EvidenceConfidence.High => "Alta",
        _ => confidence.ToString()
    };

    public static string StatusName(EventInvestigationStatus status) => status switch
    {
        EventInvestigationStatus.Running => "Em andamento",
        EventInvestigationStatus.Completed => "Concluída",
        EventInvestigationStatus.Partial => "Parcial",
        EventInvestigationStatus.Failed => "Falhou",
        EventInvestigationStatus.Cancelled => "Cancelada",
        EventInvestigationStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    public static string SourceStatusName(EventSourceStatus status) => status switch
    {
        EventSourceStatus.Running => "Em andamento",
        EventSourceStatus.Completed => "Concluída",
        EventSourceStatus.Partial => "Parcial",
        EventSourceStatus.Failed => "Falhou",
        EventSourceStatus.Unsupported => "Não suportada",
        EventSourceStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static bool Has(string provider, string value) => provider.Contains(value, IgnoreCase);

    private static EventClassification Classification(EventCategory category, string groupId, string title,
        EvidenceSeverity severity) => new(category, title, groupId, severity);
}
