using System.Text;
using System.Text.RegularExpressions;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed record RepairInterpretation(
    RepairResultClassification Classification,
    RepairConfidence Confidence,
    RepairEvidence Evidence,
    string Summary,
    bool RequiresRestart,
    bool RequiresIntervention,
    IReadOnlyList<string> Limitations);

public sealed record RepairLimitedOutput(
    IReadOnlyList<RepairOutputLine> Lines,
    int Bytes,
    bool Truncated,
    int OriginalLineCount);

/// <summary>
/// Interpreta código de saída e marcadores estruturados. Uma frase localizada nunca é a única prova de
/// sucesso: o código é primário, o marcador é auxiliar e o resultado declara a limitação correspondente.
/// </summary>
public static class RepairResultInterpreter
{
    public static RepairInterpretation Interpret(RepairOperationDescriptor operation,
        RepairExecutionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(outcome);
        var text = string.Join("\n", outcome.Output.Select(line => line.Text)).ToLowerInvariant();
        var limitations = new List<string>(outcome.Limitations) { RepairGuidance.OutputLocalization };

        if (outcome.Cancelled)
        {
            limitations.Add(RepairGuidance.CancellationLimitation);
            return new(RepairResultClassification.Cancelled, RepairConfidence.High,
                RepairEvidence.Empty, "Cancelamento solicitado. A interrupção segura da ferramenta oficial não é garantida.",
                false, true, limitations.Distinct().ToList());
        }

        if (outcome.Interrupted || !outcome.Completed)
        {
            limitations.Add("A execução não foi concluída; o resultado permanece indeterminado.");
            return new(RepairResultClassification.Interrupted, RepairConfidence.Low,
                RepairEvidence.Empty, "Execução interrompida ou não concluída; sucesso não é declarado.",
                false, true, limitations.Distinct().ToList());
        }

        if (outcome.OutputTruncated)
        {
            limitations.Add("A saída foi limitada; o resultado pode estar incompleto.");
            var partial = InterpretExitCode(operation, outcome.ExitCode, text, limitations);
            return partial with
            {
                Classification = RepairResultClassification.OutputIncomplete,
                Confidence = RepairConfidence.Low,
                RequiresIntervention = true,
                Limitations = partial.Limitations.Concat(["Saída truncada pelo limite local."]).Distinct().ToList()
            };
        }

        if (outcome.ExitCode is null)
        {
            limitations.Add("A ferramenta não forneceu código de saída verificável.");
            return new(RepairResultClassification.UnknownExitCode, RepairConfidence.Low,
                RepairEvidence.Empty, "Código de saída indisponível; resultado não avaliado.",
                false, true, limitations.Distinct().ToList());
        }

        var interpretation = InterpretExitCode(operation, outcome.ExitCode, text, limitations);
        return ApplyMarkers(operation, interpretation, text, limitations);
    }

    private static RepairInterpretation InterpretExitCode(RepairOperationDescriptor operation,
        int? exitCode, string text, List<string> limitations)
    {
        var code = exitCode ?? int.MinValue;
        if (code is 5 or RepairExitCodes.DismAccessDenied)
            return new(RepairResultClassification.AccessDenied, RepairConfidence.High,
                RepairEvidence.Empty with { AccessDenied = true },
                "Acesso negado pela ferramenta; a execução real exigiria elevação.",
                false, true, limitations.Distinct().ToList());
        if (code == RepairExitCodes.DismNotSupported)
            return new(RepairResultClassification.UnsupportedSystem, RepairConfidence.High,
                RepairEvidence.Empty, "A ferramenta relatou operação não suportada neste sistema.",
                false, true, limitations.Distinct().ToList());

        return operation.Tool switch
        {
            RepairTool.Sfc => Sfc(operation, code, text, limitations),
            RepairTool.Dism => Dism(operation, code, text, limitations),
            RepairTool.Chkdsk => Chkdsk(operation, code, limitations),
            RepairTool.Reagentc => Reagentc(operation, code, text, limitations),
            _ => Unknown("Ferramenta não catalogada.", limitations)
        };
    }

    private static RepairInterpretation Sfc(RepairOperationDescriptor operation, int code, string text,
        List<string> limitations)
    {
        var repair = operation.Kind == RepairOperationKind.Repair;
        var noViolations = Has(text, "did not find any integrity violations",
            "não encontrou violações de integridade", "nao encontrou violacoes de integridade");
        var repaired = Has(text, "successfully repaired", "reparou com êxito", "reparou com exito");
        var unable = Has(text, "unable to fix some", "não conseguiu corrigir alguns",
            "nao conseguiu corrigir alguns", "não pôde corrigir alguns", "nao pode corrigir alguns");

        if (code == 0)
        {
            if (unable) return Partial("A ferramenta relatou arquivos que não puderam ser reparados.", limitations);
            if (repaired) return Completed("A ferramenta relatou reparação concluída.", limitations);
            return new(RepairResultClassification.NoIssuesReported,
                noViolations ? RepairConfidence.High : RepairConfidence.Medium,
                RepairEvidence.Empty,
                repair
                    ? "Nenhuma violação de integridade foi relatada pela ferramenta."
                    : "Nenhuma violação de integridade foi relatada pela verificação.",
                false, false, limitations.Distinct().ToList());
        }
        if (code == 1)
        {
            return repair
                ? Completed("A ferramenta relatou reparação de arquivos protegidos.", limitations)
                : new(RepairResultClassification.CorruptionDetected, RepairConfidence.High,
                    RepairEvidence.Empty with { CorruptionDetected = true },
                    "Violações de integridade foram encontradas; nenhuma reparação foi solicitada.",
                    false, true, limitations.Distinct().ToList());
        }
        if (code == 2)
        {
            return repair
                ? Partial("A ferramenta não conseguiu reparar todos os arquivos ou componentes afetados.", limitations)
                : new(RepairResultClassification.RepairFailed, RepairConfidence.High,
                    RepairEvidence.Empty, "A verificação não pôde ser concluída pela ferramenta.",
                    false, true, limitations.Distinct().ToList());
        }
        if (code == 3)
        {
            return new(RepairResultClassification.RepairFailed, RepairConfidence.High,
                RepairEvidence.Empty, "A ferramenta não pôde executar a operação solicitada.",
                false, true, limitations.Distinct().ToList());
        }
        return Unknown($"Código de saída SFC não catalogado: {code}.", limitations);
    }

    private static RepairInterpretation Dism(RepairOperationDescriptor operation, int code, string text,
        List<string> limitations)
    {
        if (code is RepairExitCodes.DismSourceNotFound or RepairExitCodes.DismSourceFilesNotFound)
        {
            return new(RepairResultClassification.RepairSourceUnavailable, RepairConfidence.High,
                RepairEvidence.Empty with { RepairSourceUnavailable = true },
                "A reparação não encontrou uma fonte de arquivos de origem disponível.",
                false, true, limitations.Distinct().ToList());
        }
        if (code is RepairExitCodes.DismAccessDenied or 5)
        {
            return new(RepairResultClassification.AccessDenied, RepairConfidence.High,
                RepairEvidence.Empty with { AccessDenied = true },
                "Acesso negado pela ferramenta; a execução real exigiria elevação.",
                false, true, limitations.Distinct().ToList());
        }
        if (code == RepairExitCodes.DismGenericFailure)
            return new(RepairResultClassification.RepairFailed, RepairConfidence.Medium,
                RepairEvidence.Empty, "A ferramenta relatou falha genérica na operação.", false, true,
                limitations.Distinct().ToList());
        if (code != 0)
            return Unknown($"Código de saída DISM/HRESULT não catalogado: {RepairExitCodes.Display(code)}.", limitations);

        var noCorruption = Has(text, "no component store corruption detected",
            "nenhuma corrupção do repositório de componentes", "nenhuma corrupcao do repositorio de componentes",
            "não foram detectadas corrupções", "nao foram detectadas corrupcoes");
        var repairable = Has(text, "component store is repairable", "repositório de componentes é reparável",
            "repositorio de componentes e reparavel", "a imagem é reparável", "a imagem e reparavel");
        var repaired = Has(text, "repairs have been completed", "as reparações foram concluídas",
            "as reparacoes foram concluidas", "operation completed successfully",
            "operação concluída com êxito", "operacao concluida com exito");

        if (operation.Kind == RepairOperationKind.Repair)
        {
            if (repaired)
                return Completed("A ferramenta relatou conclusão da tentativa de reparação da imagem.", limitations);
            limitations.Add("Código de saída zero não prova que toda corrupção foi corrigida.");
            return new(RepairResultClassification.RepairCompleted, RepairConfidence.Medium,
                RepairEvidence.Empty with { RepairCompleted = true },
                "Código de saída zero para RestoreHealth; a conclusão não prova que toda corrupção foi corrigida.",
                false, false, limitations.Distinct().ToList());
        }

        if (repairable)
            return new(RepairResultClassification.CorruptionDetected, RepairConfidence.High,
                RepairEvidence.Empty with { CorruptionDetected = true },
                "A imagem foi reportada como reparável; corrupção detectada.", false, true,
                limitations.Distinct().ToList());
        if (noCorruption)
            return new(RepairResultClassification.NoCorruptionDetected, RepairConfidence.High,
                RepairEvidence.Empty with { NoCorruptionDetected = true },
                "Nenhuma corrupção do repositório de componentes foi detectada.", false, false,
                limitations.Distinct().ToList());
        return new(RepairResultClassification.NoCorruptionDetected, RepairConfidence.Low,
            RepairEvidence.Empty with { NoCorruptionDetected = true },
            "Código de saída zero, sem marcador textual reconhecido; a ausência de corrupção não é conclusiva.",
            false, false, limitations.Distinct().ToList());
    }

    private static RepairInterpretation Chkdsk(RepairOperationDescriptor operation, int code,
        List<string> limitations)
    {
        return code switch
        {
            0 => new(RepairResultClassification.NoIssuesReported, RepairConfidence.High,
                RepairEvidence.Empty, "A ferramenta não relatou problemas no volume.",
                false, false, limitations.Distinct().ToList()),
            1 => operation.Kind == RepairOperationKind.Diagnostic
                ? new(RepairResultClassification.CorruptionDetected, RepairConfidence.High,
                    RepairEvidence.Empty with { CorruptionDetected = true },
                    "A ferramenta relatou erros no volume durante a verificação.",
                    false, true, limitations.Distinct().ToList())
                : new(RepairResultClassification.RepairCompleted, RepairConfidence.High,
                    RepairEvidence.Empty with { RepairCompleted = true },
                    "A ferramenta relatou correção de erros no volume.", false, false,
                    limitations.Distinct().ToList()),
            2 => new(RepairResultClassification.RestartRequired, RepairConfidence.Medium,
                RepairEvidence.Empty with
                {
                    RestartRequired = true,
                    RepairCompleted = operation.Kind == RepairOperationKind.Repair
                },
                "A ferramenta relatou limpeza ou necessidade de reinicialização; confirme o estado do volume.",
                true, true, limitations.Distinct().ToList()),
            3 => new(RepairResultClassification.RepairFailed, RepairConfidence.High,
                RepairEvidence.Empty, "A ferramenta não pôde verificar o volume.", false, true,
                limitations.Distinct().ToList()),
            _ => Unknown($"Código de saída chkdsk não catalogado: {code}.", limitations)
        };
    }

    private static RepairInterpretation Reagentc(RepairOperationDescriptor operation, int code, string text,
        List<string> limitations)
    {
        _ = operation;
        if (code != 0)
        {
            if (Has(text, "access is denied", "acesso negado"))
                return new(RepairResultClassification.AccessDenied, RepairConfidence.High,
                    RepairEvidence.Empty with { AccessDenied = true },
                    "Acesso negado ao consultar o ambiente de recuperação.", false, true,
                    limitations.Distinct().ToList());
            return new(RepairResultClassification.RepairFailed, RepairConfidence.Medium,
                RepairEvidence.Empty, "A consulta ao ambiente de recuperação não foi concluída.",
                false, true, limitations.Distinct().ToList());
        }

        bool? enabled = null;
        if (Has(text, "windows recovery environment", "ambiente de recuperação", "ambiente de recuperacao", "winre"))
        {
            if (Has(text, "enabled", "habilitado", "habilitada")) enabled = true;
            else if (Has(text, "disabled", "desabilitado", "desabilitada")) enabled = false;
        }
        return new(RepairResultClassification.InformationRetrieved,
            enabled is null ? RepairConfidence.Medium : RepairConfidence.High,
            RepairEvidence.Empty with { WinReEnabled = enabled },
            enabled switch
            {
                true => "Informações do WinRE recuperadas; o ambiente de recuperação aparece habilitado.",
                false => "Informações do WinRE recuperadas; o ambiente de recuperação aparece desabilitado.",
                _ => "Informações do WinRE recuperadas; o estado habilitado/desabilitado não foi reconhecido."
            },
            false, false, limitations.Distinct().ToList());
    }

    private static RepairInterpretation ApplyMarkers(RepairOperationDescriptor operation,
        RepairInterpretation interpretation, string text, List<string> limitations)
    {
        var result = interpretation;
        if (Has(text, "not ntfs", "não é ntfs", "nao e ntfs", "file system is not ntfs") &&
            result.Classification is RepairResultClassification.RepairFailed
                or RepairResultClassification.UnknownExitCode)
        {
            limitations.Add("O texto indica sistema de arquivos incompatível.");
            result = result with
            {
                Classification = RepairResultClassification.IncompatibleFileSystem,
                Confidence = RepairConfidence.Medium,
                RequiresIntervention = true
            };
        }
        if (Has(text, "cannot open volume", "não é possível abrir o volume",
                "nao e possivel abrir o volume") &&
            result.Classification is RepairResultClassification.RepairFailed
                or RepairResultClassification.UnknownExitCode)
        {
            limitations.Add("O texto indica volume indisponível para verificação.");
            result = result with
            {
                Classification = RepairResultClassification.VolumeUnavailable,
                Confidence = RepairConfidence.Medium,
                RequiresIntervention = true
            };
        }
        if (Has(text, "found problems with the file system", "encontrou problemas com o sistema de arquivos") &&
            operation.Kind == RepairOperationKind.Diagnostic &&
            result.Classification is not RepairResultClassification.CorruptionDetected)
        {
            result = result with
            {
                Classification = RepairResultClassification.CorruptionDetected,
                Confidence = RepairConfidence.Medium,
                Evidence = result.Evidence with { CorruptionDetected = true },
                RequiresIntervention = true
            };
        }
        if (Has(text, "completed with errors", "concluída com erros", "concluida com erros",
                "could not be fixed", "não puderam ser corrigidos", "nao puderam ser corrigidos") &&
            result.Classification is RepairResultClassification.RepairCompleted
                or RepairResultClassification.NoIssuesReported
                or RepairResultClassification.RepairFailed)
        {
            limitations.Add("O texto indica conclusão com erros ou correção parcial.");
            result = result with
            {
                Classification = RepairResultClassification.RepairPartial,
                Confidence = RepairConfidence.Medium,
                Evidence = result.Evidence with { RepairPartial = true },
                RequiresIntervention = true,
                Summary = "A ferramenta relatou reparação parcial; algumas falhas podem permanecer."
            };
        }
        if (Has(text, "access is denied", "acesso negado") &&
            result.Classification != RepairResultClassification.AccessDenied)
        {
            limitations.Add("O texto contém indicação de acesso negado que diverge da classificação primária.");
            result = result with
            {
                Classification = RepairResultClassification.AccessDenied,
                Confidence = RepairConfidence.Medium,
                Evidence = result.Evidence with { AccessDenied = true },
                RequiresIntervention = true
            };
        }
        if (Has(text, "source files could not be found", "arquivos de origem não puderam ser encontrados",
                "arquivos de origem nao puderam ser encontrados") &&
            result.Classification != RepairResultClassification.RepairSourceUnavailable)
        {
            limitations.Add("O texto indica ausência de fonte de reparação.");
            result = result with
            {
                Classification = RepairResultClassification.RepairSourceUnavailable,
                Confidence = RepairConfidence.Medium,
                Evidence = result.Evidence with { RepairSourceUnavailable = true },
                RequiresIntervention = true
            };
        }
        if (Has(text, "unable to fix some", "não conseguiu corrigir alguns",
                "nao conseguiu corrigir alguns", "não pôde corrigir alguns", "nao pode corrigir alguns") &&
            result.Classification is RepairResultClassification.RepairCompleted
                or RepairResultClassification.NoIssuesReported)
        {
            result = result with
            {
                Classification = RepairResultClassification.RepairPartial,
                Confidence = RepairConfidence.Medium,
                Evidence = result.Evidence with { RepairPartial = true },
                RequiresIntervention = true,
                Summary = "A ferramenta relatou reparação parcial; algumas falhas podem permanecer."
            };
        }
        if (Has(text, "restart", "reiniciar", "reboot", "reinicialização", "reinicializacao"))
        {
            result = result with
            {
                RequiresRestart = true,
                Evidence = result.Evidence with { RestartRequired = true }
            };
        }
        return result with { Limitations = limitations.Distinct().ToList() };
    }

    private static bool Has(string text, params string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    private static RepairInterpretation Completed(string summary, List<string> limitations) =>
        new(RepairResultClassification.RepairCompleted, RepairConfidence.High,
            RepairEvidence.Empty with { RepairCompleted = true }, summary, false, false,
            limitations.Distinct().ToList());

    private static RepairInterpretation Partial(string summary, List<string> limitations) =>
        new(RepairResultClassification.RepairPartial, RepairConfidence.Medium,
            RepairEvidence.Empty with { RepairPartial = true }, summary, false, true,
            limitations.Distinct().ToList());

    private static RepairInterpretation Unknown(string summary, List<string> limitations) =>
        new(RepairResultClassification.UnknownExitCode, RepairConfidence.Low,
            RepairEvidence.Empty, summary, false, true, limitations.Distinct().ToList());
}

/// <summary>
/// Limita e sanitiza a saída antes de persistir ou apresentar: tamanho, linhas, controles e caminhos do
/// perfil do usuário. Nenhum conteúdo é enviado para fora do computador.
/// </summary>
public static class RepairOutputLimiter
{
    private static readonly Regex UserProfilePattern = new(
        @"[A-Za-z]:\\Users\\[^\\\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RepairLimitedOutput Limit(IEnumerable<string> lines, string profilePath)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(profilePath);
        var source = lines.ToList();
        var limited = new List<RepairOutputLine>();
        var bytes = 0;
        var truncated = false;
        long sequence = 0;

        foreach (var raw in source)
        {
            sequence++;
            var text = Sanitize(raw, profilePath, out var lineTruncated);
            if (lineTruncated) truncated = true;
            if (limited.Count >= RepairLimits.MaxOutputLines)
            {
                truncated = true;
                break;
            }
            var lineBytes = Encoding.UTF8.GetByteCount(text);
            if (bytes + lineBytes > RepairLimits.MaxOutputBytes)
            {
                truncated = true;
                break;
            }
            limited.Add(new RepairOutputLine(sequence, text));
            bytes += lineBytes;
        }
        if (sequence < source.Count) truncated = true;
        return new RepairLimitedOutput(limited, bytes, truncated, source.Count);
    }

    public static string Sanitize(string? line, string? profilePath = null) =>
        Sanitize(line, profilePath, out _);

    public static string Sanitize(string? line, string? profilePath, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(line)) return string.Empty;
        var text = line.Replace("\r", " ").Replace("\n", " ").Trim();
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '\t' || !char.IsControl(character)) builder.Append(character);
        }
        var sanitized = builder.ToString();
        if (!string.IsNullOrWhiteSpace(profilePath))
            sanitized = sanitized.Replace(profilePath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        sanitized = UserProfilePattern.Replace(sanitized, "%USERPROFILE%");
        if (sanitized.Length > RepairLimits.MaxOutputLineChars)
        {
            sanitized = sanitized[..(RepairLimits.MaxOutputLineChars - 1)] + "…";
            truncated = true;
        }
        return sanitized;
    }

    public static string Stored(RepairLimitedOutput output) =>
        output.Lines.Count == 0 ? "Nenhuma saída registrada." : string.Join("\n", output.Lines.Select(line => line.Text));
}
