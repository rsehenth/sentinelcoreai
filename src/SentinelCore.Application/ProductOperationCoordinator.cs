using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Serializes product journeys through persistence; target locks and authorization remain independent.</summary>
public sealed class ProductOperationCoordinator(IExclusiveLockFactory? locks = null)
{
    private int active;
    public bool IsBusy => Volatile.Read(ref active) != 0;
    public event Action? Changed;

    public IDisposable Acquire()
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new TargetLockedException("Aguarde a operação em andamento antes de iniciar outra jornada.");
        IDisposable? reservation = null;
        try
        {
            reservation = locks?.Acquire("SentinelCore.ProductOperation.v1");
            Changed?.Invoke();
            return new Lease(this, reservation);
        }
        catch
        {
            reservation?.Dispose();
            Interlocked.Exchange(ref active, 0);
            throw;
        }
    }

    private sealed class Lease(ProductOperationCoordinator owner, IDisposable? reservation) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { reservation?.Dispose(); }
            finally
            {
                Interlocked.Exchange(ref owner.active, 0);
                owner.Changed?.Invoke();
            }
        }
    }
}
