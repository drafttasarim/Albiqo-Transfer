namespace Albiqo.Transfer.Core;

/// <summary>
/// A single physical BLE connection, not a device address or an OS pairing.
/// Once disconnected, the same instance must never authorize a reconnection.
/// Service revisions can be refreshed without treating them as a new radio link.
/// </summary>
public sealed class BluetoothLinkLifetime
{
    private readonly CancellationTokenSource disconnected = new();
    private int ended;
    private long serviceRevision;

    public CancellationToken ConnectionLost => disconnected.Token;
    public bool IsActive => Volatile.Read(ref ended) == 0;
    public long ServiceRevision => Volatile.Read(ref serviceRevision);

    public void ObserveConnection(bool connected)
    {
        if (!connected) End();
    }

    public void ObserveServiceChange() => Interlocked.Increment(ref serviceRevision);

    public void End()
    {
        if (Interlocked.Exchange(ref ended, 1) == 0) disconnected.Cancel();
    }
}
