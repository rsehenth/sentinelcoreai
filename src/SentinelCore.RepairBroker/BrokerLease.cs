using System.Text.Json;

namespace SentinelCore.RepairBroker;

/// <summary>Machine-wide exclusion and replay fence. It contains no repair results or user data.</summary>
internal sealed class BrokerLease : IDisposable
{
    private const int MaxClaims = 256;
    private const int MaxStateBytes = 64 * 1024;
    private readonly FileStream file;
    private BrokerLeaseState state;

    internal BrokerLease()
    {
        var path = Path.Combine(BrokerWindows.InstallDirectory, "RepairBroker.state");
        var options = new FileStreamOptions
        {
            Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.WriteThrough,
            Mode = FileMode.Open
        };
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Estado do broker contém redirecionamento.");
            file = new FileStream(path, options);
        }
        catch (FileNotFoundException)
        {
            options.Mode = FileMode.CreateNew;
            file = new FileStream(path, options);
            try
            {
                state = new(1, null, []);
                Save();
                return;
            }
            catch { file.Dispose(); throw; }
        }

        try
        {
            if (file.Length is <= 0 or > MaxStateBytes)
                throw new InvalidDataException("Estado persistente do broker incompleto.");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            state = JsonSerializer.Deserialize(bytes, BrokerJson.Default.BrokerLeaseState)
                ?? throw new InvalidDataException("Estado persistente do broker indisponível.");
            if (state.Version != 1 || state.Recent is null || state.Recent.Length > MaxClaims ||
                state.Recent.Any(claim => claim is null || claim.ExecutionId == Guid.Empty ||
                    claim.AuthorizationId == Guid.Empty || claim.ExpiresUtc.Offset != TimeSpan.Zero) ||
                state.ActiveExecution == Guid.Empty ||
                state.ActiveExecution is { } active && !state.Recent.Any(claim => claim.ExecutionId == active))
                throw new InvalidDataException("Estado persistente do broker inválido.");
        }
        catch { file.Dispose(); throw; }
    }

    internal bool HasUnresolvedExecution => state.ActiveExecution is not null;

    internal bool Reserve(Guid execution, Guid authorization, DateTimeOffset expiry)
    {
        // Retain past the authorization window so minor clock adjustments cannot reopen recent claims.
        var recent = state.Recent.Where(claim => claim.ExpiresUtc >= DateTimeOffset.UtcNow.AddHours(-1)).ToArray();
        if (HasUnresolvedExecution || recent.Length >= MaxClaims ||
            recent.Any(claim => claim.ExecutionId == execution || claim.AuthorizationId == authorization))
            return false;
        state = state with { ActiveExecution = execution, Recent = [.. recent, new(execution, authorization, expiry)] };
        Save();
        return true;
    }

    internal void ClearActive()
    {
        state = state with { ActiveExecution = null };
        Save();
    }

    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, BrokerJson.Default.BrokerLeaseState);
        file.Position = 0;
        file.Write(bytes);
        file.SetLength(bytes.Length);
        file.Flush(flushToDisk: true);
    }

    public void Dispose() => file.Dispose();
}
