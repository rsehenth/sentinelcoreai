using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal static class WerReportParser
{
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] KnownPrefixes =
    [
        "AppCrash", "AppHang", "Kernel", "LiveKernelEvent", "BlueScreen", "Critical", "NonCritical",
        "AppLaunch", "AppTerminated", "Update", "Generic", "Store", "WERInternal"
    ];

    private static readonly HashSet<string> ExcludedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "User", "UserSid", "Sid", "Username", "UserDomain"
    };

    public static RawWerReport Parse(string source, string store, string folderPath, int maxFileBytes)
    {
        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var limitations = new List<string>();
        var folderTime = FolderCreation(folderPath, limitations);
        var pairs = ReadPairs(Path.Combine(folderPath, "Report.wer"), maxFileBytes, limitations);

        var fileId = Text(pairs, "ReportIdentifier") ?? Text(pairs, "ReportId");
        var reportIdFromFile = false;
        string reportId;
        if (fileId is not null && Guid.TryParse(fileId.Trim('{', '}'), out var parsedId))
        {
            reportId = parsedId.ToString("D");
            reportIdFromFile = true;
        }
        else
        {
            var folderGuid = GuidFromName(folderName);
            if (folderGuid is not null)
            {
                reportId = folderGuid;
                limitations.Add("ReportId derivado do nome da pasta; o arquivo Report.wer não o forneceu.");
            }
            else
            {
                reportId = $"folder:{source}:{store}:{folderName}";
                limitations.Add("ReportId não disponível; identidade limitada à pasta de origem.");
            }
        }

        var (folderType, folderCode) = FromFolderName(folderName);
        var (fileType, fileCode) = FromFolderName(Text(pairs, "EventType"));
        var eventType = fileType ?? folderType;
        var problemCode = fileCode ?? folderCode;
        if (fileType is null && folderType is not null)
            limitations.Add("EventType derivado do nome da pasta.");
        if (fileCode is null && folderCode is not null)
            limitations.Add("Código do problema derivado do nome da pasta.");

        var eventTime = FileTime(Text(pairs, "EventTime"));
        var creationTime = FileTime(Text(pairs, "CreationTime")) ?? folderTime;
        if (eventTime is null)
            limitations.Add("Horário do evento ausente; foi preservado o horário de criação disponível.");
        if (creationTime is null)
            limitations.Add("Horário de criação indisponível.");

        var application = Text(pairs, "AppName");
        var applicationPath = Text(pairs, "AppPath");
        var module = Text(pairs, "ModuleName");
        var moduleVersion = Text(pairs, "ModuleVersion");
        var bucket = Text(pairs, "Response.BucketId") ?? Text(pairs, "BucketId");
        var status = Text(pairs, "ReportStatus");
        var confidence = reportIdFromFile ? EvidenceConfidence.High
            : reportId.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) ? EvidenceConfidence.Low
            : EvidenceConfidence.Medium;

        return new(reportId, reportIdFromFile, source, store, folderName, eventType, problemCode, eventTime,
            creationTime, status, application, applicationPath, module, moduleVersion, bucket,
            Data(pairs, limitations), limitations, confidence);
    }

    public static (string? EventType, string? ProblemCode) FromFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, null);
        var head = KnownPrefixes.FirstOrDefault(prefix => prefix.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (head is null) return (null, null);
        if ((head is "Kernel" or "LiveKernelEvent") && parts.Length > 1 &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return (head, parts[1]);
        return (head, null);
    }

    public static string? GuidFromName(string name)
    {
        var match = GuidPattern.Match(name);
        return match.Success && Guid.TryParse(match.Value, out var guid) ? guid.ToString("D") : null;
    }

    public static DateTimeOffset? FileTime(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) return null;
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(ticks));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadPairs(string path, int maxFileBytes,
        List<string> limitations)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            limitations.Add("Report.wer ausente; somente metadados da pasta foram usados.");
            return pairs;
        }

        try
        {
            var info = new FileInfo(path);
            var length = (int)Math.Min(info.Length, maxFileBytes);
            if (info.Length > maxFileBytes)
                limitations.Add($"Report.wer excede {maxFileBytes} bytes; a leitura foi limitada.");
            var bytes = new byte[length];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset < bytes.Length) Array.Resize(ref bytes, offset);
            }

            var text = Decode(bytes).GetString(bytes).TrimStart('\uFEFF');
            var malformed = 0;
            foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is '[' or ';') continue;
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    malformed++;
                    continue;
                }
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (key.Length == 0 || key.Length > 128) continue;
                pairs.TryAdd(key, value.Length > 1024 ? value[..1024] : value);
            }
            if (malformed > 0) limitations.Add($"{malformed} linha(s) de Report.wer sem formato chave=valor foram ignoradas.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            limitations.Add($"Report.wer não pôde ser lido ({error.GetType().Name}); somente a pasta foi usada.");
        }
        return pairs;
    }

    private static Encoding Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;
        return Encoding.UTF8;
    }

    private static IReadOnlyList<EventProperty> Data(IReadOnlyDictionary<string, string> pairs,
        List<string> limitations)
    {
        var eligible = pairs
            .Where(pair => !ExcludedKeys.Contains(pair.Key) &&
                !pair.Key.StartsWith("LoadedModule", StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var data = eligible
            .Take(128)
            .Select(pair => new EventProperty(pair.Key, pair.Value))
            .ToArray();
        if (eligible.Length > 128) limitations.Add("Metadados WER limitados a 128 propriedades técnicas.");
        return data;
    }

    private static string? Text(IReadOnlyDictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var value) ? value : null;

    private static DateTimeOffset? FolderCreation(string path, List<string> limitations)
    {
        try
        {
            var created = Directory.GetCreationTimeUtc(path);
            if (created.Year < 1980)
            {
                limitations.Add("Horário de criação da pasta indisponível.");
                return null;
            }
            return new DateTimeOffset(created);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            limitations.Add($"Horário da pasta indisponível ({error.GetType().Name}).");
            return null;
        }
    }
}
