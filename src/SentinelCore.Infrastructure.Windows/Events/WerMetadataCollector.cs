using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal sealed class WerMetadataCollector(InvestigationLimits? limits = null) : IEventEvidenceCollector
{
    private readonly InvestigationLimits limits = limits ?? InvestigationLimits.Default;

    public string Id => "wer-metadata";
    public string Version => "1.0.0";
    public string Source => "Windows Error Reporting (metadados)";

    public Task<EventCollectorResult> CollectAsync(Guid investigationId, EventQueryWindow window,
        CancellationToken cancellationToken) => Task.Run(() => Collect(window, cancellationToken), cancellationToken);

    private EventCollectorResult Collect(EventQueryWindow window, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var reports = new List<RawWerReport>();
        var channels = new List<EventChannelState>();
        var limitations = new List<string>();
        var limitHit = false;
        var partialReports = 0;
        var accessibleStores = 0;
        var unknownTime = 0;

        foreach (var store in Stores())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > limits.CollectionTime)
            {
                limitHit = true;
                limitations.Add("Tempo máximo de coleta WER atingido; a contagem é parcial.");
                break;
            }
            if (reports.Count >= limits.MaxTotalWerReports)
            {
                limitHit = true;
                limitations.Add($"Limite total de {limits.MaxTotalWerReports} relatórios WER atingido; a contagem é parcial.");
                break;
            }

            var channel = $"WER — {store.Source}/{store.Store}";
            if (!Directory.Exists(store.Path))
            {
                channels.Add(new(channel, false, false, false, "Repositório ausente; isso é normal em alguns sistemas."));
                continue;
            }

            string[] folders;
            try
            {
                folders = Directory.EnumerateDirectories(store.Path, "*", SearchOption.TopDirectoryOnly)
                    // Newest first so a capped scan still covers the requested recent window.
                    .Select(path => new DirectoryInfo(path))
                    .OrderByDescending(info => info.CreationTimeUtc)
                    .Take(limits.MaxWerReportsPerStore + 1)
                    .Select(info => info.FullName)
                    .ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                channels.Add(new(channel, true, true, false, "Acesso negado; elevação não solicitada."));
                limitations.Add($"Acesso negado ao repositório WER {store.Source}/{store.Store}; nenhuma elevação foi solicitada.");
                continue;
            }
            catch (Exception error) when (error is IOException)
            {
                channels.Add(new(channel, true, true, false, $"Repositório indisponível ({error.GetType().Name})."));
                limitations.Add($"Repositório WER {store.Source}/{store.Store} indisponível ({error.GetType().Name}): {error.Message}");
                continue;
            }

            accessibleStores++;
            channels.Add(new(channel, true, true, true, null));
            var perStore = 0;
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reports.Count >= limits.MaxTotalWerReports || perStore >= limits.MaxWerReportsPerStore)
                {
                    limitHit = true;
                    limitations.Add($"Limite de {limits.MaxWerReportsPerStore} relatórios em {store.Source}/{store.Store} foi atingido; a contagem é parcial.");
                    break;
                }
                try
                {
                    var report = WerReportParser.Parse(store.Source, store.Store, folder, limits.MaxWerFileBytes);
                    perStore++;
                    if (!IsWithin(report, window, out var timeKnown))
                    {
                        if (!timeKnown) unknownTime++;
                        continue;
                    }
                    reports.Add(report);
                    if (report.Confidence != EvidenceConfidence.High || report.Limitations.Count > 0) partialReports++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    InvalidOperationException or ArgumentException)
                {
                    limitations.Add($"Relatório WER em {store.Source}/{store.Store}/{Path.GetFileName(folder)} " +
                        $"não recuperado ({error.GetType().Name}): {error.Message}");
                }
            }
        }

        if (partialReports > 0)
            limitations.Add($"{partialReports} relatório(s) usaram metadados parciais; as limitações individuais foram preservadas em cada evidência.");
        if (unknownTime > 0)
            limitations.Add($"{unknownTime} relatório(s) WER sem horário confiável foram excluídos do período; a contagem é parcial.");
        if (accessibleStores == 0)
            limitations.Add("Nenhum repositório WER pôde ser lido; a ausência de relatórios não é conclusiva.");
        if (timer.Elapsed > limits.CollectionTime)
        {
            limitHit = true;
            limitations.Add("Tempo máximo de coleta WER atingido; a contagem é parcial.");
        }

        var status = limitations.Count == 0 && !limitHit ? EventSourceStatus.Completed : EventSourceStatus.Partial;
        return new(Id, Version, Source, started, DateTimeOffset.UtcNow, timer.Elapsed, status, 0,
            reports.Count, limitHit, [], reports, channels,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static bool IsWithin(RawWerReport report, EventQueryWindow window, out bool timeKnown)
    {
        var observed = report.EventTimeUtc ?? report.CreationTimeUtc;
        timeKnown = observed is not null;
        return observed is { } at && at >= window.FromUtc && at <= window.ToUtc;
    }

    private static IEnumerable<(string Source, string Store, string Path)> Stores()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return ("Sistema", "ReportArchive", Path.Combine(common, "Microsoft", "Windows", "WER", "ReportArchive"));
        yield return ("Sistema", "ReportQueue", Path.Combine(common, "Microsoft", "Windows", "WER", "ReportQueue"));
        yield return ("Usuário", "ReportArchive", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"));
        yield return ("Usuário", "ReportQueue", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"));
    }
}
