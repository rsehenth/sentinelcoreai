using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class FreshnessEngine(TimeProvider? clock = null, TimeSpan? basicLifetime = null, TimeSpan? fullLifetime = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan basic = basicLifetime ?? TimeSpan.FromDays(7);
    private readonly TimeSpan full = fullLifetime ?? TimeSpan.FromDays(30);

    public FreshnessResult Evaluate(IEnumerable<AuditRecord> history, AuditKind kind)
    {
        var attempts = history.Where(a => a.Kind == kind).OrderByDescending(a => a.CompletedUtc).ToArray();
        if (attempts.Length == 0) return new(FreshnessState.None, null, null, null);
        var now = time.GetUtcNow();
        var latest = attempts[0];
        var success = attempts.FirstOrDefault(a => a.Status == DiagnosticStatus.Completed && a.CompletedUtc <= now
            && a.StartedUtc <= a.CompletedUtc);
        var expires = success?.CompletedUtc + (kind == AuditKind.Basic ? basic : full);
        if (latest.CompletedUtc > now || latest.StartedUtc > latest.CompletedUtc)
            return new(FreshnessState.InvalidDate, success?.CompletedUtc, expires, latest.Status);
        if (success is null) return new(FreshnessState.Unsuccessful, null, null, latest.Status);
        var remaining = expires!.Value - now;
        var state = remaining <= TimeSpan.Zero ? FreshnessState.Outdated
            : remaining <= TimeSpan.FromDays(kind == AuditKind.Basic ? 1 : 3) ? FreshnessState.Expiring : FreshnessState.Current;
        return new(state, success.CompletedUtc, expires, latest.Status);
    }
}
