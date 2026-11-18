using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SentinelCore.App.Localization;
using SentinelCore.Domain;

namespace SentinelCore.App;

public partial class MainWindow
{
    private const string NoticesFileName = "THIRD-PARTY-NOTICES.md";

    /// <summary>Página Sobre: os textos vêm dos recursos (trocam com o idioma); versão e build, dos metadados do assembly.</summary>
    private void InitializeAbout()
    {
        AboutVersionText.Text = $"v{SoftwareInfo.Version}";
        AboutPreviewPill.Visibility = SoftwareInfo.Version.Contains("-preview", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        AboutBuildText.Text = BuildDescription();
        AboutProjectText.Text = AboutText.ProjectUrl;
        AboutNoticesButton.Visibility = File.Exists(Path.Combine(AppContext.BaseDirectory, NoticesFileName))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildDescription()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var built = string.Empty;
        try
        {
            var location = typeof(MainWindow).Assembly.Location;
            if (location.Length > 0) built = $" · {File.GetLastWriteTime(location):yyyy-MM-dd}";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return $"{configuration}{built} · {RuntimeInformation.ProcessArchitecture}";
    }

    private void OnAboutOpenProject(object sender, RoutedEventArgs e) => OpenAboutTarget(AboutText.ProjectUrl);

    private void OnAboutOpenNotices(object sender, RoutedEventArgs e) =>
        OpenAboutTarget(Path.Combine(AppContext.BaseDirectory, NoticesFileName));

    /// <summary>Abre somente os dois destinos fixos da página (URL do projeto e arquivo de avisos), por clique explícito.</summary>
    private void OpenAboutTarget(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception error) { ShowFailure(Loc.T(AboutText.OpenFailed), error); }
    }
}
