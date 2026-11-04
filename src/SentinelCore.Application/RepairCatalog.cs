using SentinelCore.Domain;

namespace SentinelCore.Application;

public static class RepairCategories
{
    public const string Integrity = "Integridade do Windows";
    public const string Image = "Imagem do Windows";
    public const string Volumes = "Discos e volumes";
    public const string Recovery = "Ambiente de Recuperação";

    public static IReadOnlyList<string> All { get; } = [Integrity, Image, Volumes, Recovery];

    public static int Rank(string category)
    {
        var index = All.ToList().IndexOf(category);
        return index < 0 ? int.MaxValue : index;
    }
}

/// <summary>
/// Allowlist de argumentos oficiais. O catálogo é a única fonte de argumentos; nenhum texto do usuário,
/// caminho, script ou expressão de shell chega ao executor.
/// </summary>
public static class RepairArgumentPolicy
{
    private static readonly HashSet<string> SfcArguments =
        new(StringComparer.OrdinalIgnoreCase) { "/verifyonly", "/scannow" };

    private static readonly HashSet<string> DismArguments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "/Online", "/Cleanup-Image", "/CheckHealth", "/ScanHealth", "/RestoreHealth"
        };

    private static readonly HashSet<string> ChkdskArguments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            RepairOperationDescriptor.VolumePlaceholder, "/scan", "/f", "/r", "/x", "/spotfix",
            "/offlinescanandfix", "/forceofflinefix"
        };

    private static readonly HashSet<string> ReagentcArguments =
        new(StringComparer.OrdinalIgnoreCase) { "/info", "/enable", "/disable", "/boottore" };

    /// <summary>
    /// Operações que desmontam volume, corrigem offline, alteram o boot ou a configuração de recuperação.
    /// Elas podem existir apenas como documentação indisponível, nunca como operação executável.
    /// </summary>
    public static bool IsAdvancedOnly(RepairTool tool, string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return false;
        return tool switch
        {
            RepairTool.Chkdsk => argument.Equals("/f", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/r", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/x", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/spotfix", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/offlinescanandfix", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/forceofflinefix", StringComparison.OrdinalIgnoreCase),
            RepairTool.Reagentc => argument.Equals("/enable", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/disable", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/boottore", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>Allowlist reduzida para operações realmente executáveis; exclui todo argumento avançado.</summary>
    public static bool IsAllowedForExecutable(RepairTool tool, string? argument) =>
        IsAllowed(tool, argument) && !IsAdvancedOnly(tool, argument);

    public static bool IsAllowed(RepairTool tool, string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return false;
        if (argument.Length > RepairLimits.MaxArgumentChars) return false;
        if (argument.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            return false;
        if (argument.Contains("..", StringComparison.Ordinal) ||
            argument.Contains('"') || argument.Contains('\'') || argument.Contains('\\') ||
            argument.Contains('&') || argument.Contains('|') || argument.Contains(';') ||
            argument.Contains('<') || argument.Contains('>') || argument.Contains('%') ||
            argument.Contains('$') || argument.Contains('`') || argument.Contains('*') ||
            argument.Contains('?') || argument.Contains('=') || argument.Contains('#'))
            return false;
        if (argument.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("cmd", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains(".bat", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains(".cmd", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains(".ps1", StringComparison.OrdinalIgnoreCase))
            return false;

        return tool switch
        {
            RepairTool.Sfc => SfcArguments.Contains(argument),
            RepairTool.Dism => DismArguments.Contains(argument),
            RepairTool.Chkdsk => ChkdskArguments.Contains(argument) ||
                RepairVolumeIdPolicy.IsWellFormed(argument),
            RepairTool.Reagentc => ReagentcArguments.Contains(argument),
            _ => false
        };
    }
}

public sealed class RepairCatalog : IRepairCatalog
{
    public const string DefaultVersion = "r1-windows-repair-1";

    private readonly Dictionary<string, RepairOperationDescriptor> operations;

    public RepairCatalog(string version, IEnumerable<RepairOperationDescriptor> available,
        IEnumerable<RepairOperationDescriptor> documentedOnly)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("Versão do catálogo de reparos é obrigatória.");
        Version = version.Trim();
        var executable = available?.ToList() ?? throw new InvalidDataException("Catálogo de reparos ausente.");
        var documented = documentedOnly?.ToList() ?? [];
        if (executable.Count == 0)
            throw new InvalidDataException("Catálogo de reparos vazio não é permitido.");

        operations = new Dictionary<string, RepairOperationDescriptor>(StringComparer.Ordinal);
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in executable.Concat(documented))
        {
            Validate(operation);
            if (!allIds.Add(operation.Id))
                throw new InvalidDataException($"Operação duplicada no catálogo de reparos: {operation.Id}.");
            if (!operation.IsDocumentedOnly)
                operations.Add(operation.Id, operation);
        }

        All = executable
            .OrderBy(item => RepairCategories.Rank(item.Category))
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        DocumentedOnly = documented
            .OrderBy(item => RepairCategories.Rank(item.Category))
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        ContentHash = RepairHashing.CatalogContentHash(All);
    }

    public string Version { get; }
    public string ContentHash { get; }
    public IReadOnlyList<RepairOperationDescriptor> All { get; }
    public IReadOnlyList<RepairOperationDescriptor> DocumentedOnly { get; }

    public RepairOperationDescriptor Get(string operationId) =>
        TryGet(operationId, out var descriptor) && descriptor is not null
            ? descriptor
            : throw new KeyNotFoundException($"Operação de reparo desconhecida no catálogo fechado: {operationId}.");

    public bool TryGet(string operationId, out RepairOperationDescriptor? descriptor)
    {
        descriptor = null;
        return !string.IsNullOrWhiteSpace(operationId) && operations.TryGetValue(operationId, out descriptor);
    }

    public static RepairCatalog CreateDefault() =>
        new(DefaultVersion, AvailableOperations(), DocumentedOperations());

    private static void Validate(RepairOperationDescriptor operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Id) || operation.Id.Length > 64 ||
            operation.Id.Any(char.IsWhiteSpace))
            throw new InvalidDataException("ID de operação de reparo inválido.");
        if (operation.Version <= 0)
            throw new InvalidDataException($"{operation.Id}: versão deve ser positiva.");
        if (string.IsNullOrWhiteSpace(operation.Category) || string.IsNullOrWhiteSpace(operation.DisplayName))
            throw new InvalidDataException($"{operation.Id}: categoria e nome são obrigatórios.");
        if (string.IsNullOrWhiteSpace(operation.Description) || string.IsNullOrWhiteSpace(operation.ExpectedResult))
            throw new InvalidDataException($"{operation.Id}: descrição e resultado esperado são obrigatórios.");
        if (!Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.Risk) ||
            !Enum.IsDefined(operation.Availability) || !Enum.IsDefined(operation.Tool))
            throw new InvalidDataException($"{operation.Id}: enumeração inválida.");
        if (operation.FixedArguments is null || operation.FixedArguments.Count == 0)
            throw new InvalidDataException($"{operation.Id}: argumentos oficiais ausentes.");
        if (operation.FixedArguments.Count > RepairLimits.MaxArgumentCount)
            throw new InvalidDataException($"{operation.Id}: excesso de argumentos.");
        if (operation.FixedArguments.Any(argument => !RepairArgumentPolicy.IsAllowed(operation.Tool, argument)))
            throw new InvalidDataException($"{operation.Id}: argumento fora da allowlist oficial.");
        if (!operation.IsDocumentedOnly && operation.FixedArguments.Any(argument =>
                !RepairArgumentPolicy.IsAllowedForExecutable(operation.Tool, argument)))
            throw new InvalidDataException($"{operation.Id}: operação executável contém argumento avançado.");
        var advanced = operation.FixedArguments.Any(argument =>
            RepairArgumentPolicy.IsAdvancedOnly(operation.Tool, argument));
        if (advanced && !operation.IsDocumentedOnly)
            throw new InvalidDataException($"{operation.Id}: operação avançada deve permanecer documental e indisponível.");
        if (operation.IsDocumentedOnly && !advanced)
            throw new InvalidDataException($"{operation.Id}: operação documental deve declarar uma operação avançada reconhecida.");
        if (operation.FixedArguments
                .Select(argument => argument.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal).Count() != operation.FixedArguments.Count)
            throw new InvalidDataException($"{operation.Id}: argumento duplicado.");
        if (operation.Consequences is null || operation.PossibleChanges is null ||
            operation.Limitations is null || operation.Consequences.Count == 0 ||
            operation.PossibleChanges.Count == 0 || operation.Limitations.Count == 0)
            throw new InvalidDataException($"{operation.Id}: consequências, alterações e limitações são obrigatórias.");

        var volumeArguments = operation.FixedArguments.Count(argument =>
            string.Equals(argument, RepairOperationDescriptor.VolumePlaceholder, StringComparison.Ordinal));
        if (operation.Tool == RepairTool.Chkdsk)
        {
            if (!operation.RequiresVolume || volumeArguments != 1 ||
                !string.Equals(operation.FixedArguments[0], RepairOperationDescriptor.VolumePlaceholder,
                    StringComparison.Ordinal))
                throw new InvalidDataException($"{operation.Id}: chkdsk exige exatamente um volume validado como primeiro argumento.");
        }
        else if (operation.RequiresVolume || volumeArguments != 0)
        {
            throw new InvalidDataException($"{operation.Id}: operação sem volume não pode declarar volume.");
        }

        if (operation.IsDocumentedOnly)
        {
            if (operation.Availability != RepairAvailability.DocumentedOnly)
                throw new InvalidDataException($"{operation.Id}: operação documental deve estar indisponível.");
            if (operation.Risk is RiskLevel.Low or RiskLevel.Medium)
                throw new InvalidDataException($"{operation.Id}: operação documental deve ser de risco alto ou não validado.");
            if (string.IsNullOrWhiteSpace(operation.DocumentedOnlyReason))
                throw new InvalidDataException($"{operation.Id}: motivo da indisponibilidade é obrigatório.");
        }
        else
        {
            if (operation.Availability != RepairAvailability.Available)
                throw new InvalidDataException($"{operation.Id}: operação executável deve estar disponível.");
            if (operation.Risk is RiskLevel.High or RiskLevel.NotValidated)
                throw new InvalidDataException($"{operation.Id}: risco alto ou não validado não pode ser executável.");
            if (operation.Kind == RepairOperationKind.Repair && !operation.MayChangeSystem)
                throw new InvalidDataException($"{operation.Id}: reparação deve declarar possível alteração do sistema.");
            if (operation.Kind == RepairOperationKind.Repair && operation.CanCancelSafely)
                throw new InvalidDataException($"{operation.Id}: reparação não pode prometer cancelamento seguro.");
        }
    }

    public static IReadOnlyList<string> AvailableIds { get; } =
    [
        SfcVerifyId, SfcRepairId, DismCheckHealthId, DismScanHealthId, DismRestoreHealthId,
        ChkdskCheckId, ChkdskScanId, WinReInfoId
    ];

    public const string SfcVerifyId = "SC-RP-SFC-01";
    public const string SfcRepairId = "SC-RP-SFC-02";
    public const string DismCheckHealthId = "SC-RP-DISM-01";
    public const string DismScanHealthId = "SC-RP-DISM-02";
    public const string DismRestoreHealthId = "SC-RP-DISM-03";
    public const string ChkdskCheckId = "SC-RP-CHK-01";
    public const string ChkdskScanId = "SC-RP-CHK-02";
    public const string WinReInfoId = "SC-RP-WINRE-01";

    private static IReadOnlyList<RepairOperationDescriptor> AvailableOperations() =>
    [
        SfcVerify(),
        SfcRepair(),
        DismCheckHealth(),
        DismScanHealth(),
        DismRestoreHealth(),
        ChkdskCheck(),
        ChkdskScan(),
        WinReInfo()
    ];

    private static IReadOnlyList<RepairOperationDescriptor> DocumentedOperations() =>
    [
        Documented("SC-RP-CHK-90", RepairCategories.Volumes, "chkdsk /f — corrigir erros",
            "Corrige erros no volume; pode desmontar o volume, exigir reinicialização e não possui rollback.",
            RepairTool.Chkdsk, ["{volume}", "/f"],
            "Operação de correção offline não habilitada nesta fase."),
        Documented("SC-RP-CHK-91", RepairCategories.Volumes, "chkdsk /r — localizar setores defeituosos",
            "Executa verificação completa e tenta recuperar setores legíveis; pode durar horas e exigir reinicialização.",
            RepairTool.Chkdsk, ["{volume}", "/r"],
            "Operação de recuperação física não habilitada nesta fase."),
        Documented("SC-RP-CHK-92", RepairCategories.Volumes, "chkdsk /x — forçar desmontagem",
            "Força a desmontagem do volume antes da verificação; pode interromper aplicativos e serviços.",
            RepairTool.Chkdsk, ["{volume}", "/x"],
            "Desmontagem forçada não habilitada nesta fase."),
        Documented("SC-RP-CHK-93", RepairCategories.Volumes, "chkdsk /spotfix — correção pontual",
            "Tenta correção pontual em volume NTFS; requer avaliação específica e não é equivalente a uma verificação completa.",
            RepairTool.Chkdsk, ["{volume}", "/spotfix"],
            "Correção pontual não habilitada nesta fase."),
        Documented("SC-RP-CHK-94", RepairCategories.Volumes, "chkdsk — verificação offline com correção",
            "Executa verificação offline com correção; pode desmontar o volume, exigir reinicialização e não possui rollback convencional.",
            RepairTool.Chkdsk, ["{volume}", "/offlinescanandfix"],
            "Verificação offline com correção não habilitada nesta fase."),
        Documented("SC-RP-CHK-95", RepairCategories.Volumes, "chkdsk — agendamento na inicialização",
            "Agenda correção para a próxima inicialização; altera o fluxo de boot e não possui reversão automática.",
            RepairTool.Chkdsk, ["{volume}", "/f"],
            "Agendamento automático no boot não é implementado nesta fase."),
        Documented("SC-RP-WINRE-90", RepairCategories.Recovery, "reagentc /enable — habilitar WinRE",
            "Habilita o ambiente de recuperação; altera a configuração de recuperação do computador.",
            RepairTool.Reagentc, ["/enable"],
            "Alteração do WinRE não habilitada nesta fase."),
        Documented("SC-RP-WINRE-91", RepairCategories.Recovery, "reagentc /disable — desabilitar WinRE",
            "Desabilita o ambiente de recuperação; pode remover a disponibilidade de recuperação local.",
            RepairTool.Reagentc, ["/disable"],
            "Alteração do WinRE não habilitada nesta fase."),
        Documented("SC-RP-WINRE-92", RepairCategories.Recovery, "reagentc /boottore — reiniciar no WinRE",
            "Configura a próxima inicialização para o ambiente de recuperação; altera o fluxo de boot.",
            RepairTool.Reagentc, ["/boottore"],
            "Reinicialização para WinRE não habilitada nesta fase.")
    ];

    private static RepairOperationDescriptor Documented(string id, string category, string displayName,
        string description, RepairTool tool, IReadOnlyList<string> arguments, string reason) =>
        new(id, 1, category, displayName, description, RepairOperationKind.Repair, RiskLevel.High,
            RepairAvailability.DocumentedOnly, tool, arguments,
            tool == RepairTool.Chkdsk, true, true, true, false, true, true, reason,
            ["Operação documentada para avaliação futura; não executável nesta fase."],
            ["Pode alterar o volume, o fluxo de inicialização ou a configuração de recuperação."],
            [RepairGuidance.NoRollback, RepairGuidance.NoAutomaticRepair,
                "Somente uma etapa futura com autorização específica poderá avaliar esta operação."],
            "Não executada nesta fase.", "Microsoft Learn — documentação oficial da ferramenta");

    private static RepairOperationDescriptor SfcVerify() => new(
        SfcVerifyId, 1, RepairCategories.Integrity, "Verificar arquivos do sistema",
        "Verifica os arquivos protegidos do Windows com sfc /verifyonly sem solicitar reparação.",
        RepairOperationKind.Diagnostic, RiskLevel.Low, RepairAvailability.Available, RepairTool.Sfc,
        ["/verifyonly"], false, true, false, false, false, false, false, null,
        ["A operação apenas verifica; nenhum arquivo protegido é reparado.",
            "A saída pode indicar violações de integridade, mas não identifica a causa de uma falha."],
        ["Nenhuma alteração de arquivos protegidos é esperada; logs internos do Windows podem ser atualizados."],
        [RepairGuidance.OutputLocalization, RepairGuidance.ExitCodeLimitation,
            RepairGuidance.ElevationLimitation, RepairGuidance.CancellationLimitation,
            RepairGuidance.NoRollback],
        "Relatório de violações de integridade, sem reparação.",
        "Microsoft Learn — sfc");

    private static RepairOperationDescriptor SfcRepair() => new(
        SfcRepairId, 1, RepairCategories.Integrity, "Reparar arquivos do sistema",
        "Verifica e tenta reparar arquivos protegidos corrompidos com sfc /scannow.",
        RepairOperationKind.Repair, RiskLevel.Medium, RepairAvailability.Available, RepairTool.Sfc,
        ["/scannow"], false, true, false, true, false, false, false, null,
        ["Tenta reparar arquivos protegidos corrompidos.",
            "Pode não corrigir todas as falhas e não promete recuperação automática.",
            "A conclusão pode exigir reinicialização e a saída é localizada."],
        ["Arquivos protegidos do Windows podem ser alterados.",
            "Logs do Windows podem registrar a operação; uma reinicialização pode ser solicitada pela ferramenta."],
        [RepairGuidance.NoRollback, RepairGuidance.ExitCodeLimitation,
            RepairGuidance.OutputLocalization, RepairGuidance.ElevationLimitation,
            RepairGuidance.CancellationLimitation,
            "A reparação depende de arquivos de origem e do estado do componente; falha parcial permanece possível."],
        "Tentativa de reparação com resultado reportado pela ferramenta; sucesso não é presumido.",
        "Microsoft Learn — sfc");

    private static RepairOperationDescriptor DismCheckHealth() => new(
        DismCheckHealthId, 1, RepairCategories.Image, "Verificação rápida da imagem",
        "Consulta o estado da imagem com DISM /Online /Cleanup-Image /CheckHealth, sem reparar.",
        RepairOperationKind.Diagnostic, RiskLevel.Low, RepairAvailability.Available, RepairTool.Dism,
        ["/Online", "/Cleanup-Image", "/CheckHealth"], false, true, false, false, false, false, false, null,
        ["A verificação rápida usa informações já registradas e não executa uma verificação completa.",
            "Não corrige a imagem e não garante que toda corrupção seja detectada."],
        ["Nenhuma alteração da imagem é esperada; logs do componente podem ser atualizados."],
        [RepairGuidance.OutputLocalization, RepairGuidance.ExitCodeLimitation,
            RepairGuidance.ElevationLimitation, RepairGuidance.NoRollback,
            "CheckHealth pode não detectar corrupção que exigiria ScanHealth ou RestoreHealth."],
        "Estado reportado da imagem, sem reparação.",
        "Microsoft Learn — DISM /Cleanup-Image");

    private static RepairOperationDescriptor DismScanHealth() => new(
        DismScanHealthId, 1, RepairCategories.Image, "Verificação profunda da imagem",
        "Executa uma verificação profunda da imagem com DISM /Online /Cleanup-Image /ScanHealth, sem reparar.",
        RepairOperationKind.Diagnostic, RiskLevel.Low, RepairAvailability.Available, RepairTool.Dism,
        ["/Online", "/Cleanup-Image", "/ScanHealth"], false, true, false, false, false, false, false, null,
        ["A verificação profunda pode ser demorada e consome recursos do componente.",
            "Não corrige a imagem; uma corrupção detectada exigirá avaliação e autorização separadas."],
        ["Nenhuma alteração da imagem é esperada; logs do componente podem ser atualizados."],
        [RepairGuidance.OutputLocalization, RepairGuidance.ExitCodeLimitation,
            RepairGuidance.ElevationLimitation, RepairGuidance.NoRollback,
            "A operação pode não concluir dentro do tempo esperado em imagens grandes ou danificadas."],
        "Relatório de corrupção detectada ou não detectada, sem reparação.",
        "Microsoft Learn — DISM /Cleanup-Image");

    private static RepairOperationDescriptor DismRestoreHealth() => new(
        DismRestoreHealthId, 1, RepairCategories.Image, "Reparar a imagem do Windows",
        "Tenta reparar a imagem com DISM /Online /Cleanup-Image /RestoreHealth.",
        RepairOperationKind.Repair, RiskLevel.Medium, RepairAvailability.Available, RepairTool.Dism,
        ["/Online", "/Cleanup-Image", "/RestoreHealth"], false, true, false, true, false, false, false, null,
        ["Pode usar Windows Update ou uma fonte de reparação disponível no sistema.",
            "Pode falhar por ausência de fonte, acesso negado ou corrupção não reparável.",
            "Pode exigir reinicialização e não garante a correção de todas as falhas."],
        ["A imagem do componente pode ser alterada.",
            "Arquivos de origem e Windows Update podem ser consultados pela ferramenta, sem parâmetros de origem arbitrários nesta fase.",
            "Uma reinicialização pode ser solicitada pela ferramenta."],
        [RepairGuidance.NoRollback, RepairGuidance.OutputLocalization,
            RepairGuidance.ExitCodeLimitation, RepairGuidance.ElevationLimitation,
            RepairGuidance.CancellationLimitation,
            "Não há parâmetro de fonte arbitrária nesta fase; a indisponibilidade de fonte é um resultado possível.",
            "A operação não altera Windows Update, Defender, firewall ou políticas de segurança por conta própria."],
        "Resultado reportado da tentativa de reparação; sucesso não é presumido.",
        "Microsoft Learn — DISM /Cleanup-Image");

    private static RepairOperationDescriptor ChkdskCheck() => new(
        ChkdskCheckId, 1, RepairCategories.Volumes, "Verificar volume",
        "Executa chkdsk <volume> em modo de verificação, somente sobre um volume local validado.",
        RepairOperationKind.Diagnostic, RiskLevel.Low, RepairAvailability.Available, RepairTool.Chkdsk,
        ["{volume}"], true, true, false, false, false, false, false, null,
        ["Examina o volume selecionado e relata problemas; não executa correção.",
            "O volume precisa estar disponível e o sistema de arquivos precisa ser compatível."],
        ["Nenhuma correção é esperada; logs do sistema podem ser atualizados."],
        [RepairGuidance.OutputLocalization, RepairGuidance.ExitCodeLimitation,
            RepairGuidance.ElevationLimitation, RepairGuidance.NoRollback,
            "A verificação online não substitui uma verificação offline; o volume não é desmontado."],
        "Relatório de verificação do volume, sem correção.",
        "Microsoft Learn — chkdsk");

    private static RepairOperationDescriptor ChkdskScan() => new(
        ChkdskScanId, 1, RepairCategories.Volumes, "Verificação online NTFS",
        "Executa chkdsk <volume> /scan em volume NTFS compatível, sem parâmetros livres.",
        RepairOperationKind.Diagnostic, RiskLevel.Medium, RepairAvailability.Available, RepairTool.Chkdsk,
        ["{volume}", "/scan"], true, true, false, true, false, false, false, null,
        ["Executa uma verificação online em volume NTFS.",
            "Não é equivalente a chkdsk /f ou /r e não executa correção offline nesta fase.",
            "O volume precisa estar montado, disponível e com sistema de arquivos NTFS."],
        ["Pode atualizar metadados de verificação e logs do sistema.",
            "Efeitos indiretos no volume não são descartados; não há rollback convencional."],
        [RepairGuidance.NoRollback, RepairGuidance.OutputLocalization,
            RepairGuidance.ExitCodeLimitation, RepairGuidance.ElevationLimitation,
            RepairGuidance.OnlineChkdskLimitation,
            "Uma correção completa pode exigir etapa offline e reinicialização, não implementadas nesta fase."],
        "Relatório de verificação online; problemas podem permanecer sem correção.",
        "Microsoft Learn — chkdsk");

    private static RepairOperationDescriptor WinReInfo() => new(
        WinReInfoId, 1, RepairCategories.Recovery, "Consultar ambiente de recuperação",
        "Consulta o estado do WinRE com reagentc /info, sem habilitar, desabilitar ou reiniciar.",
        RepairOperationKind.Diagnostic, RiskLevel.Low, RepairAvailability.Available, RepairTool.Reagentc,
        ["/info"], false, true, false, false, true, false, false, null,
        ["Consulta o estado do ambiente de recuperação e a identificação da instalação.",
            "Não habilita, desabilita, altera BCD ou reinicia o computador."],
        ["Nenhuma alteração de configuração de recuperação é esperada."],
        [RepairGuidance.OutputLocalization, RepairGuidance.ElevationLimitation,
            "A consulta pode exigir elevação e pode não expor todos os detalhes em todas as versões do Windows.",
            "Não substitui uma validação real do WinRE nem garante que a recuperação funcionará."],
        "Informações técnicas do WinRE, sem alteração de configuração.",
        "Microsoft Learn — reagentc");
}
