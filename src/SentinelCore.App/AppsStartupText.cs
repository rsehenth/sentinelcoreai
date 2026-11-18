using SentinelCore.Domain;

namespace SentinelCore.App;

/// <summary>
/// Textos do módulo Aplicativos e Inicialização (português como chave). Cada texto é registrado em <see cref="Entries"/>,
/// que <c>Strings.Entries</c> anexa ao final da tabela: os índices T### existentes não mudam.
/// </summary>
internal static class AppsStartupText
{
    private static readonly List<(string Pt, string En)> entries = [];
    private static string T(string pt, string en) { entries.Add((pt, en)); return pt; }

    public static (string Pt, string En)[] Entries { get { _ = Title; return [.. entries]; } }

    // Navegação e cabeçalhos
    public static readonly string Title = T("Aplicativos e Inicialização", "Apps and Startup");
    public static readonly string NavTooltip = T("Inicialização do Windows e aplicativos instalados", "Windows startup and installed apps");
    public static readonly string Subtitle = T("Veja o que inicia com o Windows e o que está instalado. Nenhuma ação real é executada nesta distribuição.", "See what starts with Windows and what is installed. No real action runs in this distribution.");
    public static readonly string OpenTitle = T("Abrir Aplicativos e Inicialização", "Open Apps and Startup");
    public static readonly string OpenPersonalization = T("Abrir Personalização", "Open Personalization");
    public static readonly string StartupTabHeader = T("Inicialização do Windows", "Windows startup");
    public static readonly string AppsTabHeader = T("Aplicativos instalados", "Installed apps");
    public static readonly string Refresh = T("Atualizar inventário", "Refresh inventory");
    public static readonly string DemoBadge = T("MODO DEMONSTRATIVO", "DEMO MODE");
    public static readonly string GateClosed = T("Os itens abaixo são dados de demonstração: não representam os aplicativos nem a inicialização deste computador. Ações reais estão desabilitadas nesta distribuição; as simulações não alteram o Windows.", "The items below are demonstration data: they do not represent this computer's apps or startup entries. Real actions are disabled in this distribution; simulations do not change Windows.");
    public static readonly string StartupTabHint = T("Entradas que iniciam junto com o Windows ou com o seu perfil. Desativar uma entrada não desinstala o aplicativo.", "Entries that start with Windows or your profile. Disabling an entry does not uninstall the app.");
    public static readonly string AppsTabHint = T("Aplicativos instalados (MSI, MSIX/Store e Win32). A ação prevista é a desinstalação individual; itens sem remoção comprovada ficam indisponíveis.", "Installed apps (MSI, MSIX/Store and Win32). The planned action is individual uninstall; items without a proven removal stay unavailable.");
    public static readonly string StartupGridName = T("Entradas de inicialização do Windows", "Windows startup entries");
    public static readonly string AppsGridName = T("Aplicativos instalados", "Installed apps");
    public static readonly string DetailsName = T("Detalhes do item selecionado", "Selected item details");
    public static readonly string PreviewName = T("Prévia da operação simulada", "Simulated operation preview");
    public static readonly string DataIsSynthetic = T("Dados sintéticos: nenhuma consulta ao Windows foi feita.", "Synthetic data: no Windows query was made.");
    public static readonly string NotLoaded = T("Inventário ainda não carregado.", "Inventory not loaded yet.");
    public static readonly string Loading = T("Carregando inventário sintético…", "Loading synthetic inventory…");
    public static readonly string Unavailable = T("Módulo indisponível nesta composição.", "Module unavailable in this composition.");
    public static readonly string LoadFailed = T("Não foi possível carregar o inventário.", "The inventory could not be loaded.");
    public static readonly string IncompleteWarning = T("Inventário incompleto ou com avisos: a ausência de um item não é confirmada.", "Inventory incomplete or with warnings: the absence of an item is not confirmed.");
    public static readonly string EmptyStartup = T("Nenhuma entrada de inicialização carregada.", "No startup entry loaded.");
    public static readonly string EmptyApps = T("Nenhum aplicativo instalado carregado.", "No installed app loaded.");
    public static readonly string NoMatches = T("Nenhum item corresponde aos filtros.", "No item matches the filters.");
    public static readonly string SelectItem = T("Selecione um item para ver os detalhes.", "Select an item to see its details.");

    // Filtros
    public static readonly string Search = T("Pesquisar por nome", "Search by name");
    public static readonly string FilterAll = T("Todos", "All");
    public static readonly string FilterSource = T("Origem", "Source");
    public static readonly string FilterState = T("Estado", "State");
    public static readonly string FilterType = T("Tipo", "Type");
    public static readonly string FilterScope = T("Escopo", "Scope");

    // Colunas
    public static readonly string ColName = T("Nome", "Name");
    public static readonly string ColPublisher = T("Fabricante", "Publisher");
    public static readonly string ColVersion = T("Versão", "Version");
    public static readonly string ColEligibility = T("Operação", "Operation");
    public static readonly string ColRuntime = T("Execução", "Execution");
    public static readonly string ColDate = T("Data (local)", "Date (local)");
    public static readonly string ColAction = T("Ação", "Action");
    public static readonly string ColResult = T("Resultado", "Result");
    public static readonly string HistOriginDemo = T("Demonstração (sintético)", "Demonstration (synthetic)");
    public static readonly string HistOriginOperational = T("Operacional (não reverificado)", "Operational (not re-verified)");

    // Origens / tipos
    public static string Source(StartupSourceKind kind) => kind switch
    {
        StartupSourceKind.RegistryRun => SrcRun,
        StartupSourceKind.StartupFolder => SrcFolder,
        StartupSourceKind.PackagedStartupTask => SrcPackaged,
        StartupSourceKind.ScheduledTask => SrcTask,
        StartupSourceKind.RegistryRunOnce => SrcRunOnce,
        StartupSourceKind.Service => SrcService,
        StartupSourceKind.Driver => SrcDriver,
        StartupSourceKind.SecurityComponent => SrcSecurity,
        StartupSourceKind.WindowsInternalTask => SrcInternal,
        _ => SrcOther
    };
    private static readonly string SrcRun = T("Registro (Run)", "Registry (Run)");
    private static readonly string SrcFolder = T("Pasta Inicializar", "Startup folder");
    private static readonly string SrcPackaged = T("Tarefa de inicialização empacotada", "Packaged startup task");
    private static readonly string SrcTask = T("Tarefa agendada", "Scheduled task");
    private static readonly string SrcRunOnce = T("Registro (RunOnce)", "Registry (RunOnce)");
    private static readonly string SrcService = T("Serviço", "Service");
    private static readonly string SrcDriver = T("Driver", "Driver");
    private static readonly string SrcSecurity = T("Componente de segurança", "Security component");
    private static readonly string SrcInternal = T("Tarefa interna do Windows", "Windows internal task");
    private static readonly string SrcOther = T("Outra origem", "Other source");

    public static string Scope(AppsScope scope) => scope == AppsScope.CurrentUser ? ScopeUser : ScopeMachine;
    private static readonly string ScopeUser = T("Usuário", "User");
    private static readonly string ScopeMachine = T("Máquina", "Machine");

    public static string State(StartupConfiguredState state) => state switch
    {
        StartupConfiguredState.Enabled => StateEnabled,
        StartupConfiguredState.Disabled => StateDisabled,
        _ => StateUnknown
    };
    private static readonly string StateEnabled = T("Habilitada", "Enabled");
    private static readonly string StateDisabled = T("Desabilitada", "Disabled");
    private static readonly string StateUnknown = T("Estado não verificado", "State not verified");

    public static string Runtime(StartupRuntimeState state) => state switch
    {
        StartupRuntimeState.Running => RunRunning,
        StartupRuntimeState.NotRunning => RunNotRunning,
        _ => RunNotVerified
    };
    private static readonly string RunRunning = T("Em execução", "Running");
    private static readonly string RunNotRunning = T("Não está em execução", "Not running");
    private static readonly string RunNotVerified = T("Execução não verificada", "Running state not verified");

    public static string Eligibility(ChangeEligibility eligibility) => eligibility switch
    {
        ChangeEligibility.Eligible => EligOk,
        ChangeEligibility.ReadOnly => EligReadOnly,
        _ => EligUnavailable
    };
    private static readonly string EligOk = T("Elegível para simulação", "Eligible for simulation");
    private static readonly string EligReadOnly = T("Somente leitura", "Read-only");
    private static readonly string EligUnavailable = T("Operação indisponível", "Operation unavailable");

    public static string Kind(InstalledApplicationKind kind) => kind switch
    {
        InstalledApplicationKind.Msi => KindMsi,
        InstalledApplicationKind.Msix => KindMsix,
        _ => KindWin32
    };
    private static readonly string KindMsi = T("MSI", "MSI");
    private static readonly string KindMsix = T("MSIX/Store", "MSIX/Store");
    private static readonly string KindWin32 = T("Win32 (Registro)", "Win32 (Registry)");

    public static string Method(UninstallMethod method) => method switch
    {
        UninstallMethod.MsiProductCode => MethodMsi,
        UninstallMethod.MsixPackageFullName => MethodMsix,
        _ => MethodNone
    };
    private static readonly string MethodMsi = T("MSI por código de produto", "MSI by product code");
    private static readonly string MethodMsix = T("MSIX por nome completo do pacote", "MSIX by full package name");
    private static readonly string MethodNone = T("Nenhum mecanismo confiável", "No reliable mechanism");

    public static string Reason(ChangeBlockReason reason) => reason switch
    {
        ChangeBlockReason.NotAnApplication => ReasonNotApp,
        ChangeBlockReason.ProtectedEntry => ReasonProtected,
        ChangeBlockReason.ConservativeScheduledTask => ReasonTask,
        ChangeBlockReason.NoReversibleMechanism => ReasonNoRevert,
        ChangeBlockReason.NoReliableUninstallMechanism => ReasonNoUninstall,
        ChangeBlockReason.SystemOrFramework => ReasonSystem,
        ChangeBlockReason.AllUsersNotSupported => ReasonAllUsers,
        ChangeBlockReason.IdentityIncomplete => ReasonIdentity,
        ChangeBlockReason.UnknownState => ReasonUnknown,
        _ => ReasonNone
    };
    private static readonly string ReasonNone = T("Sem bloqueio.", "No block.");
    private static readonly string ReasonNotApp = T("Serviço, driver, componente de segurança, tarefa interna ou RunOnce: não é tratado como aplicativo comum.", "Service, driver, security component, internal task or RunOnce: not treated as a regular app.");
    private static readonly string ReasonProtected = T("Entrada protegida.", "Protected entry.");
    private static readonly string ReasonTask = T("Tarefa agendada: classificação conservadora, sem mecanismo reversível comprovado.", "Scheduled task: conservative classification, no proven reversible mechanism.");
    private static readonly string ReasonNoRevert = T("Sem mecanismo reversível comprovado para esta origem.", "No proven reversible mechanism for this source.");
    private static readonly string ReasonNoUninstall = T("Sem mecanismo de desinstalação confiável para este tipo de instalação.", "No reliable uninstall mechanism for this installation type.");
    private static readonly string ReasonSystem = T("Componente de sistema ou framework.", "System component or framework.");
    private static readonly string ReasonAllUsers = T("Remoção para todos os usuários não é suportada.", "Removal for all users is not supported.");
    private static readonly string ReasonIdentity = T("Identidade incompleta.", "Incomplete identity.");
    private static readonly string ReasonUnknown = T("Estado de inicialização desconhecido.", "Unknown startup state.");

    public static string Outcome(AppsOperationOutcome outcome) => outcome switch
    {
        AppsOperationOutcome.Succeeded => OutOk,
        AppsOperationOutcome.Failed => OutFailed,
        AppsOperationOutcome.Indeterminate => OutIndeterminate,
        AppsOperationOutcome.Conflict => OutConflict,
        AppsOperationOutcome.GateClosed => OutGate,
        _ => OutBlocked
    };
    private static readonly string OutOk = T("Concluída (sintética). Nenhuma alteração foi feita no Windows.", "Completed (synthetic). No change was made to Windows.");
    private static readonly string OutFailed = T("Falhou (sintética). Nenhuma alteração foi confirmada.", "Failed (synthetic). No change was confirmed.");
    private static readonly string OutIndeterminate = T("Indeterminada: o estado final não pôde ser confirmado. Atualize o inventário.", "Indeterminate: the final state could not be confirmed. Refresh the inventory.");
    private static readonly string OutConflict = T("O item mudou após a prévia. Atualize o inventário e prepare uma nova prévia.", "The item changed after the preview. Refresh the inventory and prepare a new preview.");
    private static readonly string OutGate = T("Operação real desabilitada pelo gate de segurança desta distribuição.", "Real operation disabled by this distribution's safety gate.");
    private static readonly string OutBlocked = T("Bloqueada: confirmação divergente ou item inelegível.", "Blocked: confirmation mismatch or ineligible item.");

    // Prévia e confirmação
    public static readonly string PreviewDisable = T("Simular: desativar inicialização", "Simulate: disable startup");
    public static readonly string PreviewEnable = T("Simular: reativar inicialização", "Simulate: re-enable startup");
    public static readonly string PreviewUninstall = T("Simular: desinstalar", "Simulate: uninstall");
    public static readonly string ConfirmLabel = T("Digite exatamente a confirmação exibida", "Type the confirmation exactly as shown");
    public static readonly string Execute = T("Confirmar simulação", "Confirm simulation");
    public static readonly string ConfirmDialog = T("Confirmar esta simulação individual sobre dados de demonstração? Nenhuma alteração será feita no Windows.", "Confirm this single simulation on demonstration data? No change will be made to Windows.");
    public static readonly string NoSelection = T("Nenhum item selecionado.", "No item selected.");
    public static readonly string PreviewNote = T("Desativar a inicialização não encerra processos em execução; os dados originais da entrada são preservados e a ação é reversível.", "Disabling startup does not end running processes; the entry's original data is preserved and the action is reversible.");
    public static readonly string UninstallNote = T("Desinstalação individual. MSIX pode encerrar o aplicativo e remover dependências sem outros consumidores. Após o início, não há cancelamento garantido. MSI pode exigir reinicialização posterior; o SentinelCore não reinicia o computador. Sem lote ou comandos de UninstallString.", "Single uninstall. MSIX may close the app and remove dependencies with no other consumers. Cancellation is not guaranteed after starting. MSI may require a later restart; SentinelCore does not restart the computer. No batch or UninstallString commands.");
    public static readonly string RequiresPrivilege = T("Exige privilégios elevados.", "Requires elevated privileges.");
    public static readonly string NoPrivilege = T("Não exige privilégios elevados.", "Does not require elevated privileges.");
    public static readonly string ConfirmationIs = T("Confirmação exata", "Exact confirmation");
    public static readonly string NotExecutable = T("Esta operação não pode ser executada.", "This operation cannot be run.");

    // Detalhes
    public static readonly string DetPublisherUnknown = T("(não informado)", "(not provided)");
    public static readonly string DetReference = T("Executável ou referência", "Executable or reference");
    public static readonly string DetType = T("Tipo de entrada", "Entry type");
    public static readonly string DetPrivilege = T("Privilégios", "Privileges");
    public static readonly string DetReason = T("Motivo", "Reason");
    public static readonly string DetId = T("Identificador estável", "Stable identifier");
    public static readonly string DetLocation = T("Localização", "Location");
    public static readonly string DetRegistry = T("Chave no Registro do Windows", "Windows Registry key");
    public static readonly string DetMethod = T("Método previsto", "Planned method");
    public static readonly string DetOrigin = T("Origem dos dados", "Data origin");
    public static readonly string DetSynthetic = T("Sintética (fixture)", "Synthetic (fixture)");
    public static readonly string DetOperational = T("Operacional", "Operational");
    public static readonly string DetScope = T("Escopo", "Scope");
    public static readonly string DetState = T("Estado configurado", "Configured state");
    public static readonly string DetRuntime = T("Execução atual", "Current execution");
    public static readonly string DetVersion = T("Versão", "Version");
    public static readonly string DetKind = T("Tipo de instalação", "Installation type");

    // Histórico
    public static readonly string HistoryTitle = T("Histórico de operações", "Operation history");
    public static readonly string HistoryEmpty = T("Nenhuma operação carregada.", "No operation loaded.");
    public static readonly string HistoryDisable = T("Desativar inicialização", "Disable startup");
    public static readonly string HistoryEnable = T("Reativar inicialização", "Re-enable startup");
    public static readonly string HistoryUninstall = T("Desinstalar", "Uninstall");

    public static readonly string RestartRequired = T("Reinicialização posterior necessária", "A later restart is required");
    private static readonly string OperationalOk = T("Concluída; estado final verificado.", "Completed; final state verified.");
    private static readonly string OperationalFailed = T("Falhou; consulte o código do resultado.", "Failed; check the result code.");

    public static string Outcome(AppsOperationOutcome outcome, bool synthetic) => !synthetic && outcome == AppsOperationOutcome.Succeeded
        ? OperationalOk : !synthetic && outcome == AppsOperationOutcome.Failed ? OperationalFailed : Outcome(outcome);

    public static string HistoryAction(string kind, string action) =>
        kind == "uninstall" ? HistoryUninstall : action == "disable" ? HistoryDisable : HistoryEnable;
}
