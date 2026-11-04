using System.Security.Cryptography;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public sealed class FileExclusiveLockFactory : IExclusiveLockFactory
{
    private readonly string directory;

    public FileExclusiveLockFactory(string dataRoot)
    {
        directory = Path.Combine(Path.GetFullPath(dataRoot), "data", "locks");
        Directory.CreateDirectory(directory);
    }

    public IDisposable Acquire(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Chave de bloqueio vazia.", nameof(key));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var path = Path.Combine(directory, hash[..32] + ".lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new TargetLockedException("Alvo já está sendo modificado por outra operação.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new TargetLockedException("Bloqueio do alvo indisponível.", error);
        }
    }
}
