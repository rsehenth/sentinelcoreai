using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public static class TransactionReport
{
    public static string RenderEvent(TransactionEventRecord entry, bool syntheticDebloat = false)
    {
        var builder = new StringBuilder();
        builder.AppendLine(syntheticDebloat ? "# Evento Debloat — SYNTHETIC" : "# Evento de transação");
        if (syntheticDebloat) AppendDebloatNotice(builder);
        builder.AppendLine();
        builder.AppendLine($"- Transação: `{entry.TransactionId:D}`");
        builder.AppendLine($"- Sequência: {entry.Sequence}");
        builder.AppendLine($"- Estado: {entry.State}");
        builder.AppendLine($"- Operação: `{Safe(entry.OperationId)}` v{entry.OperationVersion}");
        builder.AppendLine($"- Catálogo: `{Safe(entry.CatalogVersion)}`");
        builder.AppendLine($"- Hash do plano: `{Safe(entry.PlanHash)}`");
        builder.AppendLine($"- Horário UTC: {entry.AtUtc:O}");
        if (!string.IsNullOrWhiteSpace(entry.Detail)) builder.AppendLine($"- Detalhe: {Safe(entry.Detail)}");
        if (!string.IsNullOrWhiteSpace(entry.Error)) builder.AppendLine($"- Erro: {Safe(entry.Error)}");
        if (entry.BackupPath is not null) builder.AppendLine($"- Backup: `{Safe(entry.BackupPath)}`");
        if (entry.Plan is not null) AppendPlan(builder, entry.Plan);
        if (entry.Authorization is not null) AppendAuthorization(builder, entry.Authorization);
        if (entry.Backup is not null) AppendBackup(builder, entry.Backup);
        if (entry.Verification is not null) AppendVerification(builder, entry.Verification);
        if (entry.Recovery is not null) AppendRecovery(builder, entry.Recovery);
        if (entry.Results is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("## Resultados");
            foreach (var step in entry.Results) AppendStep(builder, step);
        }
        if (entry.Limitations.Count > 0) AppendLimitations(builder, entry.Limitations);
        return builder.ToString();
    }

    public static string RenderBackup(BackupRecord backup, bool syntheticDebloat = false)
    {
        var builder = new StringBuilder();
        builder.AppendLine(syntheticDebloat ? "# Snapshot de metadados Debloat — SYNTHETIC" : "# Backup do estado observado");
        if (syntheticDebloat) AppendDebloatNotice(builder);
        builder.AppendLine();
        builder.AppendLine($"- Backup: `{backup.BackupId:D}`");
        builder.AppendLine($"- Transação: `{backup.TransactionId:D}`");
        builder.AppendLine($"- Plano: `{backup.PlanId:D}`");
        builder.AppendLine($"- Operação: `{Safe(backup.OperationId)}` v{backup.OperationVersion}");
        builder.AppendLine($"- Sequência: {backup.Sequence}");
        builder.AppendLine($"- Horário UTC: {backup.CreatedAtUtc:O}");
        builder.AppendLine($"- Hash do conteúdo: `{backup.ContentHash}`");
        builder.AppendLine(syntheticDebloat
            ? $"- Metadados completos: {(backup.IsComplete ? "sim" : "não")}"
            : $"- Completo para reversão: {(backup.IsComplete ? "sim" : "não")}");
        AppendBackup(builder, backup);
        if (backup.Limitations.Count > 0) AppendLimitations(builder, backup.Limitations);
        return builder.ToString();
    }

    public static string Render(TransactionRecord transaction, bool syntheticDebloat = false)
    {
        var builder = new StringBuilder();
        builder.AppendLine(syntheticDebloat ? "# Debloat — SYNTHETIC" : "# Transação de modificação controlada");
        builder.AppendLine();
        var targetsCurrentUser = transaction.Plan?.Changes.Any(change =>
            string.Equals(change.Target.Root, OperationTargetRoots.CurrentUser, StringComparison.Ordinal)) == true;
        if (syntheticDebloat) AppendDebloatNotice(builder);
        else builder.AppendLine(targetsCurrentUser
            ? "Operação controlada em HKCU do usuário atual. Estado observado, backup, autorização, " +
              "execução e verificação permanecem registrados abaixo."
            : "Nenhuma alteração real do Windows foi executada. Este registro descreve uma operação sintética da T1.");
        builder.AppendLine();
        builder.AppendLine($"- Transação: `{transaction.TransactionId:D}`");
        builder.AppendLine($"- Operação: `{Safe(transaction.OperationId)}` v{transaction.OperationVersion}");
        builder.AppendLine($"- Catálogo: `{Safe(transaction.CatalogVersion)}`");
        builder.AppendLine($"- Hash do plano: `{transaction.PlanHash}`");
        builder.AppendLine($"- Estado final: {transaction.State}");
        builder.AppendLine($"- Sucesso verificado: {(transaction.Succeeded ? "sim" : "não")}");
        builder.AppendLine($"- Indeterminado: {(transaction.IsIndeterminate ? "sim" : "não")}");
        builder.AppendLine($"- Criado UTC: {transaction.CreatedAtUtc:O}");
        builder.AppendLine($"- Atualizado UTC: {transaction.UpdatedAtUtc:O}");
        if (transaction.Plan is not null) AppendPlan(builder, transaction.Plan);
        if (transaction.Authorization is not null) AppendAuthorization(builder, transaction.Authorization);
        if (transaction.Backup is not null) AppendBackup(builder, transaction.Backup);
        if (transaction.Verification is not null) AppendVerification(builder, transaction.Verification);
        if (transaction.Recovery is not null) AppendRecovery(builder, transaction.Recovery);

        builder.AppendLine();
        builder.AppendLine("## Journal");
        foreach (var entry in transaction.Events.OrderBy(item => item.Sequence))
            builder.AppendLine($"{entry.Sequence}. {entry.State} — {entry.AtUtc:O}" +
                (string.IsNullOrWhiteSpace(entry.Detail) ? "" : $" — {Safe(entry.Detail)}") +
                (string.IsNullOrWhiteSpace(entry.Error) ? "" : $" — erro: {Safe(entry.Error)}"));

        if (transaction.Limitations.Count > 0) AppendLimitations(builder, transaction.Limitations);
        return builder.ToString();
    }

    public static string RenderIndex(IReadOnlyList<TransactionRecord> transactions, bool syntheticDebloat = false)
    {
        var builder = new StringBuilder();
        builder.AppendLine(syntheticDebloat ? "# DEBLOAT — SYNTHETIC" : "# RECOVERY — transações e recuperações");
        builder.AppendLine();
        builder.AppendLine(syntheticDebloat
            ? "Índice reconstruível a partir dos registros imutáveis em `memory/debloat`."
            : "Índice reconstruível a partir dos registros imutáveis em `memory/transactions`.");
        if (syntheticDebloat) AppendDebloatNotice(builder);
        builder.AppendLine();
        builder.AppendLine(syntheticDebloat
            ? "| Transação | Operação | Estado | Sucesso sintético | Indeterminado | Atualizado UTC |"
            : "| Transação | Operação | Estado | Sucesso | Indeterminado | Atualizado UTC | Recuperação |");
        builder.AppendLine(syntheticDebloat ? "|---|---|---|---|---|---|" : "|---|---|---|---|---|---|---|");
        foreach (var transaction in transactions.OrderByDescending(item => item.UpdatedAtUtc))
            builder.AppendLine($"| `{transaction.TransactionId:D}` | {Cell(transaction.OperationId)} | " +
                $"{transaction.State} | {(transaction.Succeeded ? "sim" : "não")} | " +
                $"{(transaction.IsIndeterminate ? "sim" : "não")} | {transaction.UpdatedAtUtc:O} |" +
                (syntheticDebloat ? "" : $" {(transaction.Recovery is null ? "não tentada" : transaction.Recovery.Outcome.ToString())} |"));
        return builder.ToString();
    }

    private static void AppendDebloatNotice(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("SYNTHETIC: fixtures locais; nenhum pacote real foi consultado, removido ou reinstalado.");
        builder.AppendLine("Snapshot de metadados não é backup dos dados do aplicativo. Não oferece rollback, " +
            "reinstalação nem recuperação de dados. Validação real pendente.");
    }

    private static void AppendPlan(StringBuilder builder, OperationPlan plan)
    {
        builder.AppendLine();
        builder.AppendLine("## Plano");
        builder.AppendLine();
        builder.AppendLine($"- Plano: `{plan.PlanId:D}`");
        builder.AppendLine($"- Risco: {plan.Risk}");
        builder.AppendLine($"- Escopo: {plan.Scope}");
        builder.AppendLine($"- Hash do estado observado: `{plan.ObservedStateHash}`");
        builder.AppendLine();
        builder.AppendLine("| Alvo | Tipo | Antes | Depois |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var change in plan.Changes)
            builder.AppendLine($"| {Cell(change.TargetIdentity)} | {change.Kind} | " +
                $"{Cell(Describe(change.Before))} | {Cell(Describe(change.After))} |");
    }

    private static void AppendBackup(StringBuilder builder, BackupRecord backup)
    {
        builder.AppendLine();
        builder.AppendLine("## Backup");
        builder.AppendLine();
        builder.AppendLine($"- Backup: `{backup.BackupId:D}`");
        builder.AppendLine($"- Hash do conteúdo: `{backup.ContentHash}`");
        builder.AppendLine($"- Completo: {(backup.IsComplete ? "sim" : "não")}");
        builder.AppendLine();
        builder.AppendLine("| Alvo | Alvo existe | Valor existe | Tipo | Conteúdo | Observado UTC | Checksum |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var entry in backup.Entries)
            builder.AppendLine($"| {Cell(entry.TargetIdentity)} | {(entry.TargetExists ? "sim" : "não")} | " +
                $"{(entry.Value?.Exists == true ? "sim" : "não")} | {Cell(entry.Value?.Kind ?? "ausente")} | " +
                $"{Cell(entry.Value?.Data is null ? "ausente" : entry.Value.Data)} | " +
                $"{(entry.ObservedAtUtc is { } observed ? observed.ToString("O") : "indisponível")} | " +
                $"{Cell(entry.Checksum)} |");
    }

    private static void AppendAuthorization(StringBuilder builder, AuthorizationToken authorization)
    {
        builder.AppendLine();
        builder.AppendLine("## Autorização");
        builder.AppendLine();
        builder.AppendLine($"- Autorização: `{authorization.AuthorizationId:D}`");
        builder.AppendLine($"- Plano: `{authorization.PlanId:D}`");
        builder.AppendLine($"- Transação: `{authorization.TransactionId:D}`");
        builder.AppendLine($"- Confirmação: {Safe(authorization.UserConfirmationId)}");
        builder.AppendLine($"- Emissão UTC: {authorization.IssuedAtUtc:O}");
        builder.AppendLine($"- Expiração UTC: {authorization.ExpiresAtUtc:O}");
        builder.AppendLine($"- Hash do estado observado: `{authorization.ObservedStateHash}`");
        builder.AppendLine($"- Consumida: {(authorization.Consumed ? "sim" : "não")}");
    }

    private static void AppendVerification(StringBuilder builder, VerificationResult verification)
    {
        builder.AppendLine();
        builder.AppendLine("## Verificação");
        builder.AppendLine();
        builder.AppendLine($"- Aprovada: {(verification.Passed ? "sim" : "não")}");
        builder.AppendLine($"- Hash posterior: `{verification.PostStateHash}`");
        foreach (var step in verification.Steps) AppendStep(builder, step);
    }

    private static void AppendRecovery(StringBuilder builder, RecoveryResult recovery)
    {
        builder.AppendLine();
        builder.AppendLine("## Recuperação");
        builder.AppendLine();
        builder.AppendLine($"- Recuperação: `{recovery.RecoveryId:D}`");
        builder.AppendLine($"- Resultado: {recovery.Outcome}");
        builder.AppendLine($"- Integral: {(recovery.FullyRecovered ? "sim" : "não")}");
        builder.AppendLine($"- Efeito pode ter ocorrido: {(recovery.EffectMayHaveOccurred ? "sim" : "não")}");
        builder.AppendLine($"- Início UTC: {recovery.StartedAtUtc:O}");
        builder.AppendLine($"- Fim UTC: {recovery.CompletedAtUtc:O}");
        if (!string.IsNullOrWhiteSpace(recovery.Error)) builder.AppendLine($"- Erro: {Safe(recovery.Error)}");
        foreach (var step in recovery.Steps) AppendStep(builder, step);
        foreach (var conflict in recovery.Conflicts)
            builder.AppendLine($"- Conflito em {Cell(conflict.TargetIdentity)}: {conflict.Kind} — {Safe(conflict.Description)}");
    }

    private static void AppendStep(StringBuilder builder, StepResult step) =>
        builder.AppendLine($"- {Cell(step.TargetIdentity)}: aplicado={step.Applied}, alterado={step.Changed}, " +
            $"verificado={step.Verified}" + (step.Error is null ? "" : $", erro={Safe(step.Error)}"));

    private static void AppendLimitations(StringBuilder builder, IReadOnlyList<string> limitations)
    {
        builder.AppendLine();
        builder.AppendLine("## Limitações");
        foreach (var limitation in limitations) builder.AppendLine($"- {Safe(limitation)}");
    }

    private static string Describe(TargetValueState? value) => value is null
        ? "indisponível"
        : !value.Exists
            ? $"{value.Kind} ausente"
            : $"{value.Kind} = \"{value.Data}\"";

    private static string Cell(string? value) => Safe(value).Replace("|", "\\|");

    private static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "—";
        return value
            .Replace("<!-- sentinelcore:payload:v1 -->", "[marcador removido]")
            .Replace("<!-- sha256:", "[checksum removido]")
            .Replace("```", "'''")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("`", "'");
    }
}
