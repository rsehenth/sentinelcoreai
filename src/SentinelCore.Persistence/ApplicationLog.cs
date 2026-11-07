namespace SentinelCore.Persistence;

/// <summary>
/// Registro de falhas em <c>logs/application.log</c> da raiz autorizada. Sem raiz (null) nada é gravado:
/// não há recuo para a raiz padrão.
/// </summary>
public sealed class ApplicationLog(string? root)
{
    public void Write(Exception error)
    {
        if (root is null) return;
        try
        {
            var directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "application.log"), $"{DateTimeOffset.UtcNow:O} {error}\n");
        }
        catch (Exception loggingError)
        {
            System.Diagnostics.Debug.WriteLine($"Falha ao gravar log: {loggingError.GetType().Name}");
        }
    }
}
