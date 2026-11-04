using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Repairs;

public sealed record RepairFixture(
    int? ExitCode,
    IReadOnlyList<string> Lines,
    bool OutputTruncated = false);

/// <summary>
/// Saídas sintéticas representativas das ferramentas oficiais. São fixtures de teste: o executor sempre
/// acrescenta o aviso <see cref="RepairGuidance.SyntheticDisclaimer"/> e o registro final é marcado como
/// sintético. Nenhuma linha é apresentada como saída do Windows real.
/// </summary>
public static class RepairFixtures
{
    private const int DismGenericFailure = unchecked((int)0x80004005);

    public static RepairFixture Get(RepairOperationDescriptor operation, RepairFixtureScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return scenario switch
        {
            RepairFixtureScenario.Cancelled => new(null,
                ["Operation cancelled by user request.", "Cancelamento solicitado antes da conclusão."]),
            RepairFixtureScenario.Interrupted => new(null,
                [Partial(operation), "Output interrupted before completion."]),
            RepairFixtureScenario.IncompleteOutput => new(SuccessExit(operation),
                [SuccessLine(operation)], OutputTruncated: true),
            RepairFixtureScenario.AccessDenied => new(5,
                ["Access is denied.", "Acesso negado. The operation requires elevation."]),
            RepairFixtureScenario.UnsupportedSystem => new(RepairExitCodes.DismNotSupported,
                ["The operation is not supported on this system.", "A operação não é suportada neste sistema."]),
            RepairFixtureScenario.RepairSourceUnavailable => SourceUnavailable(operation),
            RepairFixtureScenario.RestartRequired => RestartRequired(operation),
            RepairFixtureScenario.VolumeUnavailable => new(3,
                ["Cannot open volume for direct access.", "Não é possível abrir o volume para acesso direto."]),
            RepairFixtureScenario.IncompatibleFileSystem => new(3,
                ["The type of the file system is not NTFS.", "O tipo do sistema de arquivos não é NTFS.",
                    "This operation is not supported for the current file system."]),
            RepairFixtureScenario.ToolError => Failure(operation),
            RepairFixtureScenario.RepairFailed => Failure(operation),
            RepairFixtureScenario.RepairPartial => PartialFixture(operation),
            RepairFixtureScenario.CorruptionFound => CorruptionFound(operation),
            RepairFixtureScenario.CorruptionNotFound => CorruptionNotFound(operation),
            RepairFixtureScenario.SuccessRepairsCompleted => RepairCompleted(operation),
            RepairFixtureScenario.WinReEnabled => WinRe(operation, true),
            RepairFixtureScenario.WinReDisabled => WinRe(operation, false),
            _ => Success(operation)
        };
    }

    private static RepairFixture Success(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => new(0, operation.Kind == RepairOperationKind.Repair
            ? ["Windows Resource Protection did not find any integrity violations."]
            : ["Windows Resource Protection did not find any integrity violations.",
                "A Proteção de Recursos do Windows não encontrou violações de integridade."]),
        RepairTool.Dism => new(0, ["No component store corruption detected.",
            "The operation completed successfully.", "Nenhuma corrupção do repositório de componentes foi detectada."]),
        RepairTool.Chkdsk => new(0, ["Windows has scanned the file system and found no problems.",
            "No further action is required.", "O Windows examinou o sistema de arquivos e não encontrou problemas."]),
        RepairTool.Reagentc => WinRe(operation, true),
        _ => new(0, ["Synthetic success."])
    };

    private static RepairFixture RepairCompleted(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => new(1, ["Windows Resource Protection found corrupt files and successfully repaired them.",
            "A Proteção de Recursos do Windows encontrou arquivos corrompidos e os reparou com êxito."]),
        RepairTool.Dism => new(0, ["The restore operation completed successfully.",
            "Repairs have been completed successfully.", "As reparações foram concluídas."]),
        RepairTool.Chkdsk => new(0, ["Windows has scanned the file system and found no problems.",
            "No further action is required."]),
        RepairTool.Reagentc => WinRe(operation, true),
        _ => new(0, ["Synthetic success."])
    };

    private static RepairFixture CorruptionFound(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => operation.Kind == RepairOperationKind.Repair
            ? new(2, ["Windows Resource Protection found corrupt files but was unable to fix some of them.",
                "A Proteção de Recursos do Windows encontrou arquivos corrompidos, mas não conseguiu corrigir alguns."])
            : new(1, ["Windows Resource Protection found corrupt files.",
                "A Proteção de Recursos do Windows encontrou arquivos corrompidos."]),
        RepairTool.Dism => new(0, ["The component store is repairable.",
            "O repositório de componentes é reparável.", "Corruption was detected in the component store."]),
        RepairTool.Chkdsk => new(1, ["Windows found problems with the file system.",
            "O Windows encontrou problemas com o sistema de arquivos."]),
        RepairTool.Reagentc => new(0, ["Windows Recovery Environment (Windows RE) information.",
            "WinRE status: Disabled.", "Ambiente de recuperação: desabilitado."]),
        _ => new(1, ["Synthetic corruption detected."])
    };

    private static RepairFixture CorruptionNotFound(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => new(0, ["Windows Resource Protection did not find any integrity violations."]),
        RepairTool.Dism => new(0, ["No component store corruption detected.",
            "Nenhuma corrupção do repositório de componentes foi detectada."]),
        RepairTool.Chkdsk => new(0, ["Windows has scanned the file system and found no problems."]),
        RepairTool.Reagentc => WinRe(operation, true),
        _ => new(0, ["Synthetic no corruption."])
    };

    private static RepairFixture PartialFixture(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => new(2, ["Windows Resource Protection found corrupt files but was unable to fix some of them."]),
        RepairTool.Dism => new(0, ["The operation completed with errors.",
            "Some repairs could not be completed.", "A operação foi concluída com erros."]),
        RepairTool.Chkdsk => new(3, ["Some errors could not be fixed.",
            "Alguns erros não puderam ser corrigidos."]),
        RepairTool.Reagentc => new(1, ["The operation completed with errors."]),
        _ => new(2, ["Synthetic partial repair."])
    };

    private static RepairFixture Failure(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => new(3, ["Windows Resource Protection could not perform the requested operation.",
            "A Proteção de Recursos do Windows não pôde executar a operação solicitada."]),
        RepairTool.Dism => new(DismGenericFailure, ["The operation failed.", "An error occurred."]),
        RepairTool.Chkdsk => new(3, ["The operation could not be completed.", "An error occurred while checking the volume."]),
        RepairTool.Reagentc => new(1, ["The operation failed."]),
        _ => new(1, ["Synthetic failure."])
    };

    private static RepairFixture SourceUnavailable(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Dism => new(RepairExitCodes.DismSourceNotFound,
            ["The source files could not be found.",
                "Os arquivos de origem não puderam ser encontrados.",
                "Use the Source option to specify the location of the files."]),
        RepairTool.Sfc => new(2, ["The source files could not be found.",
            "Repairs could not be completed because the source files are unavailable."]),
        _ => new(3, ["The required repair source is unavailable."])
    };

    private static RepairFixture RestartRequired(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Chkdsk => new(2, ["Windows has made corrections to the file system.",
            "A restart is required to complete the repair.", "Reinicialização necessária."]),
        _ => new(0, [SuccessLine(operation), "A restart is required to complete the repair.",
            "Reinicialização necessária para concluir a operação."])
    };

    private static RepairFixture WinRe(RepairOperationDescriptor operation, bool enabled)
    {
        _ = operation;
        return enabled
            ? new(0, ["Windows Recovery Environment (Windows RE) and system reset configuration Information:",
                "    Windows RE status:         Enabled",
                "    Windows RE location:       \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE",
                "    Boot Configuration Data (BCD) identifier: {00000000-0000-0000-0000-000000000000}",
                "Ambiente de recuperação do Windows: habilitado."])
            : new(0, ["Windows Recovery Environment (Windows RE) and system reset configuration Information:",
                "    Windows RE status:         Disabled",
                "    Windows RE location:       (none)",
                "Ambiente de recuperação do Windows: desabilitado."]);
    }

    private static int? SuccessExit(RepairOperationDescriptor operation) => 0;

    private static string SuccessLine(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => "Windows Resource Protection did not find any integrity violations.",
        RepairTool.Dism => "No component store corruption detected.",
        RepairTool.Chkdsk => "Windows has scanned the file system and found no problems.",
        RepairTool.Reagentc => "Windows Recovery Environment (Windows RE) information.",
        _ => "Synthetic success."
    };

    private static string Partial(RepairOperationDescriptor operation) => operation.Tool switch
    {
        RepairTool.Sfc => "Windows Resource Protection found corrupt files.",
        RepairTool.Dism => "The component store is repairable.",
        RepairTool.Chkdsk => "Windows found problems with the file system.",
        RepairTool.Reagentc => "Windows Recovery Environment (Windows RE) information.",
        _ => "Synthetic partial output."
    };
}
