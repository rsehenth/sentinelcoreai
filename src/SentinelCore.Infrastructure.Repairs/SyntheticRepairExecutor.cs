using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Repairs;

/// <summary>
/// Executor exclusivamente sintético. Não inicia processo, não abre volume, não consulta o Windows e não
/// solicita elevação. Cada saída é marcada como fixture e o registro final carrega <c>IsSynthetic=true</c>.
/// </summary>
public sealed class SyntheticRepairExecutor : IRepairExecutor
{
    private readonly TimeProvider time;

    public SyntheticRepairExecutor(TimeProvider? time = null) =>
        this.time = time ?? TimeProvider.System;

    public async Task<RepairExecutionOutcome> ExecuteAsync(RepairExecutionRequest request,
        RepairExecutionOptions options, IProgress<RepairProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (request.Operation.Tool != request.Invocation.Tool ||
            !string.Equals(request.Operation.ExecutableName, request.Invocation.ExecutableName,
                StringComparison.Ordinal))
            return Failed(request, "Invocação sintética não corresponde ao catálogo fechado.");

        var scenario = options.SyntheticScenario ?? RepairFixtureScenario.SuccessNoIssues;
        var started = time.GetUtcNow();
        var limitations = new List<string>
        {
            RepairGuidance.SyntheticDisclaimer,
            "Nenhum processo real foi iniciado e o resultado não representa o estado do Windows.",
            RepairGuidance.NoRollback,
            RepairGuidance.LocalOnly
        };

        progress?.Report(new RepairProgress("Preparando fixture sintética", TimeSpan.Zero,
            $"{request.Operation.DisplayName} · cenário {scenario}"));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var cancelledAt = time.GetUtcNow();
            return new RepairExecutionOutcome(true, false, true, false, null,
                "Cancelamento solicitado antes da execução sintética.", false,
                BuildLines([RepairGuidance.SyntheticDisclaimer, "Cancelamento solicitado."]),
                started, cancelledAt, NonNegative(cancelledAt - started), true,
                limitations.Concat([RepairGuidance.CancellationLimitation]).ToList());
        }

        if (scenario == RepairFixtureScenario.Cancelled)
        {
            var cancelledAt = time.GetUtcNow();
            return new RepairExecutionOutcome(true, false, true, false, null,
                "Cenário sintético de cancelamento.", false,
                BuildLines([RepairGuidance.SyntheticDisclaimer,
                    "Operation cancelled by user request.", "Cancelamento solicitado."]),
                started, cancelledAt, NonNegative(cancelledAt - started), true, limitations);
        }

        var fixture = RepairFixtures.Get(request.Operation, scenario);
        progress?.Report(new RepairProgress("Executando fixture sintética", TimeSpan.Zero, null));
        await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);

        var lines = new List<string> { RepairGuidance.SyntheticDisclaimer };
        lines.AddRange(fixture.Lines);
        var output = BuildLines(lines);
        var completedAt = time.GetUtcNow();
        var duration = NonNegative(completedAt - started);

        if (scenario == RepairFixtureScenario.Interrupted)
        {
            progress?.Report(new RepairProgress("Interrompida", duration, null));
            return new RepairExecutionOutcome(true, false, false, true, null,
                "Cenário sintético de interrupção.", fixture.OutputTruncated, output,
                started, completedAt, duration, true,
                limitations.Concat(["Execução interrompida; o resultado é indeterminado."]).ToList());
        }

        progress?.Report(new RepairProgress("Concluída", duration, null));
        return new RepairExecutionOutcome(true, true, false, false, fixture.ExitCode, null,
            fixture.OutputTruncated, output, started, completedAt, duration, true, limitations);
    }

    private static RepairExecutionOutcome Failed(RepairExecutionRequest request, string error)
    {
        var now = DateTimeOffset.UtcNow;
        return new RepairExecutionOutcome(false, false, false, false, null, error, false, [],
            now, now, TimeSpan.Zero, true,
            [RepairGuidance.SyntheticDisclaimer, error]);
    }

    private static IReadOnlyList<RepairOutputLine> BuildLines(IReadOnlyList<string> lines) =>
        lines.Select((line, index) => new RepairOutputLine(index + 1, line)).ToList();

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
