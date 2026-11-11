using SentinelCore.Domain;

namespace SentinelCore.App.Localization;

internal static class AuditScopeText
{
    public static string For(AuditKind kind) => kind == AuditKind.Full
        ? Loc.T("A auditoria completa inclui inventário, eventos e WER das últimas 24 h, 30 s de desempenho, ") +
          Loc.T("rede sem endereços, gráficos e uma sessão de 5 min de monitoramento local de eventos, CPU/memória, rede e gráficos. ") +
          Loc.T("Ao confirmar, você autoriza essa sessão. O monitoramento termina ao final da sessão ou ao cancelar.")
        : Loc.T("A auditoria básica lê informações do Windows, CPU, memória, placa-mãe, gráficos, discos e volumes.");
}
