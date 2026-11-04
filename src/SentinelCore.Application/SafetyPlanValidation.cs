using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Validação compartilhada entre emissão de autorização, execução e recuperação: o plano precisa
/// corresponder ao descritor versionado do catálogo fechado, não apenas ao seu próprio hash.
/// </summary>
internal static class SafetyPlanValidation
{
    public static bool MatchesCatalog(IOperationCatalog catalog, OperationDescriptor operation,
        OperationPlan plan, out string reason)
    {
        if (!catalog.TryGet(plan.OperationId, out var descriptor) || descriptor is null)
        {
            reason = "Operação não existe no catálogo fechado.";
            return false;
        }
        if (descriptor.Version != plan.OperationVersion)
        {
            reason = "Versão da operação diverge do catálogo.";
            return false;
        }
        if (operation.Id != plan.OperationId || operation.Version != plan.OperationVersion)
        {
            reason = "Descritor apresentado diverge do plano.";
            return false;
        }
        if (plan.Risk != descriptor.Risk || plan.Scope != descriptor.Scope ||
            plan.Protection != descriptor.Protection)
        {
            reason = "Risco, escopo ou proteção do plano diverge do catálogo.";
            return false;
        }
        if (!string.Equals(plan.CatalogVersion, catalog.Version, StringComparison.Ordinal))
        {
            reason = "Versão do catálogo diverge do plano.";
            return false;
        }
        if (!string.Equals(plan.CatalogContentHash, catalog.ContentHash, StringComparison.Ordinal))
        {
            reason = "Conteúdo do catálogo diverge do plano.";
            return false;
        }
        if (plan.Changes.Count != descriptor.Changes.Count)
        {
            reason = "Plano não cobre exatamente as alterações declaradas no catálogo.";
            return false;
        }

        var expected = descriptor.Changes
            .OrderBy(item => item.Target.Identity, StringComparer.Ordinal)
            .ToList();
        var actual = plan.Changes
            .OrderBy(item => item.TargetIdentity, StringComparer.Ordinal)
            .ToList();
        for (var index = 0; index < expected.Count; index++)
        {
            var declared = expected[index];
            var planned = actual[index];
            if (!string.Equals(planned.TargetIdentity, declared.Target.Identity, StringComparison.Ordinal) ||
                !string.Equals(planned.Target.Root, declared.Target.Root, StringComparison.Ordinal) ||
                !string.Equals(planned.Target.Path, declared.Target.Path, StringComparison.Ordinal) ||
                !string.Equals(planned.Target.ValueName, declared.Target.ValueName, StringComparison.Ordinal) ||
                planned.Kind != descriptor.Kind ||
                planned.AfterTargetExists != (descriptor.Kind != ChangeKind.DeleteKey) ||
                planned.After != declared.DesiredValue)
            {
                reason = $"Alteração planejada diverge do catálogo em {planned.TargetIdentity}.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }
}
