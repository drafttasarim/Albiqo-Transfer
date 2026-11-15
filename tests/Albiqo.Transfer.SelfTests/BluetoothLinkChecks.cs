using Albiqo.Transfer.Core;

internal static class BluetoothLinkChecks
{
    public static IEnumerable<(string Name, Func<Task> Body)> All => new (string, Func<Task>)[]
    {
        ("Bluetooth radio loss invalidates the original connection even after same-device reconnect", () =>
        {
            var link = new BluetoothLinkLifetime();
            var revoked = 0;
            using var registration = link.ConnectionLost.Register(() => Interlocked.Increment(ref revoked));
            link.ObserveConnection(true);
            Assert(link.IsActive && !link.ConnectionLost.IsCancellationRequested);
            link.ObserveConnection(false);
            link.ObserveConnection(true);
            link.End();
            Assert(!link.IsActive && link.ConnectionLost.IsCancellationRequested && revoked == 1);
            // A new device object/connection needs a fresh authorization, even at the same address.
            var replacement = new BluetoothLinkLifetime();
            Assert(replacement.IsActive && !ReferenceEquals(link, replacement));
            replacement.End();
            return Task.CompletedTask;
        }),
        ("Bluetooth service revisions preserve the physical link but revoke stale GATT handles", () =>
        {
            var link = new BluetoothLinkLifetime();
            var oldRevision = link.ServiceRevision;
            link.ObserveServiceChange();
            link.ObserveServiceChange();
            Assert(link.IsActive && !link.ConnectionLost.IsCancellationRequested && link.ServiceRevision == oldRevision + 2);
            link.End();
            Assert(!link.IsActive && link.ConnectionLost.IsCancellationRequested);
            return Task.CompletedTask;
        }),
        ("Bluetooth disconnect interrupts pending work immediately without waiting for request timeout", async () =>
        {
            var link = new BluetoothLinkLifetime();
            using var caller = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, link.ConnectionLost);
            var pending = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            link.ObserveConnection(false);
            try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Assert(!caller.IsCancellationRequested && link.ConnectionLost.IsCancellationRequested);
        })
    };

    private static void Assert(bool condition)
    {
        if (!condition) throw new Exception("Bluetooth lifetime invariant failed.");
    }
}
