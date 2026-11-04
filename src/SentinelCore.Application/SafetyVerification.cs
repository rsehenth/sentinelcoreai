using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class VerificationEngine
{
    private readonly ITargetStateReader reader;

    public VerificationEngine(ITargetStateReader reader) => this.reader = reader;

    public async Task<VerificationResult> VerifyAsync(OperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<StepResult>();
        var current = new List<ObservedTargetState>();
        var limitations = new List<string>();

        foreach (var change in plan.Changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
        {
            ObservedTargetState state;
            try
            {
                state = await reader.ReadAsync(change.Target, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                state = ObservedTargetState.Unknown(change.TargetIdentity, DateTimeOffset.UtcNow,
                    $"Verificação indisponível: {error.Message}");
                limitations.Add($"{change.TargetIdentity}: {error.Message}");
            }

            current.Add(state);
            var matches = Matches(state, change);
            if (!state.IsKnown)
                limitations.Add($"{change.TargetIdentity}: estado posterior não observável.");
            steps.Add(new StepResult(change.TargetIdentity, !StateEquals(change.Before, change.After),
                matches, matches, matches ? null : "Estado posterior diverge do esperado.",
                [$"Esperado: {Describe(change.After)}; observado: {Describe(state)}"]));
        }

        var hash = SafetyHashing.ObservedStateHash(current);
        return new VerificationResult(steps.All(item => item.Verified), hash, steps,
            limitations.Distinct().ToList());
    }

    public static bool Matches(ObservedTargetState state, PlannedChange change)
    {
        if (!state.IsKnown) return false;
        if (!change.AfterTargetExists) return !state.TargetExists;
        if (!state.TargetExists) return false;
        return ValueEquals(state.Value, change.After);
    }

    public static bool ValueEquals(TargetValueState? current, TargetValueState expected)
    {
        if (current is null) return !expected.Exists;
        if (current.Exists != expected.Exists) return false;
        if (!current.Exists) return true;
        return string.Equals(current.Kind, expected.Kind, StringComparison.Ordinal) &&
               string.Equals(current.Data, expected.Data, StringComparison.Ordinal);
    }

    public static bool StateEquals(TargetValueState? first, TargetValueState? second)
    {
        if (first is null || second is null) return first is null && second is null;
        return first == second;
    }

    public static string Describe(TargetValueState? value) => value is null
        ? "indisponível"
        : !value.Exists
            ? $"{value.Kind} ausente"
            : $"{value.Kind} = {(value.Data is null ? "indisponível" : $"\"{value.Data}\"")}";

    public static string Describe(ObservedTargetState state) => !state.IsKnown
        ? "indisponível"
        : !state.TargetExists
            ? "alvo inexistente"
            : Describe(state.Value);
}
