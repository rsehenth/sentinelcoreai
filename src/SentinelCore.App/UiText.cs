using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

/// <summary>Rótulos em português para enums exibidos na interface. Somente apresentação: o valor persistido,
/// os códigos e os identificadores técnicos não mudam. Valor desconhecido cai no nome original (nunca vira sucesso).</summary>
internal static class UiText
{
    public static string Risk(RiskLevel risk) => Loc.T(RepairPresentation.RiskName(risk));

    public static string State(TransactionState state) => Loc.T(state switch
    {
        TransactionState.Planned => "Planejada",
        TransactionState.PreflightPassed => "Pré-verificação aprovada",
        TransactionState.BackupPrepared => "Backup preparado",
        TransactionState.BackupVerified => "Backup verificado",
        TransactionState.Authorized => "Autorizada",
        TransactionState.ExecutionStarted => "Execução iniciada",
        TransactionState.ExecutionCompleted => "Execução concluída",
        TransactionState.ExecutionFailed => "Execução com falha",
        TransactionState.ExecutionIndeterminate => "Execução indeterminada",
        TransactionState.VerificationPassed => "Verificação aprovada",
        TransactionState.VerificationFailed => "Verificação reprovada",
        TransactionState.RecoveryRequested => "Recuperação solicitada",
        TransactionState.RecoveryStarted => "Recuperação iniciada",
        TransactionState.RecoveryCompleted => "Recuperação concluída",
        TransactionState.RecoveryFailed => "Recuperação com falha",
        TransactionState.Interrupted => "Interrompida",
        TransactionState.Cancelled => "Cancelada",
        TransactionState.Blocked => "Bloqueada",
        _ => state.ToString()
    });

    public static string Outcome(RecoveryOutcome outcome) => Loc.T(outcome switch
    {
        RecoveryOutcome.NotAttempted => "não tentada",
        RecoveryOutcome.Completed => "concluída",
        RecoveryOutcome.PartiallyCompleted => "parcial",
        RecoveryOutcome.Blocked => "bloqueada",
        RecoveryOutcome.Conflict => "conflito",
        RecoveryOutcome.Failed => "falha",
        RecoveryOutcome.AlreadyRestored => "já restaurado",
        RecoveryOutcome.NotRecoverable => "não recuperável",
        RecoveryOutcome.Interrupted => "interrompida",
        _ => outcome.ToString()
    });

    public static string Conflict(RecoveryConflictKind kind) => Loc.T(kind switch
    {
        RecoveryConflictKind.None => "sem conflito",
        RecoveryConflictKind.StateChangedAfterTransaction => "estado alterado após a operação",
        RecoveryConflictKind.TargetMissing => "alvo ausente",
        RecoveryConflictKind.KindChanged => "tipo de valor alterado",
        RecoveryConflictKind.UnknownState => "estado desconhecido",
        _ => kind.ToString()
    });

    public static string Scope(OperationScope scope) => Loc.T(scope switch
    {
        OperationScope.CurrentUser => "usuário atual",
        OperationScope.Machine => "computador",
        OperationScope.AllUsers => "todos os usuários",
        OperationScope.ServiceAccount => "conta de serviço",
        _ => scope.ToString()
    });

    /// <summary>Estado registrado (texto livre da fonte): traduz só códigos reconhecidos; o restante permanece como gravado.</summary>
    public static string Recorded(string state) =>
        Enum.TryParse<TransactionState>(state, out var transaction) && Enum.IsDefined(transaction) ? State(transaction)
        : Enum.TryParse<RepairState>(state, out var repair) && Enum.IsDefined(repair) ? Loc.T(RepairPresentation.StateName(repair))
        : state;

    public static string Diagnostic(DiagnosticStatus status) => Loc.T(status switch
    {
        DiagnosticStatus.Pending => "Pendente", DiagnosticStatus.Running => "Em andamento",
        DiagnosticStatus.Completed => "Concluída", DiagnosticStatus.Partial => "Parcial",
        DiagnosticStatus.Failed => "Falhou", DiagnosticStatus.Cancelled => "Cancelada",
        DiagnosticStatus.Interrupted => "Interrompida", _ => "Estado desconhecido"
    });

    /// <summary>Estado observado já formatado pela camada de segurança; só traduz o tipo "Unknown" de valor inexistente.</summary>
    public static string Observed(string text) => Loc.T(text.Replace("Unknown ausente", "valor ausente", StringComparison.Ordinal));
}
