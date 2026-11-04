using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

/// <summary>
/// Relatório legível derivado do registro imutável. Não é fonte autoritativa e nunca reescreve o envelope.
/// Saídas sintéticas permanecem explicitamente marcadas.
/// </summary>
public static class RepairReport
{
    public static string RenderIntent(RepairIntent intent)
    {
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Intenção de reparo do Windows");
        text.AppendLine();
        if (intent.IsSynthetic)
            text.AppendLine("> **" + RepairGuidance.SyntheticDisclaimer + "**");
        text.AppendLine("> A intenção foi registrada antes do efeito, mas nenhum registro final foi encontrado. " +
            "A execução é considerada interrompida ou indeterminada.");
        text.AppendLine();
        text.AppendLine($"- Reparo: `{intent.Id:D}`");
        text.AppendLine($"- Plano: `{intent.PlanId:D}`");
        text.AppendLine($"- Operação: `{Safe(intent.OperationId)}` v{intent.OperationVersion}");
        text.AppendLine($"- Nome: {Safe(intent.DisplayName)}");
        text.AppendLine($"- Tipo: {intent.Kind}");
        text.AppendLine($"- Risco: {intent.Risk}");
        text.AppendLine($"- Modo: {intent.ExecutionMode}");
        text.AppendLine($"- Catálogo: `{Safe(intent.CatalogVersion)}` · hash `{Safe(intent.CatalogContentHash)}`");
        text.AppendLine($"- Volume: {(intent.Volume is null ? "não aplicável" : Safe(intent.Volume.Id))}");
        text.AppendLine($"- Comando oficial: `{Safe(intent.InvocationDisplay)}`");
        text.AppendLine($"- Autorização: `{intent.AuthorizationId:D}`");
        text.AppendLine($"- Criado UTC: {intent.CreatedAtUtc:O}");
        text.AppendLine();
        text.AppendLine("## Preflight");
        AppendPreflight(text, intent.Plan.Preflight);
        text.AppendLine();
        text.AppendLine("## Limitações");
        foreach (var limitation in intent.Limitations) text.AppendLine($"- {Safe(limitation)}");
        AppendMandatory(text);
        return text.ToString();
    }

    public static string Render(RepairRecord record)
    {
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Relatório de reparo do Windows");
        text.AppendLine();
        if (record.IsSynthetic)
            text.AppendLine("> **" + RepairGuidance.SyntheticDisclaimer + "**");
        text.AppendLine("> " + RepairGuidance.RealExecutionUnavailable);
        text.AppendLine();
        text.AppendLine($"- Reparo: `{record.Id:D}`");
        text.AppendLine($"- Plano: `{record.PlanId:D}`");
        text.AppendLine($"- Operação: `{Safe(record.OperationId)}` v{record.OperationVersion}");
        text.AppendLine($"- Nome: {Safe(record.DisplayName)}");
        text.AppendLine($"- Categoria: {Safe(record.Category)}");
        text.AppendLine($"- Tipo: {record.Kind}");
        text.AppendLine($"- Risco: {record.Risk}");
        text.AppendLine($"- Modo: {record.ExecutionMode}" + (record.IsSynthetic ? " (fixture sintética)" : string.Empty));
        text.AppendLine($"- Catálogo: `{Safe(record.CatalogVersion)}` · hash `{Safe(record.CatalogContentHash)}`");
        text.AppendLine($"- Volume: {(record.Volume is null ? "não aplicável" : Safe(record.Volume.Id))}");
        text.AppendLine($"- Comando oficial: `{Safe(record.InvocationDisplay)}`");
        text.AppendLine($"- Estado final: {record.State}");
        text.AppendLine($"- Resultado interpretado: {record.Result}");
        text.AppendLine($"- Confiança: {record.Confidence}");
        text.AppendLine($"- Código de saída: {(record.ExitCodeText is null ? "indisponível" : Safe(record.ExitCodeText))}");
        text.AppendLine($"- Sucesso verificado: {(record.VerifiedSuccess ? "sim" : "não")}");
        text.AppendLine($"- Reinicialização indicada: {(record.RequiresRestart ? "sim" : "não")}");
        text.AppendLine($"- Intervenção posterior: {(record.RequiresIntervention ? "sim" : "não")}");
        text.AppendLine($"- Início UTC: {record.StartedUtc:O}");
        text.AppendLine($"- Conclusão UTC: {record.CompletedUtc:O}");
        text.AppendLine($"- Duração: {record.Duration.TotalSeconds:F2} s");
        text.AppendLine($"- Autorização: `{record.AuthorizationId:D}`");
        text.AppendLine($"- Confirmação registrada: {Safe(record.ConfirmationId)}");
        text.AppendLine();
        text.AppendLine("## Resultado");
        text.AppendLine(Safe(record.ResultSummary));
        text.AppendLine();
        text.AppendLine("## Evidências interpretadas");
        text.AppendLine($"- Corrupção detectada: {Yes(record.Evidence.CorruptionDetected)}");
        text.AppendLine($"- Nenhuma corrupção detectada: {Yes(record.Evidence.NoCorruptionDetected)}");
        text.AppendLine($"- Reparação relatada como concluída: {Yes(record.Evidence.RepairCompleted)}");
        text.AppendLine($"- Reparação parcial: {Yes(record.Evidence.RepairPartial)}");
        text.AppendLine($"- Fonte de reparação indisponível: {Yes(record.Evidence.RepairSourceUnavailable)}");
        text.AppendLine($"- Acesso negado: {Yes(record.Evidence.AccessDenied)}");
        text.AppendLine($"- Reinicialização indicada: {Yes(record.Evidence.RestartRequired)}");
        text.AppendLine($"- WinRE habilitado: {(record.Evidence.WinReEnabled is null ? "não reconhecido" : Yes(record.Evidence.WinReEnabled.Value))}");
        text.AppendLine();
        text.AppendLine("## Saída registrada");
        if (record.Output.Count == 0)
        {
            text.AppendLine("Nenhuma saída registrada.");
        }
        else
        {
            text.AppendLine($"Linhas preservadas: {record.Output.Count} · bytes: {record.OutputBytes}" +
                (record.OutputTruncated ? " · limitada localmente" : string.Empty));
            text.AppendLine();
            foreach (var line in record.Output.OrderBy(item => item.Sequence))
                text.AppendLine("    " + SafeOutput(line.Text));
        }
        text.AppendLine();
        text.AppendLine("## Preflight");
        AppendPreflight(text, record.Preflight);
        text.AppendLine();
        text.AppendLine("## Passos da máquina de estados");
        foreach (var step in record.Steps.OrderBy(item => item.Sequence))
            text.AppendLine($"- {step.Sequence}. {step.State} — {step.AtUtc:O} — {Safe(step.Detail)}" +
                (step.ExitCode is null ? string.Empty : $" — código {step.ExitCode}"));
        text.AppendLine();
        text.AppendLine("## Alterações possíveis");
        foreach (var change in record.PossibleChanges) text.AppendLine($"- {Safe(change)}");
        text.AppendLine();
        text.AppendLine("## Ações adicionais");
        foreach (var action in record.AdditionalActions) text.AppendLine($"- {Safe(action)}");
        text.AppendLine();
        text.AppendLine("## Limitações");
        foreach (var limitation in record.Limitations.Distinct()) text.AppendLine($"- {Safe(limitation)}");
        AppendMandatory(text);
        return text.ToString();
    }

    private static void AppendPreflight(StringBuilder text, RepairPreflightResult preflight)
    {
        text.AppendLine($"- Simulação permitida: {(preflight.SimulationAllowed ? "sim" : "não")}");
        text.AppendLine($"- Execução real permitida nesta fase: {(preflight.RealExecutionAllowed ? "sim" : "não")}");
        text.AppendLine($"- Windows 11: {(preflight.Profile.IsWindows11 ? "sim" : "não")} · build {preflight.Profile.Build}");
        text.AppendLine($"- Elevado: {(preflight.Profile.IsElevated ? "sim" : "não")}");
        text.AppendLine($"- Executável validado: `{Safe(preflight.Tool.Path)}`" +
            (preflight.Tool.Available ? string.Empty : " (indisponível)"));
        text.AppendLine($"- Volume: {(preflight.Volume is null ? "não aplicável" : Safe(preflight.Volume.Id) + " · " + Safe(preflight.Volume.FileSystem))}");
        text.AppendLine($"- Hash do estado observado: `{Safe(preflight.ObservedStateHash)}`");
        if (preflight.Blockers.Count > 0)
            text.AppendLine("- Bloqueios de execução real: " + string.Join(", ", preflight.Blockers));
        if (preflight.SimulationBlockers.Count > 0)
            text.AppendLine("- Bloqueios de simulação: " + string.Join(", ", preflight.SimulationBlockers));
        if (preflight.Details.Count > 0)
        {
            text.AppendLine("- Detalhes:");
            foreach (var detail in preflight.Details) text.AppendLine($"  - {Safe(detail)}");
        }
    }

    private static void AppendMandatory(StringBuilder text)
    {
        text.AppendLine();
        text.AppendLine("## Escopo, segurança e privacidade");
        foreach (var notice in new[]
                 {
                     RepairGuidance.NoRollback,
                     RepairGuidance.NoAutomaticRepair,
                     RepairGuidance.ExitCodeLimitation,
                     RepairGuidance.OutputLocalization,
                     RepairGuidance.LocalOnly,
                     "A saída é limitada e pode conter caminhos ou nomes; o perfil do usuário é redigido antes da gravação.",
                     "A execução real do Windows Repair Center permanece desabilitada na distribuição nesta fase.",
                     "Este relatório não declara que o Windows foi reparado; ele registra apenas o resultado observado ou a fixture sintética."
                 })
            text.AppendLine("- " + notice);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "Indisponível"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'")
            .Replace("<", "&lt;").Replace(">", "&gt;");

    private static string SafeOutput(string? value) => string.IsNullOrEmpty(value) ? string.Empty
        : value.Replace("`", "'").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Yes(bool value) => value ? "sim" : "não";
}
