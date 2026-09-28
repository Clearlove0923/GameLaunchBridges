using System.Security.Cryptography;
using System.Text;

namespace InfinityNikki.LaunchBridge;

public sealed class BridgeInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    public bool Acquired { get; }

    private BridgeInstanceLock(Mutex mutex, bool acquired)
    {
        _mutex = mutex;
        Acquired = acquired;
    }

    public static BridgeInstanceLock TryAcquire(string configurationPath)
    {
        var normalizedPath = Path.GetFullPath(configurationPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..24];
        var mutex = new Mutex(initiallyOwned: true, $"Local\\InfinityNikkiLaunchBridge-{hash}", out var createdNew);
        return new BridgeInstanceLock(mutex, createdNew);
    }

    public void Dispose()
    {
        if (Acquired)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
