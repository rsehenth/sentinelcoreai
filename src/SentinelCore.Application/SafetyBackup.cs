using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class BackupService
{
    private readonly ITransactionJournalStore store;
    private readonly TimeProvider time;

    public BackupService(ITransactionJournalStore store, TimeProvider? time = null)
    {
        this.store = store;
        this.time = time ?? TimeProvider.System;
    }

    public BackupRecord Create(OperationPlan plan, IReadOnlyList<ObservedTargetState> observed, int sequence)
    {
        var byIdentity = observed.ToDictionary(item => item.TargetIdentity, StringComparer.Ordinal);
        var entries = new List<BackupEntry>();
        var limitations = new List<string>();
        var complete = !plan.Protection.RequiresFullTarget;

        if (plan.Protection.RequiresFullTarget)
            limitations.Add("Backup de chave inteira não implementado na T1.");
        if (!plan.Protection.RequiresBackup)
            limitations.Add("Plano sem backup obrigatório é recusado.");
        if (!plan.Protection.RequiresBackup) complete = false;

        foreach (var change in plan.Changes.OrderBy(item => item.TargetIdentity, StringComparer.Ordinal))
        {
            if (!byIdentity.TryGetValue(change.TargetIdentity, out var state) || !state.IsKnown)
            {
                var unknown = new BackupEntry(change.TargetIdentity, false, null, "");
                entries.Add(unknown with { Checksum = SafetyHashing.EntryChecksum(unknown) });
                limitations.Add($"{change.TargetIdentity}: estado observado indisponível.");
                complete = false;
                continue;
            }

            if (state.TargetExists && state.Value is null)
            {
                limitations.Add($"{change.TargetIdentity}: alvo existe sem valor observado; " +
                    "backup não é completo para reversão.");
                complete = false;
            }

            var value = state.TargetExists
                ? state.Value ?? new TargetValueState(false, "Unknown", null)
                : new TargetValueState(false, "Unknown", null);
            if (value is { Exists: true, Kind: "None" })
            {
                limitations.Add($"{change.TargetIdentity}: REG_NONE não serializável.");
                complete = false;
            }
            if (value is { Exists: true, Data: null })
            {
                limitations.Add($"{change.TargetIdentity}: valor definido sem conteúdo legível.");
                complete = false;
            }

            var entry = new BackupEntry(change.TargetIdentity, state.TargetExists, value, "",
                state.ObservedAtUtc);
            entries.Add(entry with { Checksum = SafetyHashing.EntryChecksum(entry) });
        }

        var content = SafetyHashing.BackupContentHash(entries);
        return new BackupRecord(Guid.NewGuid(), plan.TransactionId, plan.PlanId, plan.OperationId,
            plan.OperationVersion, plan.PlanHash, sequence, time.GetUtcNow(), entries, content,
            complete, limitations.Distinct().ToList());
    }

    public async Task<BackupRecord> CreateAndPersistAsync(OperationPlan plan,
        IReadOnlyList<ObservedTargetState> observed, int sequence, CancellationToken cancellationToken = default)
    {
        var backup = Create(plan, observed, sequence);
        var path = await store.WriteBackupAsync(backup, cancellationToken);
        var persisted = await store.ReadBackupAsync(path, cancellationToken);
        var validation = ValidateBackup(persisted, plan);
        if (!validation.Valid)
            throw new InvalidDataException(validation.Reason ?? "Backup não pôde ser validado.");
        return persisted;
    }

    public async Task<BackupValidation> ReadAndValidateAsync(string path, OperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var backup = await store.ReadBackupAsync(path, cancellationToken);
            return ValidateBackup(backup, plan);
        }
        catch (FileNotFoundException error)
        {
            return BackupValidation.Fail(BackupIntegrity.Missing, error.Message);
        }
        catch (DirectoryNotFoundException error)
        {
            return BackupValidation.Fail(BackupIntegrity.Missing, error.Message);
        }
        catch (InvalidDataException error)
        {
            return BackupValidation.Fail(BackupIntegrity.Corrupt, error.Message);
        }
        catch (IOException error)
        {
            return BackupValidation.Fail(BackupIntegrity.Corrupt, error.Message);
        }
    }

    public static BackupValidation ValidateBackup(BackupRecord backup, OperationPlan plan)
    {
        if (backup.TransactionId != plan.TransactionId || backup.PlanId != plan.PlanId ||
            backup.OperationId != plan.OperationId || backup.OperationVersion != plan.OperationVersion ||
            !string.Equals(backup.PlanHash, plan.PlanHash, StringComparison.Ordinal))
            return BackupValidation.Fail(BackupIntegrity.IdentityMismatch,
                "Backup pertence a outra transação, operação ou plano.");
        if (backup.Entries.Count != plan.Changes.Count)
            return BackupValidation.Fail(BackupIntegrity.Incomplete,
                "Backup não cobre todos os alvos do plano.");
        if (backup.Entries.Select(item => item.TargetIdentity).Distinct(StringComparer.Ordinal).Count() !=
            backup.Entries.Count)
            return BackupValidation.Fail(BackupIntegrity.Corrupt,
                "Backup contém identidades de alvo duplicadas.");

        var byIdentity = backup.Entries.ToDictionary(item => item.TargetIdentity, StringComparer.Ordinal);
        foreach (var entry in backup.Entries)
        {
            if (!string.Equals(entry.Checksum, SafetyHashing.EntryChecksum(entry), StringComparison.Ordinal))
                return BackupValidation.Fail(BackupIntegrity.Corrupt,
                    $"Checksum da entrada {entry.TargetIdentity} não confere.");
        }
        if (!string.Equals(backup.ContentHash, SafetyHashing.BackupContentHash(backup.Entries),
                StringComparison.Ordinal))
            return BackupValidation.Fail(BackupIntegrity.Corrupt, "Hash do backup não confere.");
        if (!backup.IsComplete)
            return BackupValidation.Fail(BackupIntegrity.Incomplete,
                "Backup incompleto para reversão segura.");

        foreach (var change in plan.Changes)
        {
            if (!byIdentity.TryGetValue(change.TargetIdentity, out var entry))
                return BackupValidation.Fail(BackupIntegrity.Incomplete,
                    $"Backup sem entrada para {change.TargetIdentity}.");
            if (entry.TargetExists != change.BeforeTargetExists)
                return BackupValidation.Fail(BackupIntegrity.IdentityMismatch,
                    $"Existência do alvo {entry.TargetIdentity} diverge do plano.");
            if (entry.Value != change.Before)
                return BackupValidation.Fail(BackupIntegrity.IdentityMismatch,
                    $"Estado anterior de {entry.TargetIdentity} diverge do plano.");
        }

        return BackupValidation.Ok(backup);
    }
}
