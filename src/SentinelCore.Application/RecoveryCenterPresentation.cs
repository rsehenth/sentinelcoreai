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
        RecoveryCapability.InterventionRequired => "Intervenção necessária",
        RecoveryCapability.RequiresAnalysis => "Prévia necessária", _ => "Estado indeterminado"
    };
    public static string Label(RiskLevel risk) => risk switch
    { RiskLevel.Low => "Baixo", RiskLevel.Medium => "Médio", RiskLevel.High => "Alto", _ => "Não validado" };

    public static string Summary(RecoveryCenterSnapshot snapshot) => Summary(snapshot, null);
    public static string Summary(RecoveryCenterSnapshot snapshot, Func<string, string>? translate) =>
        new Renderer(translate).Summary(snapshot);
    public static string Details(RecoveryOperation operation) => Details(operation, null);
    public static string Details(RecoveryOperation operation, Func<string, string>? translate) =>
        new Renderer(translate).Details(operation);
    public static string Timeline(RecoveryOperation operation) => Timeline(operation, null);
    public static string Timeline(RecoveryOperation operation, Func<string, string>? translate) =>
        new Renderer(translate).Timeline(operation);

    private sealed class Renderer(Func<string, string>? translate)
    {
        private string T(string value) => translate?.Invoke(value) ?? value;
        private string F(FormattableString value)
        {
            // Only format literals are translated; historical values and identifiers are never passed to T.
            var parts = System.Text.RegularExpressions.Regex.Split(value.Format,
                @"(\{[0-9]+(?:,-?[0-9]+)?(?::[^{}]*)?\})");
            for (var index = 0; index < parts.Length; index += 2) parts[index] = T(parts[index]);
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, string.Concat(parts), value.GetArguments());
        }
        public string Summary(RecoveryCenterSnapshot snapshot)
        {
            var s = RecoveryCenter.Summarize(snapshot);
            var text = new StringBuilder(s.IsComplete ? T("Históricos carregados. ") : T("HISTÓRICO INCOMPLETO — quantidades apenas dos registros legíveis; totais indisponíveis. "));
            text.Append(F($"{s.Registered} registradas; {s.Completed} concluídas; {s.Failed} com falha; {s.Indeterminate} indeterminadas; {s.Conflicts} com conflitos; {s.Intervention} requerem intervenção. "));
            text.Append(F($"{snapshot.Operations.Count(o => o.Interrupted)} interrompidas; {snapshot.Operations.Count(o => o.PartiallyApplied)} com aplicação parcial registrada. "));
            text.Append(F($"Recuperações: {s.RecoveriesCompleted} integrais; {s.RecoveriesPartial} parciais; {s.RecoveriesBlocked} bloqueadas; {s.RecoveriesFailed} falhas/interrompidas."));
            return text.ToString();
        }

        public string Details(RecoveryOperation o)
        {
            var b = new StringBuilder();
            b.AppendLine(T("IDENTIFICAÇÃO"));
            b.AppendLine(F($"{T(Label(o.Module))} | {o.OperationId} | {o.Id:D}"));
            b.AppendLine(F($"Data local: {o.AtUtc.ToLocalTime():g}; tipo: {o.Type}"));
            b.AppendLine(F($"Alvo: {o.Target}"));
            b.AppendLine(F($"Origem: {T(Label(o.Origin))}; risco: {(o.Risk is { } risk ? T(Label(risk)) : T("não registrado"))}"));
            b.AppendLine().AppendLine(T("ESTADO E EVIDÊNCIAS"));
            b.AppendLine(F($"{o.State} | {T(Label(o.Outcome))}"));
            if (o.Interrupted) b.AppendLine(T("Interrupção registrada ou sem fechamento no journal."));
            if (o.PartiallyApplied) b.AppendLine(T("A aplicação confirmou efeito em parte do plano. Consulte o recibo e a recuperação."));
            b.AppendLine(F($"{o.Verification}; efeito possivelmente iniciado: {YesNo(o.EffectMayHaveOccurred)}"));
            b.AppendLine(F($"Conflito registrado: {YesNo(o.HasConflict)}; intervenção: {YesNo(o.RequiresIntervention)}; bloqueio persistente T3C: {YesNo(o.TargetPersistentlyBlocked)}"));
            b.AppendLine().AppendLine(T("CONSEQUÊNCIAS"));
            Lines(b, o.Consequences);
            b.AppendLine().AppendLine(T("PROTEÇÕES"));
            b.AppendLine(T("Autorização: ") + o.Authorization);
            b.AppendLine(T("Backup: ") + o.Backup);
            if (o.Transaction?.Plan is { } plan)
            {
                b.AppendLine(F($"Plano: {plan.PlanHash}; catálogo {plan.CatalogVersion}; versão {plan.OperationVersion}"));
                foreach (var change in plan.Changes)
                {
                    b.AppendLine(F($"{change.TargetIdentity}: anterior (alvo existe={change.BeforeTargetExists}): {VerificationEngine.Describe(change.Before)}"));
                    b.AppendLine(F($"Pretendido (alvo existe={change.AfterTargetExists}): {VerificationEngine.Describe(change.After)}"));
                }
                var receipt = o.Transaction.Events.LastOrDefault(entry => entry.State is
                    TransactionState.ExecutionCompleted or TransactionState.ExecutionFailed or TransactionState.ExecutionIndeterminate);
                if (receipt?.Results is { } results)
                {
                    b.AppendLine(F($"Recibo da aplicação no journal #{receipt.Sequence:D4}, às {receipt.AtUtc.ToLocalTime():g}."));
                    foreach (var step in results)
                        b.AppendLine(F($"{step.TargetIdentity}: aplicação confirmada={YesNo(step.Applied)}; alteração={YesNo(step.Changed)}; {step.Error ?? string.Join("; ", step.Effects)}"));
                }
                else b.AppendLine(T("Recibo da aplicação indisponível."));
                foreach (var step in o.Transaction.Verification?.Steps ?? [])
                    b.AppendLine(F($"Pós-verificação de {step.TargetIdentity}: {step.Error ?? string.Join("; ", step.Effects)}"));
            }
            b.AppendLine().AppendLine(T("RECUPERAÇÃO"));
            b.AppendLine(T(Label(o.Capability)));
            if (o.Preview is { } preview)
            {
                b.AppendLine(F($"Prévia de recuperação: {preview.Outcome}. {preview.Reason}"));
                foreach (var step in preview.CurrentStates)
                    b.AppendLine(F($"{step.TargetIdentity}: {step.Error ?? string.Join(" ", step.Effects)}"));
                foreach (var c in preview.Conflicts)
                    b.AppendLine(F($"Conflito {c.TargetIdentity}: {c.Kind}; {c.Description}; atual={Describe(c.Current)}, anterior={Describe(c.Observed)}, pós-estado esperado={Describe(c.ExpectedPost)}"));
            }
            foreach (var r in o.Recoveries.OrderBy(r => r.StartedAtUtc))
            {
                b.AppendLine(F($"{r.StartedAtUtc.ToLocalTime():g} | {r.RecoveryId:D} | {r.Outcome}; integral={r.FullyRecovered}; efeito possível={r.EffectMayHaveOccurred}; {r.Error}"));
                foreach (var c in r.Conflicts) b.AppendLine(F($"Conflito: {c.TargetIdentity}; {c.Kind}; {c.Description}"));
                foreach (var step in r.Steps) b.AppendLine(F($"Alvo {step.TargetIdentity}: aplicado={step.Applied}; verificado={step.Verified}; {step.Error}"));
            }
            if (o.Recoveries.Count == 0) b.AppendLine(T("Nenhuma tentativa de recuperação registrada nesta carga."));
            b.AppendLine().AppendLine(T("LIMITAÇÕES"));
            Lines(b, o.Limitations);
            b.AppendLine().AppendLine(T("PRÓXIMAS AÇÕES"));
            Lines(b, o.Guidance);
            b.AppendLine().AppendLine(T("Localização do relatório: ") + o.ReportPath);
            return b.ToString();
        }

        public string Timeline(RecoveryOperation operation) => operation.Events.Count == 0
            ? T("Eventos indisponíveis; nenhuma etapa presumida.")
            : string.Join(Environment.NewLine, operation.Events.OrderBy(e => e.Sequence).Select(e =>
                F($"{e.Sequence:D4} | {e.AtUtc.ToLocalTime():g} | {e.State}{Environment.NewLine}{e.Detail ?? T("Detalhe não registrado")}{(e.Error is null ? "" : Environment.NewLine + T("Erro: ") + e.Error)}")));
        private string YesNo(bool value) => value ? T("sim") : T("não");
        private string Describe(TargetValueState? value) => value is null ? T("indisponível") : VerificationEngine.Describe(value);
        private void Lines(StringBuilder builder, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) builder.AppendLine(T("Não registrado."));
            else foreach (var line in lines) builder.AppendLine(T("• ") + line);
        }
    }
}
