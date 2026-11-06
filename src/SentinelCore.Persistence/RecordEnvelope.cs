using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Persistence;

internal enum AtomicWriteStage { BeforeTemporary, DuringTemporaryWrite, BeforePublish, AfterPublish }

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
        var end = text.LastIndexOf(ChecksumMarker, StringComparison.Ordinal);
        if (end < start) throw new InvalidDataException("Checksum ausente.");
        var json = text[start..end];
        var hashStart = end + ChecksumMarker.Length;
        if (text.Length < hashStart + 64) throw new InvalidDataException("Checksum truncado.");
        if (text.Substring(hashStart, 64) != Hash(json))
            throw new InvalidDataException("Checksum não confere.");
        if (!text[(hashStart + 64)..].StartsWith(" -->", StringComparison.Ordinal))
            throw new InvalidDataException("Envelope malformado.");
        return json;
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Test-only seam: internal, scoped to the current async flow and null by default. It is reachable only from
    /// assemblies named in InternalsVisibleTo (tests and the synthetic worker); there is no config, argument or env switch.
    /// </summary>
    internal static readonly AsyncLocal<Action<AtomicWriteStage>?> Probe = new();

    public static void AtomicWrite(string path, string content, bool overwrite)
    {
        var probe = Probe.Value;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            probe?.Invoke(AtomicWriteStage.BeforeTemporary);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var split = probe is null ? bytes.Length : bytes.Length / 2;
                stream.Write(bytes.AsSpan(0, split));
                if (probe is not null)
                {
                    stream.Flush(flushToDisk: true);
                    probe(AtomicWriteStage.DuringTemporaryWrite);
                    stream.Write(bytes.AsSpan(split));
                }
                stream.Flush(flushToDisk: true);
            }
            probe?.Invoke(AtomicWriteStage.BeforePublish);
            File.Move(temporary, path, overwrite);
            probe?.Invoke(AtomicWriteStage.AfterPublish);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
