using Albiqo.Transfer.Core;

internal static class ConnectionPairingChecks
{
    internal static readonly (string Name, Func<Task> Body)[] All =
    [
        ("Pairing code survives repeated operations on one live link", () =>
        {
            using var link = new CancellationTokenSource();
            using var pairing = new ConnectionPairingCode("123456", link.Token);
            for (var i = 0; i < 20; i++)
                Require(pairing.IsActive && pairing.TryGet(out var pin) && pin == "123456");
            using var stoppedOperation = new CancellationTokenSource();
            stoppedOperation.Cancel();
            Require(pairing.TryGet(out var remaining) && remaining == "123456");
            return Task.CompletedTask;
        }),
        ("Physical disconnect irrevocably forgets the code before reconnect", () =>
        {
            using var firstLink = new CancellationTokenSource();
            using var pairing = new ConnectionPairingCode("123456", firstLink.Token);
            firstLink.Cancel();
            using var newLink = new CancellationTokenSource();
            Require(!pairing.IsActive && !pairing.TryGet(out var pin) && pin == "");
            using var newPairing = new ConnectionPairingCode("654321", newLink.Token);
            Require(newPairing.TryGet(out var newPin) && newPin == "654321");
            Require(!pairing.TryGet(out _));
            return Task.CompletedTask;
        }),
        ("An already lost link cannot acquire cached authorization", () =>
        {
            using var link = new CancellationTokenSource(); link.Cancel();
            using var pairing = new ConnectionPairingCode("123456", link.Token);
            Require(!pairing.IsActive && !pairing.TryGet(out _));
            return Task.CompletedTask;
        }),
        ("Explicit disconnect and rejected authorization erase the remembered code", () =>
        {
            using var pairing = new ConnectionPairingCode("123456", CancellationToken.None);
            pairing.Clear(); Require(!pairing.TryGet(out _));
            pairing.Dispose(); Require(!pairing.IsActive);
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool value) { if (!value) throw new Exception("Connection pairing lifecycle regression."); }
}
