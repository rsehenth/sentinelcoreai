using System.Globalization;
using System.Text;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using Xunit;

namespace SentinelCore.Tests;

public sealed class WerParserTests : IDisposable
{
    private const int MaxBytes = 1024 * 1024;
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "SentinelCoreTests", "wer-" + Guid.NewGuid().ToString("N"));

    public WerParserTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Parse_FileIdentifierTimesAndNeutralKeys()
    {
        var folder = Folder("AppCrash_Contoso_1234");
        var eventTime = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var creation = eventTime.AddMinutes(2);
        WriteReport(folder,
            "EventType=APPCRASH",
            "ReportIdentifier={11111111-2222-3333-4444-555555555555}",
            "EventTime=" + eventTime.UtcDateTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture),
            "CreationTime=" + creation.UtcDateTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture),
            "AppName=Contoso.exe",
            "AppPath=C:\\Program Files\\Contoso\\Contoso.exe",
            "ModuleName=contoso.dll",
            "ModuleVersion=1.2.3.4",
            "Response.BucketId=12345",
            "ReportStatus=1",
            "User=someone",
            "UserSid=S-1-5-21-1",
            "LoadedModule[0]=C:\\Windows\\System32\\ntdll.dll",
            "Sig[0].Name=Application Name",
            "Sig[0].Value=Contoso.exe");

        var report = WerReportParser.Parse("Sistema", "ReportArchive", folder, MaxBytes);

        Assert.Equal("11111111-2222-3333-4444-555555555555", report.ReportId);
        Assert.True(report.ReportIdFromFile);
        Assert.Equal(EvidenceConfidence.High, report.Confidence);
        Assert.Equal(eventTime, report.EventTimeUtc);
        Assert.Equal(creation, report.CreationTimeUtc);
        Assert.Equal("AppCrash", report.EventType);
        Assert.Null(report.ProblemCode);
        Assert.Equal("Contoso.exe", report.Application);
        Assert.Equal("C:\\Program Files\\Contoso\\Contoso.exe", report.ApplicationPath);
        Assert.Equal("contoso.dll", report.Module);
        Assert.Equal("1.2.3.4", report.ModuleVersion);
        Assert.Equal("12345", report.BucketId);
        Assert.Equal("1", report.ReportStatus);
        Assert.Contains(report.Data, pair => pair.Name == "Sig[0].Name" && pair.Value == "Application Name");
        Assert.Contains(report.Data, pair => pair.Name == "ReportIdentifier");
        Assert.DoesNotContain(report.Data, pair => pair.Name.Equals("User", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Data, pair => pair.Name.Equals("UserSid", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Data, pair => pair.Name.StartsWith("LoadedModule", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(report.Limitations);
    }

    [Fact]
    public void Parse_KernelFolderWithoutReportFileUsesSyntheticIdentity()
    {
        var folder = Folder("Kernel_141");

        var report = WerReportParser.Parse("Sistema", "ReportArchive", folder, MaxBytes);

        Assert.False(report.ReportIdFromFile);
        Assert.StartsWith("folder:Sistema:ReportArchive:Kernel_141", report.ReportId, StringComparison.Ordinal);
        Assert.Equal(EvidenceConfidence.Low, report.Confidence);
        Assert.Equal("Kernel", report.EventType);
        Assert.Equal("141", report.ProblemCode);
        Assert.Contains(report.Limitations, item => item.Contains("Report.wer ausente", StringComparison.Ordinal));
        Assert.Contains(report.Limitations, item => item.Contains("identidade limitada à pasta", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_GuidInFolderNameUsesMediumConfidence()
    {
        const string guid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var folder = Folder($"AppCrash_Contoso.exe_{guid}");

        var report = WerReportParser.Parse("Usuário", "ReportQueue", folder, MaxBytes);

        Assert.False(report.ReportIdFromFile);
        Assert.Equal(guid, report.ReportId);
        Assert.Equal(EvidenceConfidence.Medium, report.Confidence);
        Assert.Contains(report.Limitations, item => item.Contains("nome da pasta", StringComparison.Ordinal));
        Assert.Equal("AppCrash", report.EventType);
        Assert.Null(report.ProblemCode);
    }

    [Fact]
    public void Parse_DoesNotReadDumpOrNonReportFiles()
    {
        var folder = Folder("Kernel_141");
        File.WriteAllText(Path.Combine(folder, "memory.hdmp"), "AppName=ShouldNotAppear\r\nReportIdentifier={99999999-9999-9999-9999-999999999999}");

        var report = WerReportParser.Parse("Sistema", "ReportArchive", folder, MaxBytes);

        Assert.Null(report.Application);
        Assert.DoesNotContain(report.Data, pair => pair.Name.Equals("AppName", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Data, pair => pair.Name.Equals("ReportIdentifier", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Kernel", report.EventType);
    }

    [Fact]
    public void FileTime_ReturnsUtcAndRejectsInvalidValue()
    {
        var instant = new DateTimeOffset(2030, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var parsed = WerReportParser.FileTime(instant.UtcDateTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));

        Assert.NotNull(parsed);
        Assert.Equal(instant, parsed.Value);
        Assert.Equal(TimeSpan.Zero, parsed.Value.Offset);
        Assert.Null(WerReportParser.FileTime("not-a-number"));
        Assert.Null(WerReportParser.FileTime("-9000000000000000000"));
    }

    [Theory]
    [InlineData("Kernel_141", "Kernel", "141")]
    [InlineData("LiveKernelEvent_193", "LiveKernelEvent", "193")]
    [InlineData("AppCrash_Contoso.exe", "AppCrash", null)]
    [InlineData("AppHang_Contoso.exe_1234", "AppHang", null)]
    [InlineData("BlueScreen_0x0000011E", "BlueScreen", null)]
    [InlineData("Kernel_abc", "Kernel", null)]
    [InlineData("unrelated_folder", null, null)]
    public void FromFolderName_RecognizesKnownTechnicalPrefixes(string name, string? eventType, string? problemCode)
    {
        var result = WerReportParser.FromFolderName(name);

        Assert.Equal(eventType, result.EventType);
        Assert.Equal(problemCode, result.ProblemCode);
    }

    [Fact]
    public void Parse_DoesNotInferApplicationFromLocalizedSignatureNames()
    {
        var folder = Folder("AppCrash_Other");
        WriteReport(folder,
            "EventType=AppCrash",
            "Sig[0].Name=Nome do Aplicativo",
            "Sig[0].Value=Contoso.exe");

        var report = WerReportParser.Parse("Sistema", "ReportArchive", folder, MaxBytes);

        Assert.Null(report.Application);
        Assert.Contains(report.Data, pair => pair.Name == "Sig[0].Value" && pair.Value == "Contoso.exe");
    }

    public static TheoryData<string, string> Encodings => new()
    {
        { "utf16le", "utf-16le" },
        { "utf16be", "utf-16be" },
        { "utf8bom", "utf-8bom" },
        { "utf8", "utf-8" }
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Parse_SupportsReportFileEncodings(string label, string encodingName)
    {
        var folder = Folder("AppHang_" + label);
        var encoding = encodingName switch
        {
            "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            "utf-8bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        WriteReport(folder, encoding,
            "EventType=AppHang",
            "AppName=Contoso.exe",
            "ReportIdentifier={22222222-3333-4444-5555-666666666666}");

        var report = WerReportParser.Parse("Usuário", "ReportQueue", folder, MaxBytes);

        Assert.True(report.ReportIdFromFile);
        Assert.Equal("Contoso.exe", report.Application);
        Assert.Equal("AppHang", report.EventType);
    }

    [Fact]
    public void Parse_RecordsMalformedLinesAsLimitation()
    {
        var folder = Folder("AppCrash_Malformed");
        WriteReport(folder,
            "AppName=Contoso.exe",
            "this line has no separator");

        var report = WerReportParser.Parse("Sistema", "ReportArchive", folder, MaxBytes);

        Assert.Equal("Contoso.exe", report.Application);
        Assert.Contains(report.Limitations, item => item.Contains("chave=valor", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RespectsMaximumFileSize()
    {
        var folder = Folder("AppCrash_Large");
        WriteReport(folder,
            "AppName=Contoso.exe",
            new string('x', 4096));

        var report = WerReportParser.Parse("Sistema", "ReportArchive", folder, 128);

        Assert.Contains(report.Limitations, item => item.Contains("excede 128 bytes", StringComparison.Ordinal));
    }

    private string Folder(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteReport(string folder, params string[] lines) =>
        WriteReport(folder, Encoding.Unicode, lines);

    private static void WriteReport(string folder, Encoding encoding, params string[] lines) =>
        File.WriteAllText(Path.Combine(folder, "Report.wer"), string.Join("\r\n", lines) + "\r\n", encoding);
}
