using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Repairs;

/// <summary>
/// Valida a identidade do executável oficial. Não pesquisa PATH, não aceita caminho configurável, não
/// resolve scripts e não inicia processo algum. O arquivo precisa ser o nome exato dentro de System32.
/// </summary>
public static class RepairToolPathPolicy
{
    public static bool IsCanonicalToolPath(RepairTool tool, string? path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Caminho do executável ausente.";
            return false;
        }
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Somente executáveis .exe oficiais são aceitos.";
            return false;
        }
        var expectedName = RepairToolNames.Executable(tool);
        if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Nome do executável diverge de {expectedName}.";
            return false;
        }
        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            reason = "Diretório System32 indisponível.";
            return false;
        }

        string fullPath;
        string systemFull;
        try
        {
            fullPath = Path.GetFullPath(path);
            systemFull = Path.GetFullPath(systemDirectory).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception error)
        {
            reason = "Caminho do executável inválido: " + error.Message;
            return false;
        }

        if (!string.Equals(Path.GetDirectoryName(fullPath), systemFull,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "O executável precisa estar diretamente em System32.";
            return false;
        }
        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = "Executável em System32 é um ponto de reparse e não é aceito.";
                return false;
            }
        }
        catch (Exception error)
        {
            reason = "Atributos do executável não puderam ser validados: " + error.Message;
            return false;
        }
        reason = string.Empty;
        return true;
    }
}

public sealed class WindowsRepairToolLocator : IRepairToolLocator
{
    public RepairToolLocation Resolve(RepairTool tool)
    {
        var name = RepairToolNames.Executable(tool);
        if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
            return RepairToolLocation.Missing(tool,
                "Processo 32-bit não é aceito: System32 seria redirecionado para SysWOW64.");
        var system = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(system))
            return RepairToolLocation.Missing(tool, "Diretório System32 indisponível; executável oficial não resolvido.");

        var path = Path.Combine(system, name);
        if (!File.Exists(path))
            return RepairToolLocation.Missing(tool, $"{name} não encontrado em System32; PATH não é consultado.");
        if (!RepairToolPathPolicy.IsCanonicalToolPath(tool, path, out var reason))
            return RepairToolLocation.Missing(tool, reason);
        return new RepairToolLocation(tool, true, path, name, null,
            ["Caminho oficial validado em System32; nenhum processo foi iniciado."]);
    }
}
