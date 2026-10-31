using SentinelCore.Infrastructure.Windows;
using Xunit;

namespace SentinelCore.Tests;

public sealed class StorageIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "SentinelCoreTests", "identity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingDirectoryResolvesPhysicalPathWithoutChangingIt()
    {
        Directory.CreateDirectory(root);

        var identity = WindowsStorageIdentity.Resolve(root);

        Assert.Equal(Path.GetFullPath(root), identity.LogicalPath);
        Assert.NotNull(identity.PhysicalPath);
        Assert.Null(identity.Limitation);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void MissingDirectoryIsReportedAsLimitation()
    {
        var identity = WindowsStorageIdentity.Resolve(root);

        Assert.Null(identity.PhysicalPath);
        Assert.Contains("ainda não existe", identity.Limitation, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
