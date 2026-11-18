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

        var window = XDocument.Load(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.xaml"));
        var expected = new Dictionary<string, string>
        {
            ["HardwareSnapshotList"] = "No inventory loaded.", ["HardwareItemList"] = "No item loaded.",
            ["EventHistoryList"] = "No investigation loaded.", ["EventGroupList"] = "No group loaded.",
            ["PerformanceHistoryList"] = "No session loaded.", ["NetworkHistoryList"] = "No session loaded.",
            ["GraphicsHistoryList"] = "No investigation loaded.", ["CorrelationHistoryList"] = "No correlation loaded.",
            ["HistoryList"] = "No audit loaded.", ["MonitoringHistoryList"] = "No session loaded.",
            ["RepairsHistoryList"] = "No simulated execution loaded."
        };
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (name, translation) in expected)
        {
            var element = Assert.Single(window.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
            var match = Regex.Match((string?)element.Attribute("Tag") ?? "", @"^\{DynamicResource (T\d+)\}$");
            Assert.True(match.Success, name);
            Assert.Equal(translation, english[match.Groups[1].Value]);
        }
        var app = XDocument.Load(TestRepository.Combine("src", "SentinelCore.App", "App.xaml"));
        foreach (var translation in new[] { "No data loaded.", "No record loaded." })
            Assert.Contains(app.Descendants().Attributes("Value"), a =>
            {
                var match = Regex.Match(a.Value, @"^\{DynamicResource (T\d+)\}$");
                return match.Success && Equals(english[match.Groups[1].Value], translation);
            });
    }

    [Fact]
    public void TweakAndRepairDynamicMessageFragmentsTranslateInBothLanguages()
    {
        foreach (var (key, translation) in new[]
        {
            (" operações cadastradas, sem elevação e sem reiniciar o Explorer. ",
                " operations registered, without elevation and without restarting Explorer. "),
            ("e não é executada no computador de desenvolvimento.", "and is not run on the development computer."),
            ("Catálogo fechado com executáveis oficiais e argumentos fixos. A execução real não é composta nesta distribuição; os cartões demonstram o fluxo com fixtures sintéticas explicitamente marcadas. Nenhuma elevação é solicitada.",
                "Closed catalog with official executables and fixed arguments. Real execution is not composed in this distribution; the cards demonstrate the flow with explicitly marked synthetic fixtures. No elevation is requested."),
            ("A operação usa uma autorização de uso único. O Windows solicita consentimento quando necessário.",
                "The operation uses a single-use authorization. Windows requests consent when needed."),
            (" · verificado: ", " · verified: "), (" · recuperação: ", " · recovery: "),
            (" · integral: ", " · complete: "),
            ("Estado atual: ", "Current state: "), ("Estado pretendido: ", "Desired state: "),
            ("Risco: ", "Risk: "), ("Comando: ", "Command: "), ("Disponibilidade: ", "Availability: "),
            ("Registro de reparo salvo novamente.", "Repair record saved again."),
            ("pronto", "ready"),
            ("Intenção sem registro final · interrompida", "Intent without a final record · interrupted"),
            ("Reparo", "Repair"), ("no estado observado", "in the observed state"),
            ("Histórico incompleto — ", "Incomplete history — ")
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(Strings.English(key)), key);
            Assert.Equal(translation, Strings.English(key));
        }
    }

    [Fact]
    public void PersonalizationAndRepairCatalogTextHasEnglishTranslations()
    {
        // Texts shown on the Personalization and Repairs cards come from Application catalogs written in Portuguese.
        // Each one must have an English entry; otherwise the English interface would show Portuguese on those pages.
        var texts = new List<string?>();
        foreach (var operation in SentinelCore.Application.T2aCatalog.Operations().Concat(SentinelCore.Application.T2bCatalog.Operations()))
        {
            texts.Add(operation.DisplayName);
            texts.Add(operation.Category);
            texts.AddRange(operation.Consequences);
            texts.AddRange(operation.Limitations);
        }
        var repairs = SentinelCore.Application.RepairCatalog.CreateDefault();
        foreach (var operation in repairs.All.Concat(repairs.DocumentedOnly))
        {
            texts.Add(operation.DisplayName);
            texts.Add(operation.Category);
            texts.Add(operation.Description);
            texts.Add(operation.ExpectedResult);
            texts.Add(operation.DocumentedOnlyReason);
            texts.AddRange(operation.Consequences);
            texts.AddRange(operation.PossibleChanges);
            texts.AddRange(operation.Limitations);
        }
        var untranslated = texts
            .Where(text => !string.IsNullOrWhiteSpace(text) && Strings.English(text!) == text)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(untranslated.Length == 0, string.Join(" || ", untranslated));
    }

    [Fact]
    public void DictionaryEntriesHaveNoConflictingTranslations()
    {
        // A remover uma entrada deslocaria os índices T#### usados pelo XAML; duplicatas são toleradas
        // apenas quando a tradução é idêntica (a última ocorrência define o valor efetivo).
        var conflicts = Strings.Entries
            .GroupBy(entry => entry.Pt, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.En).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.Empty(conflicts);
        // "\n" e "\n\n" são separadores intencionais traduzidos para si mesmos.
        Assert.All(Strings.Entries, entry =>
        {
            Assert.False(string.IsNullOrEmpty(entry.Pt));
            Assert.False(string.IsNullOrEmpty(entry.En));
        });
    }

    [Fact]
    public void DictionaryEntriesPreserveEdgeWhitespaceAndCoverSourceFragments()
    {
        // Os fragmentos de Loc.F são concatenados: espaço ou quebra de linha nas bordas deve coincidir nos dois idiomas.
        static (string Lead, string Trail) Edges(string value) =>
            (value[..(value.Length - value.TrimStart().Length)], value[value.TrimEnd().Length..]);
        var mismatches = Strings.Entries
            .Where(entry => entry.Pt.Trim().Length > 0 && Edges(entry.Pt) != Edges(entry.En))
            .Select(entry => entry.Pt)
            .ToArray();
        Assert.Empty(mismatches);
        // Fragmentos encontrados por inspeção estática que antes caíam no português no modo inglês.
        Assert.Equal("adapter", Strings.English("adaptador"));
        Assert.Equal("Search: \"", Strings.English("Busca: \""));
        Assert.StartsWith("No group matches", Strings.English("Nenhum grupo corresponde à busca \""), StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrityWarningSourcesTranslateInBothLanguages()
    {
        foreach (var (source, english) in new[]
        {
            ("Auditorias", "Audits"), ("Hardware", "Hardware"), ("Eventos", "Events"),
            ("Desempenho", "Performance"), ("Rede", "Network"), ("Gráficos & GPU", "Graphics & GPU"),
            ("Correlação", "Correlation"), ("Monitoramento", "Monitoring"),
            ("Personalização", "Personalization"), ("Reparos", "Repairs")
        })
        {
            Localizer.Use(AppLanguage.English);
            Assert.Equal(english, Loc.T(source));
            Localizer.Use(AppLanguage.Portuguese);
            Assert.Equal(source, Loc.T(source));
        }
        Localizer.Use(AppLanguage.English);
    }

    [Fact]
    public void DiagnosticRowLabelEntriesTranslateInBothLanguages()
    {
        foreach (var (portuguese, english) in new[]
        {
            ("\nValidade: ", "\nValidity: "), ("\nÚltima tentativa: ", "\nLast attempt: "),
            ("Nenhuma auditoria", "No audit"), ("Atualizada", "Up to date"),
            ("Próxima do vencimento", "Approaching expiration"), ("Desatualizada", "Outdated"),
            ("Sem auditoria concluída", "No completed audit"), ("Data futura inválida", "Invalid future date"),
            ("Presente", "Present"), ("Desconectado", "Disconnected"), ("Indeterminado", "Indeterminate"),
            ("Adicionado", "Added"), ("Removido", "Removed"), ("Driver alterado", "Driver changed"),
            ("Firmware alterado", "Firmware changed"), ("Capacidade alterada", "Capacity changed"),
            ("Configuração alterada", "Configuration changed"), ("Estado alterado", "State changed"),
            ("Processador", "Processor"), ("Memória", "Memory"), ("Placa-mãe e BIOS", "Motherboard and BIOS"),
            ("Placa de vídeo", "Video card"), ("Monitores", "Monitors"), ("Armazenamento", "Storage"),
            ("Rede", "Network"), ("Áudio", "Audio"), ("USB e periféricos", "USB and peripherals"),
            ("Drivers", "Drivers"), ("Outros dispositivos", "Other devices"),
            ("Estabilidade e Eventos", "Stability and Events"), ("Gráficos e GPU", "Graphics and GPU"),
            ("Inventário de hardware", "Hardware inventory"), ("Monitoramento", "Monitoring"),
            ("Auditoria completa", "Full audit"), ("Auditoria básica", "Basic audit"),
            (" etapas registradas · ", " steps recorded · "), (" concluídas · ", " completed · "),
            (" parciais · ", " partial · "), (" indisponíveis ou com falha · ", " unavailable or failed · "),
            (" canceladas", " cancelled"),
            ("24 horas", "24 hours"), ("7 dias", "7 days"), ("30 dias", "30 days"), ("Personalizado", "Custom"),
            ("Todas", "All"), ("Energia", "Power"), ("Aplicativos", "Applications"), ("Serviços", "Services"),
            ("Outros", "Others"), ("Log de Eventos", "Event Log"), ("Reparos", "Repairs"),
            ("não aplicável", "not applicable"), ("verificada", "verified"), ("afetada", "affected"),
            ("indeterminada", "indeterminate"), ("Não avaliada", "Not assessed"),
            ("Qualidade afetada · ", "Affected quality · "), (" relatórios WER fora do período", " WER reports outside the period"),
            ("Qualidade verificada · ", "Verified quality · "), (" relatório(s) WER no período", " WER report(s) in the period"),
            ("Qualidade indeterminada · ", "Undetermined quality · "), (" sem horário efetivo", " without an effective time"),
            ("Qualidade não aplicável · sem relatórios WER", "Quality not applicable · no WER reports"),
            ("30 segundos", "30 seconds"), ("1 minuto", "1 minute"), ("3 minutos", "3 minutes"),
            ("CPU total", "Total CPU"), ("CPU usuário", "user CPU"), ("CPU kernel", "Kernel CPU"),
            ("Fila do processador", "Processor queue"), ("Memória disponível", "Available memory"),
            ("Memória usada", "Used memory"), ("Commit", "Commit"), ("Páginas por segundo", "Pages per second"),
            ("Auto-sobrecarga · CPU média", "Self-overload · average CPU"),
            ("Auto-sobrecarga · duração da coleta", "Self-overload · collection duration"),
            ("Leitura · ", "Read · "), ("completa", "complete"), ("parcial", "partial"),
            ("indisponível", "unavailable"), ("em andamento", "in progress"), ("concluída", "completed"),
            ("cancelada", "cancelled"), ("falhou", "failed"), ("não suportada", "not supported"),
            ("CPU sustentada", "Sustained CPU"), ("memória disponível baixa", "low available memory"),
            ("margem de commit", "commit margin"), ("crescimento de memória privada", "private memory growth"),
            ("latência de disco", "disk latency"), ("pico de I/O", "I/O peak"),
            ("Concluída com cobertura parcial", "Completed with partial coverage"),
            ("Outro evento do provedor Display", "Other event from the Display provider"),
            ("Evento de driver gráfico", "Graphics driver event"), ("Evento gráfico", "Graphics event"),
            ("LiveKernelEvent (outro código)", "LiveKernelEvent (other code)"), ("Relatório gráfico", "Graphics report"),
            ("reinício do driver", "driver restart"), ("múltiplos adaptadores", "multiple adapters"),
            ("estado de adaptador", "adapter state"), ("topologia de monitores", "monitor topology"),
            ("alteração de adaptador", "adapter change"), ("alteração de driver", "driver change"),
            ("alteração de monitor", "monitor change"), ("metadados WER", "WER metadata"),
            ("Adaptador adicionado", "Adapter added"), ("Adaptador removido", "Adapter removed"),
            ("Monitor adicionado", "Monitor added"), ("Monitor removido", "Monitor removed"),
            ("conexão indisponível", "connection unavailable"), ("ativo", "on"), ("inativo", "off"),
            ("data indisponível", "date unavailable"), ("INF indisponível", "INF unavailable"),
            ("aplicativo indisponível", "application unavailable"),
            ("SIMULAÇÃO — NENHUM APLICATIVO REAL SERÁ REMOVIDO.", "SIMULATION — NO REAL APPLICATION WILL BE REMOVED."),
            ("T3C — motor nativo compilável; execução real bloqueada e não validada.",
                "T3C — compilable native engine; real execution blocked and not validated."),
            ("Reparo", "Repair")
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(Strings.English(portuguese)), portuguese);
            Assert.Equal(english, Strings.English(portuguese));
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
