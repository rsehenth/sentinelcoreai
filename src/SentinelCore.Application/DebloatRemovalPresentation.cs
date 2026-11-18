using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Read-only WPF presentation, usable with typed T3C plans, progress, results and historical records.</summary>
public sealed record DebloatRemovalPresentation(string Plan, string Dependencies, string Risks,
    string Authorization, string Progress, string Result, string History, string Limitations, string Reinstallation)
{
    public static DebloatRemovalPresentation Unavailable(Func<string, string>? translate = null) => new(
        T(translate, DebloatRemovalMessages.NativeBlocked),
        T(translate, "Dependências diretas, reversas, compartilhadas e efeitos indiretos exigem observação completa."),
        T(translate, "153 NÃO VALIDADOS / 9 ALTO. Nenhum aplicativo autorizado para remoção real."),
        T(translate, "Autorização específica exige plano simulado, hash, SID, identidade completa e revalidação."),
        T(translate, "Não iniciado. Sem percentual informado."), T(translate, "Nenhuma execução T3C iniciada."),
        T(translate, "Registros T3C usam o journal Debloat existente."),
        string.Join("\n", DebloatRemovalMessages.Limitations.Select(value => T(translate, value))),
        T(translate, "Informação documental; reinstalação não garante recuperação de dados."));

    /// <summary>Evidence text persisted with the historical record; identifiers and original wording are never translated.</summary>
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
        DebloatRemovalHistory? history = null, Func<string, string>? translate = null)
    {
        var o = plan.Snapshot.Observation;
        var d = o.Dependencies;
        string T(string value) => translate?.Invoke(value) ?? value;
        // Only format literals are translated; plan identifiers, hashes, PFNs and confirmations are never passed to T.
        string F(FormattableString value)
        {
            var parts = System.Text.RegularExpressions.Regex.Split(value.Format,
                @"(\{[0-9]+(?:,-?[0-9]+)?(?::[^{}]*)?\})");
            for (var index = 0; index < parts.Length; index += 2) parts[index] = T(parts[index]);
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, string.Concat(parts), value.GetArguments());
        }
        string Names(IReadOnlyList<DebloatRemovalIdentity> items) => items.Count == 0
            ? T("Nenhuma na observação SINTÉTICA completa") : string.Join("; ", items.Select(p => p.PackageFullName));
        return new(
            F($"{T(DebloatLimits.SyntheticWarning)}\nPlano: {plan.Id}\nHash: {plan.Hash}\n") +
            F($"Pacote: {o.SelectedPackage.PackageFullName}\nPublisher: {o.SelectedPackage.Publisher}\n") +
            F($"Versão: {o.SelectedPackage.Version}; arquitetura: {o.SelectedPackage.Architecture}\n") +
            F($"SID sintético: {o.CurrentUserSecurityId}\nInventário: {o.InventoryRevision}\n") +
            T("Consequências:\n") + string.Join("\n", o.Consequences),
            F($"Completude: {d.Completeness}\nDiretas: {Names(d.Direct)}\nReversas: {Names(d.Reverse)}\n") +
            F($"Compartilhadas: {Names(d.Shared)}\nPotencialmente afetados: {Names(d.PotentiallyAffected)}\n") +
            T("Aplicativos que poderão ser encerrados: ") + string.Join("; ", d.ApplicationsMayClose) +
            T("\nConsequências desconhecidas: ") + string.Join("; ", d.UnknownConsequences) +
            "\n" + T(DebloatRemovalMessages.IndirectEffects),
            F($"Risco real: {plan.Preflight.RealRisk}. ") + T("Execução real bloqueada.\n") +
            string.Join("\n", plan.Preflight.RealBlockers),
            T("Somente simulação: ") + plan.Confirmation +
            T("\nExpiração: 2 minutos; uso único; mudanças exigem novo plano."),
            progress is null ? T("Não iniciado. Sem percentual informado.") :
                F($"{progress.Phase}; ") + (progress.ReportedPercentage is null ? T("sem percentual informado") :
                    F($"percentual informado pelo provedor: {progress.ReportedPercentage}%")) + "\n" + T(DebloatRemovalMessages.Cancellation),
            result is null ? T("Sem resultado de execução.") :
                F($"SINTÉTICO — {result.State}; sucesso verificado: {result.Succeeded}; intervenção: {result.RequiresIntervention}\n") +
                F($"HRESULT: {result.Receipt?.HResult?.ToString() ?? T("indisponível")}; erro: {result.Receipt?.ErrorText ?? T("não informado")}\n") +
                F($"Pós-verificação: {result.PostVerification?.Information ?? T("não disponível")}"),
            history is null ? T("Histórico não carregado neste componente.") : string.Join("\n", history.Items.Select(item =>
                F($"{item.Transaction.UpdatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} — {item.Id} — SINTÉTICO {item.Transaction.State}"))) +
                "\n" + string.Join("\n", history.Warnings),
            string.Join("\n", DebloatRemovalMessages.Limitations.Select(T)), o.ReinstallInformation + "\n" + T(DebloatLimits.RecoveryWarning));
    }

    /// <summary>The existing T3 UI can preview the new safety requirements without inventing a T3C authorization.</summary>
    public static DebloatRemovalPresentation FromLegacySynthetic(DebloatPreparedPlan plan,
        Func<string, string>? translate = null)
    {
        string T(string value) => translate?.Invoke(value) ?? value;
        string F(FormattableString value)
        {
            var parts = System.Text.RegularExpressions.Regex.Split(value.Format,
                @"(\{[0-9]+(?:,-?[0-9]+)?(?::[^{}]*)?\})");
            for (var index = 0; index < parts.Length; index += 2) parts[index] = T(parts[index]);
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, string.Concat(parts), value.GetArguments());
        }
        return Unavailable(translate) with
        {
            Plan = F($"Prévia da fixture T3: {plan.Package.PackageFullName}\nPlano T3: {plan.Id}; hash: {plan.PlanHash}\n") +
                T("Este plano não é autorização T3C ou autorização real.\n") + string.Join("\n", plan.Consequences),
            Dependencies = F($"Dependências informadas pela fixture T3: {plan.Package.DependencyStatus}\n") +
                T("Observações diretas, reversas, compartilhadas e efeitos indiretos T3C não foram executados nesta prévia.\n") +
                T(DebloatRemovalMessages.IndirectEffects),
            Reinstallation = plan.CatalogEntry.ReinstallInformation + "\n" + T(DebloatLimits.RecoveryWarning)
        };
    }

    private static string T(Func<string, string>? translate, string value) => translate?.Invoke(value) ?? value;
}
