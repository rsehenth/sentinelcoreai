using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Orquestra a investigação de correlação: resolve a janela, recupera registros históricos já persistidos,
/// normaliza, aplica regras determinísticas e declara cobertura. Nenhum coletor é executado e nenhuma
/// configuração do Windows é alterada.
/// </summary>
public sealed class EvidenceCorrelationRunner
{
    private readonly CorrelationEvidenceReader reader;
    private readonly TimeProvider clock;
    private readonly CorrelationLimits limits;
    private readonly CorrelationEvidenceNormalizer normalizer = new();
    private readonly CorrelationEngine engine = new();
    private int running;

    public EvidenceCorrelationRunner(CorrelationEvidenceReader reader, TimeProvider? clock = null,
        CorrelationLimits? limits = null)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.clock = clock ?? TimeProvider.System;
        this.limits = limits ?? CorrelationLimits.Default;
        this.limits.Validate();
    }

    public async Task<CorrelationInvestigation> RunAsync(CorrelationRequest request,
        IProgress<CorrelationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Sources == CorrelationSources.None)
            throw new ArgumentException("Selecione ao menos uma fonte para correlacionar.", nameof(request));
        // Os limites do runner valem para normalização/análise; os registros por fonte pertencem ao leitor.
        var effectiveLimits = request.Limits ?? limits;
        effectiveLimits.Validate();
        var window = EventWindows.Resolve(request.Window, clock.GetUtcNow(), request.FromUtc, request.ToUtc);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma investigação de correlação em andamento.");
        try
        {
            return await ExecuteAsync(request, window, effectiveLimits, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private async Task<CorrelationInvestigation> ExecuteAsync(CorrelationRequest request,
        EventQueryWindow window, CorrelationLimits effectiveLimits, IProgress<CorrelationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        var timer = Stopwatch.StartNew();
        var requested = Enum.GetValues<CorrelationSource>()
            .Where(source => request.Sources.HasFlag(Flag(source))).ToArray();
        var cancelled = false;
        var readerCancelled = false;
        var limitations = new List<string>();

        progress?.Report(new(0, 3, "lendo registros históricos", null));
        CorrelationEvidenceInput input;
        try
        {
            input = await reader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            readerCancelled = true;
            input = CorrelationEvidenceInput.Empty(requested.Select(source =>
                new CorrelationSourceRead(source, CorrelationSourceStatus.Cancelled, 0, false,
                    ["Leitura interrompida pelo usuário antes da conclusão."])).ToArray());
        }

        progress?.Report(new(1, 3, "normalizando evidências", null));
        CorrelationNormalizationResult normalized;
        CorrelationAnalysisResult analysis;
        try
        {
            normalized = normalizer.Normalize(input, window, effectiveLimits, cancellationToken);
            progress?.Report(new(2, 3, "aplicando regras determinísticas", null));
            analysis = engine.Analyze(normalized.Evidence, normalized.Sources, normalized.Boot, window,
                effectiveLimits, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A leitura terminou: o trabalho já lido é preservado e a normalização/regras são concluídas sem o token,
            // em vez de descartar evidências válidas apenas porque o usuário cancelou.
            cancelled = true;
            normalized = normalizer.Normalize(input, window, effectiveLimits, CancellationToken.None);
            analysis = engine.Analyze(normalized.Evidence, normalized.Sources, normalized.Boot, window,
                effectiveLimits, CancellationToken.None);
        }

        progress?.Report(new(3, 3, "concluído", null));

        var coverage = CorrelationCoverageCalculator.Build(normalized, analysis, cancelled);
        var status = cancelled ? CorrelationStatus.Cancelled
            : coverage.Status == CorrelationCoverageStatus.Unavailable ? CorrelationStatus.Failed
            : coverage.Status == CorrelationCoverageStatus.Complete ? CorrelationStatus.Completed
            : CorrelationStatus.Partial;

        if (cancelled)
            limitations.Add(readerCancelled
                ? "Leitura interrompida pelo usuário antes da conclusão; nenhuma evidência foi consolidada e " +
                  "nenhuma foi fabricada. A cobertura permanece indisponível."
                : "Processamento interrompido pelo usuário após a leitura; as evidências consolidadas até a " +
                  $"interrupção foram preservadas e a cobertura permanece {CoverageName(coverage.Status)}.");
        if (status == CorrelationStatus.Failed)
            limitations.Add("Todas as fontes selecionadas estavam indisponíveis; nenhuma evidência pôde ser avaliada.");
        if (coverage.LimitHit)
            limitations.Add(CorrelationGuidance.LimitLimitation);
        if (normalized.UndatedEvidence > 0)
            limitations.Add($"{normalized.UndatedEvidence} evidência(s) sem horário confiável não participaram da " +
                "linha do tempo.");
        if (normalized.MergedReferences > 0)
            limitations.Add(CorrelationGuidance.DeduplicationLimitation);
        limitations.Add(normalized.Boot.Note);

        limitations.AddRange(normalized.Limitations);
        limitations.AddRange(coverage.Limitations);
        limitations.Add(CorrelationGuidance.CausalityLimitation);
        limitations.Add(CorrelationGuidance.ScopeLimitation);
        limitations.Add(CorrelationGuidance.NonRenewalLimitation);
        limitations.Add(CorrelationGuidance.AbsenceLimitation);

        var completedUtc = clock.GetUtcNow();
        timer.Stop();
        return new CorrelationInvestigation(1, id, SoftwareInfo.Version, SoftwareInfo.CorrelationSchemaVersion,
            window.Preset, window.FromUtc, window.ToUtc, started, completedUtc, timer.Elapsed, status,
            request.Sources, normalized.Sources, normalized.Boot, normalized.Evidence, normalized.Timeline,
            analysis.Relations, analysis.Notes, coverage,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string CoverageName(CorrelationCoverageStatus status) => status switch
    {
        CorrelationCoverageStatus.Complete => "completa",
        CorrelationCoverageStatus.Partial => "parcial",
        _ => "indisponível"
    };

    private static CorrelationSources Flag(CorrelationSource source) => source switch
    {
        CorrelationSource.Audits => CorrelationSources.Audits,
        CorrelationSource.Hardware => CorrelationSources.Hardware,
        CorrelationSource.Events => CorrelationSources.Events,
        CorrelationSource.Performance => CorrelationSources.Performance,
        CorrelationSource.Network => CorrelationSources.Network,
        CorrelationSource.Graphics => CorrelationSources.Graphics,
        CorrelationSource.Monitoring => CorrelationSources.Monitoring,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
}
