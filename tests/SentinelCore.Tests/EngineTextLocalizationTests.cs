using System.Text.RegularExpressions;
using SentinelCore.App.Localization;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

// Presentation only: no Application/Window is created and no record is rewritten.
[Collection("Localization")]
public sealed class EngineTextLocalizationTests
{
    private const ulong GiB = 1073741824UL;
    private static readonly Regex Portuguese = new("[áàâãéêíóôõúçÁÉÍÓÚÃÕÇ]", RegexOptions.CultureInvariant);

    private static string English(string text)
    {
        Localizer.Use(AppLanguage.English);
        return Loc.Text(text);
    }

    private static void AssertAllEnglish(IEnumerable<string> texts)
    {
        var residual = texts.Distinct(StringComparer.Ordinal)
            .Where(text => Portuguese.IsMatch(English(text)) || Regex.IsMatch(English(text), @"\{\d+\}"))
            .ToArray();
        Assert.True(residual.Length == 0, "Untranslated engine text:" + Environment.NewLine + string.Join(Environment.NewLine, residual));
    }

    [Fact]
    public void TemplatesFillValuesAndKeepEveryNumber()
    {
        Assert.Equal("Peak of 120 MB/s, 3,5× the median, for 4 s; a peak does not indicate a persistent bottleneck.",
            English("Pico de 120 MB/s, 3,5× a mediana, por 4 s; pico não indica gargalo persistente."));
        Assert.Equal("State TIME_WAIT concentrated between 30 and 50 connections (60% of TCP) for 12 s. " +
            "Concentration in one state is not an error by itself and does not prove exhaustion.",
            English("O estado TIME_WAIT concentrou entre 30 e 50 conexões (60% do TCP) por 12 s. " +
                "Concentração em um estado não é erro por si só e não comprova esgotamento."));
        Assert.Equal("SFC exit code not in the catalog: 0x5.", English("Código de saída SFC não catalogado: 0x5."));
        Assert.Equal("unavailable", English("Indisponível"));
    }

    [Fact]
    public void CompositeSentencesAndFixedValueLabelsTranslate()
    {
        Assert.Equal("High disk write latency", English("Latência de gravação de disco elevada"));
        Assert.Equal("2 without a reading (unavailable) and 1 invalid of 30 sample(s)",
            English("2 sem leitura (indisponível) e 1 inválida(s) de 30 amostra(s)"));
        Assert.Equal("1 adapter(s) reported a configuration error code or an error state. Observed values: " +
            "Adapter X — code 43, state Error. The state is the one reported by Windows; it does not replace a " +
            "hardware test and does not identify the cause of the error.",
            English("1 adaptador(es) reportaram código de erro de configuração ou estado de erro. Valores observados: " +
                "Adapter X — código 43, estado Error. O estado é o reportado pelo Windows; não substitui teste de " +
                "hardware e não identifica a causa do erro."));
    }

    [Fact]
    public void PortugueseModeUnknownTextAndDataAreNeverChanged()
    {
        const string known = "Pico de I/O";
        const string unknown = "Processo chrome.exe, texto sem modelo conhecido, 12 itens.";
        Localizer.Use(AppLanguage.Portuguese);
        Assert.Equal(known, Loc.Text(known));
        Assert.Equal(unknown, Loc.Text(unknown));
        Localizer.Use(AppLanguage.English);
        Assert.Equal(unknown, Loc.Text(unknown));
        Assert.Equal(string.Empty, Loc.Text(string.Empty));
    }

    [Fact]
    public void EveryTemplateKeepsItsHolesAndTranslatesWithoutPortugueseLeftovers()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pt, en) in EngineText.Catalog)
        {
            Assert.True(seen.Add(pt), $"Duplicate template: {pt}");
            Assert.Equal(Holes(pt), Holes(en));
            var filled = Regex.Replace(pt, @"\{(\d+)\}", hole => (11 * (int.Parse(hole.Groups[1].Value) + 1)).ToString());
            var translated = English(filled);
            Assert.DoesNotMatch(Portuguese, translated);
            foreach (Match hole in Regex.Matches(pt, @"\{(\d+)\}"))
                Assert.Contains((11 * (int.Parse(hole.Groups[1].Value) + 1)).ToString(), translated);
        }
    }

    private static string Holes(string text) => string.Join(",",
        Regex.Matches(text, @"\{(\d+)\}").Select(match => match.Groups[1].Value).OrderBy(value => value, StringComparer.Ordinal));

    [Fact]
    public void RealNetworkAnalysisTextHasNoPortugueseLeftoversInEnglish()
    {
        var quiet = Enumerable.Range(0, 25).Select(index => NetworkEvidenceAnalyzerTests.CreateSample(index, index, tcp: 100, udp: 10)).ToArray();
        var growth = Enumerable.Range(0, 25).Select(index => NetworkEvidenceAnalyzerTests.CreateSample(
            index, index, tcp: 100 + index * 12, udp: 10, ephemeralTcp: 100 + index * 12, attemptFailures: (uint)(index * 40))).ToArray();
        var texts = new List<string>();
        foreach (var samples in new[] { quiet, growth })
        {
            var result = new NetworkEvidenceAnalyzer().Analyze(samples, NetworkEvidenceAnalyzerTests.DefaultRanges, requestedDurationSeconds: 30);
            texts.AddRange(result.Notes.Select(note => note.Detail));
            foreach (var observation in result.Observations)
            {
                texts.Add(observation.Title);
                texts.Add(observation.Statement);
                texts.AddRange(observation.Limitations);
            }
        }

        Assert.Contains(texts, text => text.StartsWith("As conexões TCP observadas passaram", StringComparison.Ordinal));
        AssertAllEnglish(texts);
    }

    [Fact]
    public async Task RealGraphicsInvestigationTextHasNoPortugueseLeftoversInEnglish()
    {
        var investigation = await GraphicsSamples.Runner().RunAsync(new GraphicsInvestigationRequest(EventWindow.Last7Days));
        var texts = investigation.AnalysisNotes.Select(note => note.Detail).Concat(investigation.Limitations).ToList();
        foreach (var observation in investigation.Observations)
        {
            texts.Add(observation.Title);
            texts.Add(observation.Statement);
            texts.AddRange(observation.Limitations);
        }

        Assert.NotEmpty(texts);
        AssertAllEnglish(texts);
    }

    [Fact]
    public void RealPerformanceAnalysisTextHasNoPortugueseLeftoversInEnglish()
    {
        var quiet = Enumerable.Range(0, 40).Select(index => PerformanceSamples.Sample(index)).ToArray();
        var busy = Enumerable.Range(0, 80).Select(index => PerformanceSamples.Sample(index,
            cpu: PerformanceSamples.Cpu(total: 99),
            memory: PerformanceSamples.Memory(available: GiB, commitLimitBytes: 10 * GiB, committedBytes: 9 * GiB) with { CommitUsagePercent = 95 },
            processes: [PerformanceSamples.Process(privateBytes: (100UL + 40UL * (ulong)index) * 1048576)],
            storage: [PerformanceSamples.Disk(readLatency: 900, writeLatency: 900, read: index % 9 == 0 ? 900e6 : 1e6)])).ToArray();
        var missing = Enumerable.Range(0, 40).Select(index => PerformanceSamples.Sample(index) with { Cpu = null, Storage = [] }).ToArray();

        var texts = new List<string>();
        foreach (var samples in new[] { quiet, busy, missing })
        {
            var rules = PerformanceRuleOptions.Default;
            var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, new PerformanceStatisticsEngine().Compute(samples, rules));
            texts.AddRange(analysis.Notes.Select(note => note.Detail));
            foreach (var observation in analysis.Observations)
            {
                texts.Add(observation.Title);
                texts.Add(observation.Statement);
                texts.AddRange(observation.Limitations);
            }
        }

        Assert.True(texts.Count >= 20, "The scenarios must exercise several rules.");
        Assert.Contains(texts, text => text.StartsWith("CPU total permaneceu", StringComparison.Ordinal));
        AssertAllEnglish(texts);
    }
}
