using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Autorização de uso único e curta, vinculada ao plano, ao estado observado, ao volume e ao hash do catálogo.
/// Emitir um token não executa nada: apenas habilita o adaptador sintético a receber a solicitação.
/// </summary>
public sealed class RepairAuthorizationService
{
    private readonly Dictionary<Guid, RepairAuthorizationToken> issued = new();
    private readonly TimeProvider time;

    public RepairAuthorizationService(TimeProvider? time = null, TimeSpan? lifetime = null)
    {
        this.time = time ?? TimeProvider.System;
        Lifetime = lifetime ?? RepairLimits.AuthorizationLifetime;
        if (Lifetime <= TimeSpan.Zero || Lifetime > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    public TimeSpan Lifetime { get; }

    public RepairAuthorizationToken Issue(RepairPlan plan, string confirmation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.SimulationAllowed)
            throw new InvalidOperationException("Plano bloqueado não pode ser autorizado.");
        if (!SafetyText.IsValidConfirmation(confirmation))
            throw new InvalidOperationException("Confirmação inválida.");
        if (!string.Equals(plan.PlanHash, RepairPlanValidation.ComputeHash(plan), StringComparison.Ordinal))
            throw new InvalidOperationException("Plano inconsistente; nova preparação é necessária.");
        if (!RepairConfirmationPolicy.IsValid(plan, confirmation))
            throw new InvalidOperationException(
                "A confirmação digitada não corresponde à operação e ao plano apresentado.");

        var now = time.GetUtcNow();
        var token = new RepairAuthorizationToken(Guid.NewGuid(), plan.ExecutionId, plan.PlanId,
            plan.OperationId, plan.OperationVersion, plan.PlanHash, plan.Preflight.ObservedStateHash,
            plan.Volume?.Id, confirmation.Trim(), now, now + Lifetime, false);
        lock (issued)
        {
            foreach (var previous in issued.Where(item =>
                         item.Value.PlanId == plan.PlanId && !item.Value.Consumed ||
                         item.Value.ExpiresAtUtc < now).Select(item => item.Key).ToList())
                issued.Remove(previous);
            issued[token.AuthorizationId] = token;
        }
        return token;
    }

    public RepairAuthorizationValidation Validate(RepairAuthorizationToken? token, RepairPlan plan,
        string observedStateHash)
    {
        if (token is null || plan is null)
            return new(false, RepairBlocker.AuthorizationMissing, "Plano ou autorização ausente.");
        lock (issued)
        {
            if (!issued.TryGetValue(token.AuthorizationId, out var known))
                return new(false, RepairBlocker.AuthorizationMissing,
                    "Autorização não foi emitida por este serviço.");
            if (known.Consumed)
                return new(false, RepairBlocker.AuthorizationReused, "Autorização já consumida.");
            if (known != token)
                return new(false, RepairBlocker.AuthorizationMissing,
                    "Autorização não corresponde à emitida por este serviço.");
        }
        return ValidateBinding(token, plan, observedStateHash, time.GetUtcNow());
    }

    public static RepairAuthorizationValidation ValidateBinding(RepairAuthorizationToken token,
        RepairPlan plan, string observedStateHash, DateTimeOffset now)
    {
        if (token.IssuedAtUtc > now || now >= token.ExpiresAtUtc ||
            token.AuthorizationId == Guid.Empty || token.ExpiresAtUtc <= token.IssuedAtUtc)
            return new(false, RepairBlocker.AuthorizationExpired, "Autorização expirada.");
        if (token.PlanId != plan.PlanId || token.ExecutionId != plan.ExecutionId ||
            !string.Equals(token.OperationId, plan.OperationId, StringComparison.Ordinal) ||
            token.OperationVersion != plan.OperationVersion ||
            !string.Equals(token.PlanHash, plan.PlanHash, StringComparison.Ordinal) ||
            !string.Equals(token.ObservedStateHash, observedStateHash, StringComparison.Ordinal) ||
            !string.Equals(token.VolumeId, plan.Volume?.Id, StringComparison.Ordinal) ||
            !RepairConfirmationPolicy.IsValid(plan, token.UserConfirmationId))
            return new(false, RepairBlocker.PlanMismatch,
                "Autorização diverge do plano, do estado observado ou do volume.");
        return new(true, RepairBlocker.None, null);
    }

    public bool Consume(RepairAuthorizationToken token)
    {
        lock (issued)
        {
            if (token is null || !issued.TryGetValue(token.AuthorizationId, out var known) ||
                known.Consumed || known != token || time.GetUtcNow() >= known.ExpiresAtUtc)
                return false;
            issued[token.AuthorizationId] = known with { Consumed = true };
            return true;
        }
    }
}

public static class RepairConfirmationPolicy
{
    public static string Build(RepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var hash = plan.PlanHash.Length >= 12 ? plan.PlanHash[..12] : plan.PlanHash;
        return $"AUTORIZAR REPARO {plan.OperationId} {hash}";
    }

    public static bool IsValid(RepairPlan plan, string? confirmation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return SafetyText.IsValidConfirmation(confirmation) &&
            string.Equals(confirmation!.Trim(), Build(plan), StringComparison.Ordinal);
    }
}

public static class RepairPlanValidation
{
    public static string ComputeHash(RepairPlan plan) =>
        RepairHashing.PlanHash(plan.PlanId, plan.ExecutionId, plan.OperationId, plan.OperationVersion,
            plan.CatalogVersion, plan.CatalogContentHash, plan.Kind, plan.Risk, plan.Volume,
            plan.Invocation, plan.Preflight.ObservedStateHash);

    public static bool MatchesCatalog(IRepairCatalog catalog, RepairOperationDescriptor operation,
        RepairPlan plan, out string reason)
    {
        if (!catalog.TryGet(plan.OperationId, out var descriptor) || descriptor is null)
        {
            reason = "Operação não existe no catálogo fechado.";
            return false;
        }
        if (descriptor.Version != plan.OperationVersion || operation.Version != plan.OperationVersion ||
            operation.Id != plan.OperationId)
        {
            reason = "Versão ou identidade da operação diverge do catálogo.";
            return false;
        }
        if (!string.Equals(plan.CatalogVersion, catalog.Version, StringComparison.Ordinal) ||
            !string.Equals(plan.CatalogContentHash, catalog.ContentHash, StringComparison.Ordinal))
        {
            reason = "Versão ou conteúdo do catálogo diverge do plano.";
            return false;
        }
        if (plan.Kind != descriptor.Kind || plan.Risk != descriptor.Risk ||
            plan.Availability != descriptor.Availability)
        {
            reason = "Tipo, risco ou disponibilidade diverge do catálogo.";
            return false;
        }
        if (!RepairInvocationBuilder.Matches(descriptor, plan.Invocation, plan.Volume,
                plan.Preflight.Tool, out reason))
            return false;
        if (!string.Equals(plan.PlanHash, ComputeHash(plan), StringComparison.Ordinal))
        {
            reason = "Hash do plano não corresponde ao conteúdo apresentado.";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
