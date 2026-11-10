using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Read-only WPF presentation, usable with typed T3C plans, progress, results and historical records.</summary>
public sealed record DebloatRemovalPresentation(string Plan, string Dependencies, string Risks,
    string Authorization, string Progress, string Result, string History, string Limitations, string Reinstallation)
{
    public static DebloatRemovalPresentation Unavailable() => new(DebloatRemovalMessages.NativeBlocked,
        "Dependências diretas, reversas, compartilhadas e efeitos indiretos exigem observação completa.",
        "153 NÃO VALIDADOS / 9 ALTO. Nenhum aplicativo autorizado para remoção real.",
        "Autorização específica exige plano simulado, hash, SID, identidade completa e revalidação.",
        "Não iniciado. Sem percentual informado.", "Nenhuma execução T3C iniciada.",
        "Registros T3C usam o journal Debloat existente.", string.Join("\n", DebloatRemovalMessages.Limitations),
        "Informação documental; reinstalação não garante recuperação de dados.");

    public static string HistoricalEvidence(DebloatRemovalSnapshot snapshot, DebloatRemovalReceipt? receipt,
        TransactionRecord record)
    {
        var observation = snapshot.Observation;
        var package = observation.SelectedPackage;
        var previous = observation.Packages.Single(p => DebloatRemovalIdentityPolicy.From(p) == package);
        var dependencies = observation.Dependencies;
        static string Names(IReadOnlyList<DebloatRemovalIdentity> items) => items.Count == 0
            ? "Nenhuma na fixture" : string.Join(", ", items.Select(p => p.PackageFullName));
        return $"Escopo anterior: {DebloatPresentation.Scope(previous.Scope)} (fixture)\n" +
            $"Estado anterior: {previous.InstallState} · PFN: {package.PackageFamilyName} · arquitetura: {package.Architecture}\n" +
            $"Dependências ({DebloatPresentation.Completeness(dependencies.Completeness)})\n" +
            $"Diretas: {Names(dependencies.Direct)}\nReversas: {Names(dependencies.Reverse)}\n" +
            $"Compartilhadas: {Names(dependencies.Shared)}\nPotencialmente afetadas: {Names(dependencies.PotentiallyAffected)}\n" +
            (receipt is null ? "Recibo indisponível.\n"
                : $"Recibo: {receipt.Phase} · falha parcial: {(receipt.PartialFailure ? "sim" : "não")} · efeito possível: {(receipt.EffectMayHaveOccurred ? "sim" : "não")}\n" +
                  $"HRESULT: {receipt.HResult?.ToString() ?? "indisponível"} · atividade: {receipt.ActivityId?.ToString() ?? "indisponível"}\n" +
                  $"Erro: {receipt.ErrorText ?? "não registrado"}\nPós-verificação: {receipt.PostVerification?.Information ?? "indisponível"}\n") +
            (record.IsIndeterminate || receipt?.PartialFailure == true
                ? "Recuperação pendente de investigação. O alvo permanece bloqueado se o efeito não puder ser confirmado.\n" : "") +
            "Reinstalação real indisponível pelo SentinelCore. A evidência histórica contém somente metadados.";
    }

    public static DebloatRemovalPresentation From(DebloatRemovalPlan plan,
        DebloatRemovalProgress? progress = null, DebloatRemovalResult? result = null,
        DebloatRemovalHistory? history = null)
    {
        var o = plan.Snapshot.Observation;
        var d = o.Dependencies;
        static string Names(IReadOnlyList<DebloatRemovalIdentity> items) => items.Count == 0
            ? "Nenhuma na observação SINTÉTICA completa" : string.Join("; ", items.Select(p => p.PackageFullName));
        return new(
            $"{DebloatLimits.SyntheticWarning}\nPlano: {plan.Id}\nHash: {plan.Hash}\n" +
            $"Pacote: {o.SelectedPackage.PackageFullName}\nPublisher: {o.SelectedPackage.Publisher}\n" +
            $"Versão: {o.SelectedPackage.Version}; arquitetura: {o.SelectedPackage.Architecture}\n" +
            $"SID sintético: {o.CurrentUserSecurityId}\nInventário: {o.InventoryRevision}\n" +
            "Consequências:\n" + string.Join("\n", o.Consequences),
            $"Completude: {d.Completeness}\nDiretas: {Names(d.Direct)}\nReversas: {Names(d.Reverse)}\n" +
            $"Compartilhadas: {Names(d.Shared)}\nPotencialmente afetados: {Names(d.PotentiallyAffected)}\n" +
            "Aplicativos que poderão ser encerrados: " + string.Join("; ", d.ApplicationsMayClose) +
            "\nConsequências desconhecidas: " + string.Join("; ", d.UnknownConsequences) +
            "\n" + DebloatRemovalMessages.IndirectEffects,
            $"Risco real: {plan.Preflight.RealRisk}. Execução real bloqueada.\n" + string.Join("\n", plan.Preflight.RealBlockers),
            "Somente simulação: " + plan.Confirmation + "\nExpiração: 2 minutos; uso único; mudanças exigem novo plano.",
            progress is null ? "Não iniciado. Sem percentual informado." :
                $"{progress.Phase}; " + (progress.ReportedPercentage is null ? "sem percentual informado" :
                    $"percentual informado pelo provedor: {progress.ReportedPercentage}%") + "\n" + DebloatRemovalMessages.Cancellation,
            result is null ? "Sem resultado de execução." :
                $"SINTÉTICO — {result.State}; sucesso verificado: {result.Succeeded}; intervenção: {result.RequiresIntervention}\n" +
                $"HRESULT: {result.Receipt?.HResult?.ToString() ?? "indisponível"}; erro: {result.Receipt?.ErrorText ?? "não informado"}\n" +
                $"Pós-verificação: {result.PostVerification?.Information ?? "não disponível"}",
            history is null ? "Histórico não carregado neste componente." : string.Join("\n", history.Items.Select(item =>
                $"{item.Transaction.UpdatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} — {item.Id} — SINTÉTICO {item.Transaction.State}")) +
                "\n" + string.Join("\n", history.Warnings),
            string.Join("\n", DebloatRemovalMessages.Limitations), o.ReinstallInformation + "\n" + DebloatLimits.RecoveryWarning);
    }

    /// <summary>The existing T3 UI can preview the new safety requirements without inventing a T3C authorization.</summary>
    public static DebloatRemovalPresentation FromLegacySynthetic(DebloatPreparedPlan plan) => Unavailable() with
    {
        Plan = $"Prévia da fixture T3: {plan.Package.PackageFullName}\nPlano T3: {plan.Id}; hash: {plan.PlanHash}\n" +
            "Este plano não é autorização T3C ou autorização real.\n" + string.Join("\n", plan.Consequences),
        Dependencies = $"Dependências informadas pela fixture T3: {plan.Package.DependencyStatus}\n" +
            "Observações diretas, reversas, compartilhadas e efeitos indiretos T3C não foram executados nesta prévia.\n" +
            DebloatRemovalMessages.IndirectEffects,
        Reinstallation = plan.CatalogEntry.ReinstallInformation + "\n" + DebloatLimits.RecoveryWarning
    };
}
