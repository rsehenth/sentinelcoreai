using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public static class RecoveryCenterPresentation
{
    public static string Label(RecoveryModule module) => module switch
    {
        RecoveryModule.Tweaks => "Tweaks T2A/T2B", RecoveryModule.Debloat => "Debloat T3",
        RecoveryModule.ControlledRemoval => "Remoção controlada T3C", RecoveryModule.WindowsRepair => "Reparos do Windows R1",
        _ => "Módulo desconhecido"
    };
    public static string Label(RecoveryOperationOutcome outcome) => outcome switch
    {
        RecoveryOperationOutcome.Completed => "Concluída com evidência", RecoveryOperationOutcome.Failed => "Falha",
        RecoveryOperationOutcome.Indeterminate => "Indeterminada", RecoveryOperationOutcome.Blocked => "Bloqueada",
        RecoveryOperationOutcome.Cancelled => "Cancelada", RecoveryOperationOutcome.NotStarted => "Sem início registrado",
        _ => "Resultado desconhecido"
    };
    public static string Label(RecoveryOrigin origin) => origin switch
    {
        RecoveryOrigin.Synthetic => "SINTÉTICO — fixture", RecoveryOrigin.Real => "Real — evidência histórica",
        _ => "Origem não registrada"
    };
    public static string Label(RecoveryCapability capability) => capability switch
    {
        RecoveryCapability.PotentiallyAvailable => "Recuperação potencialmente disponível",
        RecoveryCapability.Blocked => "Recuperação bloqueada", RecoveryCapability.Unsupported => "Recuperação não suportada",
        RecoveryCapability.InterventionRequired => "Intervenção necessária", _ => "Estado indeterminado"
    };
    public static string Label(RiskLevel risk) => risk switch
    { RiskLevel.Low => "Baixo", RiskLevel.Medium => "Médio", RiskLevel.High => "Alto", _ => "Não validado" };

    public static string Summary(RecoveryCenterSnapshot snapshot)
    {
        var s = RecoveryCenter.Summarize(snapshot);
        var text = new StringBuilder(s.IsComplete ? "Históricos carregados. " : "HISTÓRICO INCOMPLETO — quantidades apenas dos registros legíveis; totais indisponíveis. ");
        text.Append($"{s.Registered} registradas; {s.Completed} concluídas; {s.Failed} com falha; {s.Indeterminate} indeterminadas; {s.Conflicts} com conflitos; {s.Intervention} requerem intervenção. ");
        text.Append($"Recuperações: {s.RecoveriesCompleted} integrais; {s.RecoveriesPartial} parciais; {s.RecoveriesBlocked} bloqueadas; {s.RecoveriesFailed} falhas/interrompidas.");
        return text.ToString();
    }

    public static string Details(RecoveryOperation o)
    {
        var b = new StringBuilder();
        b.AppendLine("IDENTIFICAÇÃO");
        b.AppendLine($"{Label(o.Module)} | {o.OperationId} | {o.Id:D}");
        b.AppendLine($"Data local: {o.AtUtc.ToLocalTime():g}; tipo: {o.Type}");
        b.AppendLine($"Alvo: {o.Target}");
        b.AppendLine($"Origem: {Label(o.Origin)}; risco: {(o.Risk is { } risk ? Label(risk) : "não registrado")}");
        b.AppendLine().AppendLine("ESTADO E EVIDÊNCIAS");
        b.AppendLine($"{o.State} | {Label(o.Outcome)}");
        b.AppendLine($"{o.Verification}; efeito possivelmente iniciado: {YesNo(o.EffectMayHaveOccurred)}");
        b.AppendLine($"Conflito registrado: {YesNo(o.HasConflict)}; intervenção: {YesNo(o.RequiresIntervention)}; bloqueio persistente T3C: {YesNo(o.TargetPersistentlyBlocked)}");
        b.AppendLine().AppendLine("CONSEQUÊNCIAS");
        Lines(b, o.Consequences);
        b.AppendLine().AppendLine("PROTEÇÕES");
        b.AppendLine("Autorização: " + o.Authorization);
        b.AppendLine("Backup: " + o.Backup);
        if (o.Transaction?.Plan is { } plan)
        {
            b.AppendLine($"Plano: {plan.PlanHash}; catálogo {plan.CatalogVersion}; versão {plan.OperationVersion}");
            foreach (var change in plan.Changes)
            {
                b.AppendLine($"{change.TargetIdentity}: anterior (alvo existe={change.BeforeTargetExists}): {VerificationEngine.Describe(change.Before)}");
                b.AppendLine($"Pretendido (alvo existe={change.AfterTargetExists}): {VerificationEngine.Describe(change.After)}");
            }
            foreach (var step in o.Transaction.Steps)
                b.AppendLine($"Pós-estado registrado {step.TargetIdentity}: aplicado={step.Applied}; verificado={step.Verified}; {step.Error ?? string.Join("; ", step.Effects)}");
            b.AppendLine("O contrato StepResult registra aplicação e verificação, sem persistir o conteúdo relido. Estado atual não é presumido a partir do pretendido ou da verificação histórica.");
        }
        b.AppendLine().AppendLine("RECUPERAÇÃO");
        b.AppendLine(Label(o.Capability));
        if (o.Preview is { } preview)
        {
            b.AppendLine($"Prévia exclusivamente sintética: {preview.Outcome}; {preview.Reason}");
            foreach (var c in preview.Conflicts)
                b.AppendLine($"Conflito {c.TargetIdentity}: {c.Kind}; {c.Description}; atual={Describe(c.Current)}, anterior={Describe(c.Observed)}, pós-estado esperado={Describe(c.ExpectedPost)}");
        }
        foreach (var r in o.Recoveries.OrderBy(r => r.StartedAtUtc))
        {
            b.AppendLine($"{r.StartedAtUtc.ToLocalTime():g} | {r.RecoveryId:D} | {r.Outcome}; integral={r.FullyRecovered}; efeito possível={r.EffectMayHaveOccurred}; {r.Error}");
            foreach (var c in r.Conflicts) b.AppendLine($"Conflito: {c.TargetIdentity}; {c.Kind}; {c.Description}");
            foreach (var step in r.Steps) b.AppendLine($"Alvo {step.TargetIdentity}: aplicado={step.Applied}; verificado={step.Verified}; {step.Error}");
        }
        if (o.Recoveries.Count == 0) b.AppendLine("Nenhuma tentativa de recuperação registrada nesta carga.");
        b.AppendLine().AppendLine("LIMITAÇÕES");
        Lines(b, o.Limitations);
        b.AppendLine().AppendLine("PRÓXIMAS AÇÕES");
        Lines(b, o.Guidance);
        b.AppendLine().AppendLine("Localização do relatório: " + o.ReportPath);
        return b.ToString();
    }

    public static string Timeline(RecoveryOperation operation) => operation.Events.Count == 0
        ? "Eventos indisponíveis; nenhuma etapa presumida."
        : string.Join(Environment.NewLine, operation.Events.OrderBy(e => e.Sequence).Select(e =>
            $"{e.Sequence:D4} | {e.AtUtc.ToLocalTime():g} | {e.State}{Environment.NewLine}{e.Detail ?? "Detalhe não registrado"}{(e.Error is null ? "" : Environment.NewLine + "Erro: " + e.Error)}"));
    private static string YesNo(bool value) => value ? "sim" : "não";
    private static string Describe(TargetValueState? value) => value is null ? "indisponível" : VerificationEngine.Describe(value);
    private static void Lines(StringBuilder builder, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) builder.AppendLine("Não registrado.");
        else foreach (var line in lines) builder.AppendLine("• " + line);
    }
}
