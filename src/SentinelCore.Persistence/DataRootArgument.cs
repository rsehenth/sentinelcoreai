namespace SentinelCore.Persistence;

/// <summary>
/// Resolve e valida <c>--data-root</c> antes de qualquer composição. Não cria diretórios, não grava e não migra:
/// só normaliza o texto e consulta atributos de caminhos existentes. Argumento explícito inválido lança
/// <see cref="ArgumentException"/>; nunca recua para a raiz padrão.
/// </summary>
public static class DataRootArgument
{
    public const string Name = "--data-root";

    /// <param name="defaultRoot">Raiz padrão (injetável nos testes). Usada só se o argumento estiver ausente.</param>
    /// <param name="forbidden">Diretórios (e descendentes) proibidos; null usa os diretórios do sistema.</param>
    /// <param name="getAttributes">Leitor de atributos (injetável nos testes); null usa <see cref="File.GetAttributes"/>.</param>
    public static string Resolve(IReadOnlyList<string> args, string? defaultRoot = null,
        IEnumerable<string>? forbidden = null, Func<string, FileAttributes>? getAttributes = null)
    {
        var fallback = Path.GetFullPath(defaultRoot ?? MemoryStore.DefaultRoot);
        string? value = null;
        for (var index = 0; index < args.Count; index++)
        {
            string? current = null;
            if (args[index].StartsWith(Name + "=", StringComparison.Ordinal))
                current = args[index][(Name.Length + 1)..];
            else if (args[index] == Name)
            {
                if (index + 1 >= args.Count)
                    throw new ArgumentException("O argumento --data-root exige um caminho absoluto.");
                current = args[++index];
            }
            if (current is null) continue;
            if (value is not null)
                throw new ArgumentException("O argumento --data-root foi informado mais de uma vez.");
            value = current;
        }
        if (value is null) return fallback;
        return Validate(value, fallback, forbidden ?? SystemDirectories(), getAttributes ?? File.GetAttributes);
    }

    private static string Validate(string value, string fallback, IEnumerable<string> forbidden,
        Func<string, FileAttributes> getAttributes)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("O argumento --data-root não pode ser vazio.");
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("O diretório de dados injetado deve ser um caminho absoluto.");
        string full;
        try { full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception error) { throw new ArgumentException("Caminho de dados inválido: " + error.Message); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Compartilhamentos de rede e caminhos de dispositivo não são aceitos como diretório de dados.");
        if (full.IndexOf(':', 2) >= 0)
            throw new ArgumentException("O diretório de dados não aceita fluxos alternativos de dados.");
        var driveRoot = (Path.GetPathRoot(full) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("O diretório de dados não pode ser a raiz de uma unidade.");
        foreach (var directory in forbidden)
            if (IsWithin(full, directory))
                throw new ArgumentException($"O diretório de dados não pode ficar em {directory}.");
        if (IsWithin(full, fallback))
            throw new ArgumentException(
                "O diretório de dados não pode ser a raiz padrão do aplicativo; omita --data-root para usá-la.");
        RejectLinks(full, driveRoot, getAttributes);
        return full;
    }

    // Fail-closed: o destino e cada ancestral existente não podem ser links/junções (o destino real seria outro).
    private static void RejectLinks(string full, string driveRoot, Func<string, FileAttributes> getAttributes)
    {
        for (var current = full;
             !string.IsNullOrEmpty(current) && !string.Equals(current, driveRoot, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = getAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // ainda não existe: nada a verificar; a criação ocorre só depois da validação completa
            }
            catch (Exception error)
            {
                throw new ArgumentException($"Não foi possível verificar o diretório de dados ({error.GetType().Name}).");
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException($"O diretório de dados não pode usar link ou junção ({current}).");
            if (!attributes.HasFlag(FileAttributes.Directory))
                throw new ArgumentException($"O diretório de dados colide com um arquivo existente ({current}).");
        }
    }

    private static IEnumerable<string> SystemDirectories() =>
        new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData
        }.Select(Environment.GetFolderPath).Where(path => !string.IsNullOrWhiteSpace(path));

    private static bool IsWithin(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var normalized = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
