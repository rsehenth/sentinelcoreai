using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class OperationCatalog : IOperationCatalog
{
    private readonly Dictionary<string, OperationDescriptor> operations;

    public OperationCatalog(string version, IEnumerable<OperationDescriptor> descriptors)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("Versão do catálogo é obrigatória.");
        Version = version.Trim();
        var items = descriptors?.ToList() ?? throw new InvalidDataException("Catálogo ausente.");
        if (items.Count == 0) throw new InvalidDataException("Catálogo vazio não é permitido.");

        operations = new Dictionary<string, OperationDescriptor>(StringComparer.Ordinal);
        foreach (var operation in items)
        {
            Validate(operation);
            if (!operations.TryAdd(operation.Id, operation))
                throw new InvalidDataException($"Operação duplicada no catálogo: {operation.Id}.");
        }

        All = operations.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
        ContentHash = SafetyHashing.CatalogContentHash(All);
    }

    public string Version { get; }
    public string ContentHash { get; }
    public IReadOnlyList<OperationDescriptor> All { get; }

    public OperationDescriptor Get(string operationId) =>
        TryGet(operationId, out var descriptor) && descriptor is not null
            ? descriptor
            : throw new KeyNotFoundException($"Operação desconhecida no catálogo fechado: {operationId}.");

    public bool TryGet(string operationId, out OperationDescriptor? descriptor)
    {
        descriptor = null;
        return !string.IsNullOrWhiteSpace(operationId) && operations.TryGetValue(operationId, out descriptor);
    }

    private static void Validate(OperationDescriptor operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Id) || operation.Id.Length > 64 ||
            operation.Id.Any(char.IsWhiteSpace))
            throw new InvalidDataException("ID de operação inválido.");
        if (operation.Version <= 0)
            throw new InvalidDataException($"{operation.Id}: versão deve ser positiva.");
        if (string.IsNullOrWhiteSpace(operation.Category) || string.IsNullOrWhiteSpace(operation.DisplayName))
            throw new InvalidDataException($"{operation.Id}: categoria e nome são obrigatórios.");
        if (!int.TryParse(operation.Compatibility.MinimumBuild, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var minimum) || minimum <= 0)
            throw new InvalidDataException($"{operation.Id}: build mínimo inválido.");
        if (operation.Compatibility.MaximumBuild is not null &&
            (!int.TryParse(operation.Compatibility.MaximumBuild, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var maximum) || maximum < minimum))
            throw new InvalidDataException($"{operation.Id}: build máximo inválido.");
        if (operation.Targets.Count == 0)
            throw new InvalidDataException($"{operation.Id}: nenhum alvo declarado.");
        if (operation.Changes.Count == 0)
            throw new InvalidDataException($"{operation.Id}: nenhuma alteração declarada.");
        if (operation.Targets.Count != operation.Changes.Count)
            throw new InvalidDataException($"{operation.Id}: cada alvo deve ter exatamente uma alteração.");
        if (operation.Changes.Select(change => change.Target.Identity).Distinct(StringComparer.Ordinal).Count() !=
            operation.Changes.Count)
            throw new InvalidDataException($"{operation.Id}: alteração duplicada para o mesmo alvo.");
        if (!operation.Protection.RequiresBackup)
            throw new InvalidDataException($"{operation.Id}: toda operação exige backup observado.");
        if (operation.Risk == RiskLevel.NotValidated && operation.Availability == OperationAvailability.Enabled)
            throw new InvalidDataException($"{operation.Id}: risco não validado não pode estar habilitado.");

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in operation.Targets)
        {
            if (!OperationTargetRoots.IsAllowed(target.Root))
                throw new InvalidDataException($"{operation.Id}: alvo fora das raízes permitidas.");
            if (string.IsNullOrWhiteSpace(target.Path))
                throw new InvalidDataException($"{operation.Id}: caminho de alvo vazio.");
            if (string.Equals(target.Root, OperationTargetRoots.CurrentUser, StringComparison.Ordinal) &&
                !OperationTargetRoots.IsWellFormedCurrentUserTarget(target))
                throw new InvalidDataException($"{operation.Id}: alvo HKCU malformado: {target.Identity}.");
            if (!targets.Add(target.Identity))
                throw new InvalidDataException($"{operation.Id}: alvo duplicado {target.Identity}.");
        }

        foreach (var change in operation.Changes)
        {
            if (!targets.Contains(change.Target.Identity))
                throw new InvalidDataException($"{operation.Id}: alteração para alvo não declarado.");
            if (change.Target.ValueName is null && operation.Kind is ChangeKind.SetValue or ChangeKind.DeleteValue)
                throw new InvalidDataException($"{operation.Id}: operação de valor exige nome de valor.");
            if (operation.Kind == ChangeKind.SetValue && !change.DesiredValue.Exists)
                throw new InvalidDataException($"{operation.Id}: SetValue exige valor definido.");
            if (operation.Kind == ChangeKind.DeleteValue && change.DesiredValue.Exists)
                throw new InvalidDataException($"{operation.Id}: DeleteValue não pode definir valor.");
            if (operation.Kind == ChangeKind.DeleteKey && change.DesiredValue.Exists)
                throw new InvalidDataException($"{operation.Id}: DeleteKey não pode definir valor.");
        }

        if (operation.Dependencies.Distinct(StringComparer.Ordinal).Count() != operation.Dependencies.Count)
            throw new InvalidDataException($"{operation.Id}: dependência duplicada.");
    }
}

public static class T1SyntheticCatalog
{
    public const string CatalogVersion = "t1-synthetic-1";

    public static OperationCatalog Create() => new(CatalogVersion, Operations());

    public static IReadOnlyList<OperationDescriptor> Operations() =>
    [
        Create("SYN-PREF-01", "Preference.Test", "B", RiskLevel.Low,
            consequences: ["Preferência sintética de teste alterada."]),
        Create("SYN-PREF-02", "Preference.Empty", "value", RiskLevel.Low,
            consequences: ["Valor vazio sintético substituído por conteúdo."]),
        Create("SYN-PREF-03", "Preference.Delete", null, RiskLevel.Low, ChangeKind.DeleteValue,
            consequences: ["Valor sintético removido."]),
        Create("SYN-PREF-04", "Preference.Medium", "on", RiskLevel.Medium,
            protection: new ProtectionPolicy(true, false, true),
            consequences: ["Preferência de risco médio com ponto de restauração exigido."]),
        Create("SYN-PREF-05", "Preference.Dependent", "ready", RiskLevel.Low,
            dependencies: ["SYN-PREF-01"],
            consequences: ["Preferência dependente de outra operação."]),
        Create("SYN-PREF-90", "Preference.High", "blocked", RiskLevel.High,
            consequences: ["Operação de risco alto mantida apenas para prova de bloqueio."]),
        Create("SYN-PREF-91", "Preference.Unknown", "blocked", RiskLevel.NotValidated,
            availability: OperationAvailability.Blocked,
            consequences: ["Operação sem classificação validada."]),
        Create("SYN-PREF-92", "Preference.Machine", "blocked", RiskLevel.Medium,
            scope: OperationScope.Machine,
            consequences: ["Operação de escopo de máquina bloqueada na T1."]),
        Create("SYN-PREF-93", "Preference.Disabled", "blocked", RiskLevel.Low,
            availability: OperationAvailability.Disabled,
            consequences: ["Operação desabilitada no catálogo."]),
        Create("SYN-PREF-94", "Preference.FullTarget", "blocked", RiskLevel.Low,
            protection: new ProtectionPolicy(true, true, false),
            consequences: ["Operação que exigiria backup de chave inteira."]),
        Create("SYN-PREF-95", "Preference.NonSerializable", "blocked", RiskLevel.Low,
            consequences: ["Valor de tipo não serializável é recusado."]),
        Create("SYN-PREF-96", "Preference.NewBuild", "blocked", RiskLevel.Low,
            minimumBuild: "26100",
            consequences: ["Operação incompatível com o build observado."]),
        Create("SYN-PREF-97", "Preference.Key", null, RiskLevel.Low, ChangeKind.DeleteKey,
            consequences: ["Alvo sintético removido por completo."]),
        Create("SYN-PREF-98", "Preference.Policy", "blocked", RiskLevel.Low,
            category: "PolicyBlocked",
            consequences: ["Operação bloqueada por política restritiva."])
    ];

    private static OperationDescriptor Create(string id, string path, string? desired, RiskLevel risk,
        ChangeKind kind = ChangeKind.SetValue,
        OperationScope scope = OperationScope.CurrentUser,
        OperationAvailability availability = OperationAvailability.Enabled,
        ProtectionPolicy? protection = null,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<string>? consequences = null,
        string? minimumBuild = null,
        string category = "Preference")
    {
        var target = new OperationTarget("Synthetic", path, kind == ChangeKind.DeleteKey ? null : "Value");
        var value = kind == ChangeKind.SetValue
            ? new TargetValueState(true, "String", desired)
            : new TargetValueState(false, "String", null);
        return new OperationDescriptor(
            id,
            1,
            category,
            $"Sintética {id}",
            scope,
            kind,
            risk,
            availability,
            new OperationCompatibility(minimumBuild ?? "22000", null, true),
            [target],
            [new PlannedTarget(target, value)],
            protection ?? new ProtectionPolicy(true, false, false),
            dependencies ?? [],
            consequences ?? [],
            [],
            kind == ChangeKind.DeleteKey,
            false,
            ["Operação sintética da T1; não altera o Windows."],
            "SentinelCore T1 synthetic catalog");
    }
}
