using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SentinelCore.App.Localization;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

// No Application or Window is created. Only text resources and a GUID temporary root are used.
[Collection("Localization")]
public sealed class LocalizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreLanguageTests", Guid.NewGuid().ToString("N"));

    public LocalizationTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewOrUnreadablePreferenceAlwaysUsesEnglish(bool unreadable)
    {
        Localizer.Use(AppLanguage.Portuguese);
        if (unreadable) Directory.CreateDirectory(Path.Combine(root, "ui-language.txt"));
        Localizer.Initialize(root);
        Assert.Equal(AppLanguage.English, Localizer.Current);
    }

    [Fact]
    public void PreferenceRoundTripsOnlyInsideTheExplicitRoot()
    {
        Localizer.Use(AppLanguage.English);
        Localizer.Initialize(root);
        Localizer.Change(AppLanguage.Portuguese);
        Assert.Equal("pt", File.ReadAllText(Path.Combine(root, "ui-language.txt")));
        Localizer.Use(AppLanguage.English);
        Localizer.Initialize(root);
        Assert.Equal(AppLanguage.Portuguese, Localizer.Current);
        Localizer.Change(AppLanguage.English);
        Assert.Equal("en", File.ReadAllText(Path.Combine(root, "ui-language.txt")));
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public void TranslationPreservesUnknownTextTechnicalIdentifiersAndHistoricalPayload()
    {
        var audit = Samples.Audit(DateTimeOffset.UnixEpoch);
        var before = JsonSerializer.Serialize(audit, AuditJson.Options);
        Localizer.Use(AppLanguage.English);
        Assert.Equal("Cancel", Loc.T("Cancelar"));
        foreach (var value in new[] { audit.Id.ToString("D"), "SYN-PREF-01", "DiagnosticStatus.Partial", "original fixture text" })
            Assert.Equal(value, Loc.T(value));
        Localizer.Use(AppLanguage.Portuguese);
        Assert.Equal("Cancelar", Loc.T("Cancelar"));
        Assert.Equal(before, JsonSerializer.Serialize(audit, AuditJson.Options));
    }

    [Fact]
    public void InterpolationTranslatesLiteralsAndPreservesFormattingAndValues()
    {
        Localizer.Use(AppLanguage.English);
        var id = Guid.NewGuid();
        Assert.Equal("Adapter" + id, Loc.F($"Adaptador{id}"));
        Assert.Equal("CPU " + 12.5.ToString("F1", CultureInfo.CurrentCulture), Loc.F($"CPU {12.5:F1}"));
        Assert.Equal("CPU    12", Loc.F($"CPU {12,5}"));
        Assert.Equal("CPU ", Loc.F($"CPU {(string?)null}"));
    }

    [Fact]
    public void EveryXamlTextResourceExistsInBothLanguages()
    {
        var english = Strings.CreateDictionary(AppLanguage.English);
        var portuguese = Strings.CreateDictionary(AppLanguage.Portuguese);
        foreach (var path in Directory.GetFiles(TestRepository.Combine("src", "SentinelCore.App"), "*.xaml"))
        {
            var document = XDocument.Load(path);
            foreach (var attribute in document.Descendants().Attributes())
            {
                var match = Regex.Match(attribute.Value, @"^\{DynamicResource (T\d+)\}$");
                if (!match.Success) continue;
                var key = match.Groups[1].Value;
                Assert.True(english.Contains(key), path + ": " + key);
                Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(english[key])));
                Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(portuguese[key])));
            }
        }
    }

    [Fact]
    public void LanguageHandlerReservesJourneyBeforeChangingPreference()
    {
        var source = TestRepository.AppSource("MainWindow.xaml.cs");
        var start = source.IndexOf("void OnLanguageChanged(", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task ReloadAsync", start, StringComparison.Ordinal);
        var handler = source[start..end];
        var acquire = handler.IndexOf("using var operation = TryBeginProductOperation()", StringComparison.Ordinal);
        var change = handler.IndexOf("Localizer.Change(language)", StringComparison.Ordinal);
        Assert.True(acquire >= 0 && acquire < change, "Reserve before updating resources or the preference.");
        Assert.Contains("LanguageRow.IsEnabled = !operations.IsBusy", TestRepository.AppSource("MainWindow.Operations.cs"));
    }

    public void Dispose()
    {
        Localizer.Use(AppLanguage.English);
        Directory.Delete(root, true);
    }
}
