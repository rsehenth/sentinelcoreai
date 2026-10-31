using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Persistence;

internal static class RecordEnvelope
{
    internal const string PayloadMarker = "\n<!-- sentinelcore:payload:v1 -->\n```json\n";
    internal const string ChecksumMarker = "\n```\n<!-- sha256:";
    internal const int MaximumRecordBytes = 4 * 1024 * 1024;

    public static string Wrap(string markdown, string json) =>
        markdown + PayloadMarker + json + ChecksumMarker + Hash(json) + " -->\n";

    public static string Unwrap(string text)
    {
        var start = text.IndexOf(PayloadMarker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("Payload estruturado ausente.");
        start += PayloadMarker.Length;
        var end = text.IndexOf(ChecksumMarker, start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("Checksum ausente.");
        var json = text[start..end];
        var hashStart = end + ChecksumMarker.Length;
        if (text.Length < hashStart + 64 || text.Substring(hashStart, 64) != Hash(json))
            throw new InvalidDataException("Checksum não confere.");
        return json;
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static void AtomicWrite(string path, string content, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
