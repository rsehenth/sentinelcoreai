using System.Collections.Concurrent;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class AuthorizationService
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private readonly TimeProvider time;
    private readonly TimeSpan lifetime;
    private readonly ConcurrentDictionary<Guid, AuthorizationToken> issued = new();
    private readonly ConcurrentDictionary<Guid, byte> consumed = new();

    public AuthorizationService(TimeProvider? time = null, TimeSpan? lifetime = null)
    {
        this.time = time ?? TimeProvider.System;
        this.lifetime = lifetime ?? DefaultLifetime;
        if (this.lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Validade da autorização deve ser positiva.");
    }

    public TimeSpan Lifetime => lifetime;

    public AuthorizationToken Issue(OperationPlan plan, string userConfirmationId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!SafetyText.IsValidConfirmation(userConfirmationId))
            throw new InvalidOperationException("Autorização exige confirmação explícita, curta e sem caracteres de controle.");
        var now = time.GetUtcNow();
        var token = new AuthorizationToken(Guid.NewGuid(), plan.TransactionId, plan.PlanId,
            plan.OperationId, plan.OperationVersion, plan.PlanHash, plan.ObservedStateHash,
            userConfirmationId.Trim(), now, now + lifetime, false);
        issued[token.AuthorizationId] = token;
        return token;
    }

    public AuthorizationValidation Validate(AuthorizationToken token, OperationPlan plan,
        string currentObservedStateHash)
    {
        if (token is null || plan is null)
            return new AuthorizationValidation(false, BlockerCode.AuthorizationMissing,
                "Autorização ou plano ausente.");
        if (!issued.TryGetValue(token.AuthorizationId, out var known) || known != token)
            return new AuthorizationValidation(false, BlockerCode.AuthorizationMissing,
                "Autorização não emitida por este Safety Engine.");
        if (consumed.ContainsKey(token.AuthorizationId))
            return new AuthorizationValidation(false, BlockerCode.AuthorizationReused,
                "Autorização já consumida.");
        if (time.GetUtcNow() > token.ExpiresAtUtc)
            return new AuthorizationValidation(false, BlockerCode.AuthorizationExpired,
                "Autorização expirada.");
        if (token.PlanId != plan.PlanId || token.TransactionId != plan.TransactionId ||
            token.OperationId != plan.OperationId || token.OperationVersion != plan.OperationVersion)
            return new AuthorizationValidation(false, BlockerCode.PlanMismatch,
                "Autorização não corresponde à operação e ao plano.");
        if (!string.Equals(token.PlanHash, plan.PlanHash, StringComparison.Ordinal) ||
            !string.Equals(token.ObservedStateHash, plan.ObservedStateHash, StringComparison.Ordinal))
            return new AuthorizationValidation(false, BlockerCode.PlanMismatch,
                "Autorização não corresponde ao plano efetivamente apresentado.");
        if (!string.Equals(currentObservedStateHash, plan.ObservedStateHash, StringComparison.Ordinal))
            return new AuthorizationValidation(false, BlockerCode.StateChanged,
                "Estado observado divergiu do plano; nova simulação é necessária.");
        return new AuthorizationValidation(true, BlockerCode.None, null);
    }

    public bool Consume(AuthorizationToken token) =>
        issued.ContainsKey(token.AuthorizationId) && consumed.TryAdd(token.AuthorizationId, 0);
}
