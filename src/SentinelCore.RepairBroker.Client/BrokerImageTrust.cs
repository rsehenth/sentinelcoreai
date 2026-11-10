using System.Security.Cryptography;

namespace SentinelCore.RepairBroker;

internal static class BrokerImageTrust
{
    internal static FileStream Open(string imagePath)
    {
        if (BrokerImagePin.Value.Length != 64)
            throw new UnauthorizedAccessException("Imagem do broker ainda não aprovada para elevação.");
        var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(image)), BrokerImagePin.Value, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Imagem do broker diverge da distribuição aprovada.");
            return image;
        }
        catch { image.Dispose(); throw; }
    }
}
