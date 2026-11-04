using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Modifications;

public sealed class SyntheticSystemProfileReader : ISystemProfileReader
{
    private readonly object gate = new();
    private SystemProfile profile;

    public SyntheticSystemProfileReader(SystemProfile profile) => this.profile = profile;

    public static SystemProfile Default(DateTimeOffset now, bool windows11 = true, int build = 22631) =>
        new("Synthetic Windows 11", build, windows11, false, [], [], [], "SyntheticSystemProfileReader",
            now, []);

    public SystemProfile Read()
    {
        lock (gate) return profile;
    }

    public void Update(SystemProfile value)
    {
        lock (gate) profile = value;
    }
}

public sealed class SyntheticProtectionProvider : IProtectionProvider
{
    private readonly object gate = new();
    private bool restorePointAvailable;
    private string? reason;

    public SyntheticProtectionProvider(bool restorePointAvailable = true, string? reason = null)
    {
        this.restorePointAvailable = restorePointAvailable;
        this.reason = reason;
    }

    public void SetRestorePointAvailable(bool available, string? explanation = null)
    {
        lock (gate)
        {
            restorePointAvailable = available;
            reason = explanation;
        }
    }

    public Task<ProtectionStatus> ProbeAsync(ProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var limitations = policy.RequiresRestorePoint && !restorePointAvailable
                ? new[] { "Ponto de restauração indisponível no ambiente sintético." }
                : [];
            return Task.FromResult(new ProtectionStatus(restorePointAvailable, reason, limitations));
        }
    }
}
