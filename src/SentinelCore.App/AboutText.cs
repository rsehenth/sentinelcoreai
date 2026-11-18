namespace SentinelCore.App;

/// <summary>
/// Textos da página Sobre (português como chave). <see cref="Entries"/> é anexado ao final de <c>Strings.Entries</c>:
/// os índices T#### existentes não mudam. Só apresentação; nenhum dado de motor ou histórico passa por aqui.
/// </summary>
internal static class AboutText
{
    /// <summary>Único destino de rede da página; abre somente após o clique do usuário.</summary>
    public const string ProjectUrl = "https://github.com/rsehenth/sentinelcoreai";

    private static readonly List<(string Pt, string En)> entries = [];
    private static string T(string pt, string en) { entries.Add((pt, en)); return pt; }

    public static (string Pt, string En)[] Entries { get { _ = Title; return [.. entries]; } }

    public static readonly string Title = T("Sobre", "About");
    public static readonly string Group = T("Projeto", "Project");
    public static readonly string NavTooltip = T("Sobre o SentinelCore AI: versão, licença e projeto", "About SentinelCore AI: version, license and project");
    public static readonly string Tagline = T("Aplicativo local de diagnóstico para Windows 11.", "Local diagnostics app for Windows 11.");
    public static readonly string Summary = T(
        "Audita hardware, eventos, desempenho, rede e gráficos e guarda o histórico neste computador.",
        "Audits hardware, events, performance, network and graphics, and keeps the history on this computer.");

    public static readonly string Version = T("Versão", "Version");
    public static readonly string Preview = T("Prévia", "Preview");
    public static readonly string Build = T("Compilação", "Build");
    public static readonly string Technologies = T("Tecnologias", "Technologies");
    public static readonly string License = T("Licença", "License");
    public static readonly string Notices = T("Avisos de terceiros", "Third-party notices");
    public static readonly string NoticesTooltip = T("Abre o arquivo de créditos e avisos de terceiros", "Opens the third-party credits and notices file");
    public static readonly string Project = Group;
    public static readonly string OpenProject = T("Abrir no GitHub", "Open on GitHub");
    public static readonly string OpenProjectTooltip = T("Abre o repositório no navegador", "Opens the repository in your browser");
    public static readonly string Status = T("Status", "Status");
    public static readonly string StatusText = T(
        "Aplicativo local com recursos de diagnóstico. As operações reais de manutenção estão bloqueadas nesta distribuição.",
        "Local app with diagnostic features. Real maintenance operations are blocked in this distribution.");
    public static readonly string Footnote = T(
        "Esta página não faz consultas de rede; os links só abrem quando você os aciona.",
        "This page makes no network requests; links open only when you click them.");
    public static readonly string OpenFailed = T("Não foi possível abrir o link", "The link could not be opened");
}
