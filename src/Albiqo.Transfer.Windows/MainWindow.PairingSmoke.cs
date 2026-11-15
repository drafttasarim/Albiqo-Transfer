using Albiqo.Transfer.Core;

namespace Albiqo.Transfer.Windows;

public sealed partial class MainWindow
{
    // Windows-runner fixture: uses the production controller and buttons, never a real phone.
    internal async Task<PairingLifecycleSmokeResult> CheckPairingLifecycleSmokeAsync(Action capturePaired)
    {
        using var physicalLink = new CancellationTokenSource();
        var peer = new PairingSmokeTransport();
        var initialSession = await peer.AuthenticateAsync("123456", default);
        await LoadCatalogueAsync(peer, initialSession, default);
        RememberConnection(peer, null, physicalLink.Token, "123456", initialSession);
        RefreshEnabled();
        RequirePaired(peer);
        capturePaired();

        var initialAuthCalls = peer.AuthenticateCalls;
        await RunAsync(ScanAsync, showErrors: false);
        await RunAsync(ScanAsync, showErrors: false);
        RequirePaired(peer);
        RequireSmoke(peer.AuthenticateCalls == initialAuthCalls + 2 && code.Text.Length == 0,
            "Refreshing the album list must reuse the active connection's code.");

        peer.SessionUnavailable = true;
        await RunAsync(ScanAsync, showErrors: false);
        RequirePaired(peer);
        peer.SessionUnavailable = false;
        var oldCatalogueCalls = peer.CatalogueCalls;
        peer.NewAlbumSession();
        await RunAsync(token => RefreshConnectionAsync(token, refreshCatalogue: false), showErrors: false);
        RequirePaired(peer);
        RequireSmoke(peer.CatalogueCalls == oldCatalogueCalls + 1 && sessionId == peer.SessionId,
            "A new phone album/session must refresh metadata using the same live pairing.");

        var stopped = StartSuspendedSmokeOperation();
        RequireSmoke(cancel.Enabled, "Stop must be enabled during an operation.");
        cancel.PerformClick();
        await stopped;
        RequirePaired(peer);
        await RunAsync(ScanAsync, showErrors: false);
        RequirePaired(peer);

        // This exercises the linked operation token AND the real connection-lost handler.
        var disconnectedOperation = StartSuspendedSmokeOperation();
        physicalLink.Cancel();
        await disconnectedOperation;
        await WaitForSmokeStateAsync(() => transport is null && operation is null && code.Enabled);
        RequireSmoke(pairingCode is null && peer.Disposed && connect.Enabled && !disconnect.Enabled,
            "Physical connection loss must clear the code, close the transport and enable a fresh pairing.");

        using var secondLink = new CancellationTokenSource();
        var rejectedPeer = new PairingSmokeTransport();
        var secondSession = await rejectedPeer.AuthenticateAsync("123456", default);
        await LoadCatalogueAsync(rejectedPeer, secondSession, default);
        RememberConnection(rejectedPeer, null, secondLink.Token, "123456", secondSession);
        RefreshEnabled();
        rejectedPeer.RejectCode = true;
        await RunAsync(ScanAsync, showErrors: false);
        RequireSmoke(transport is null && pairingCode is null && rejectedPeer.Disposed && code.Enabled,
            "Rejected or expired authorization must not keep a remembered code.");

        using var finalLink = new CancellationTokenSource();
        var explicitlyClosed = new PairingSmokeTransport();
        var finalSession = await explicitlyClosed.AuthenticateAsync("123456", default);
        await LoadCatalogueAsync(explicitlyClosed, finalSession, default);
        RememberConnection(explicitlyClosed, null, finalLink.Token, "123456", finalSession);
        RefreshEnabled();
        disconnect.PerformClick();
        await WaitForSmokeStateAsync(() => transport is null && operation is null && code.Enabled);
        RequireSmoke(pairingCode is null && explicitlyClosed.Disposed,
            "The Disconnect button must revoke its pairing even while the physical link remains available.");

        using var switchingLink = new CancellationTokenSource();
        var slowlyClosed = new PairingSmokeTransport { HoldDisposal = true };
        var switchingSession = await slowlyClosed.AuthenticateAsync("123456", default);
        await LoadCatalogueAsync(slowlyClosed, switchingSession, default);
        RememberConnection(slowlyClosed, null, switchingLink.Token, "123456", switchingSession);
        RefreshEnabled();
        bluetooth.Checked = true; // Actual CheckedChanged -> ChangeTransport -> RunAsync.
        await slowlyClosed.DisposalStarted.Task;
        var switchingOperation = operation;
        RequireSmoke(switchingOperation is not null && !usb.Enabled && !bluetooth.Enabled && !scan.Enabled && !connect.Enabled && !code.Enabled,
            "Transport mode changes must block scan, code entry and another connection until the old transport is disposed.");
        var switchingAuthCalls = slowlyClosed.AuthenticateCalls;
        scan.PerformClick(); connect.PerformClick();
        await RunAsync(ScanAsync, showErrors: false);
        RequireSmoke(ReferenceEquals(operation, switchingOperation) && slowlyClosed.AuthenticateCalls == switchingAuthCalls,
            "A second operation must not race the pending transport disposal.");
        slowlyClosed.AllowDisposal.TrySetResult();
        await WaitForSmokeStateAsync(() => operation is null && code.Enabled);
        RequireSmoke(slowlyClosed.Disposed && transport is null && pairingCode is null && bluetooth.Checked && !usbSetup.Visible,
            "Completing the mode change must leave the new Bluetooth mode ready for a fresh pairing.");
        usb.Checked = true;
        await WaitForSmokeStateAsync(() => operation is null && usb.Enabled && scan.Enabled);
        RequireSmoke(usbSetup.Visible, "USB setup must return after switching back to USB.");

        return new(true, true, true, true, true, true, true, true, peer.AuthenticateCalls, peer.CatalogueCalls);
    }

    private Task StartSuspendedSmokeOperation() => RunAsync(async token =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }, showErrors: false);

    private void RequirePaired(PairingSmokeTransport expected)
    {
        RequireSmoke(ReferenceEquals(transport, expected) && pairingCode?.IsActive == true && !expected.Disposed,
            "The live transport and its pairing must remain available.");
        RequireSmoke(!code.Enabled && code.Text.Length == 0 && !connect.Enabled && disconnect.Enabled && scan.Enabled,
            "Connected controls must not request another code.");
    }

    private static async Task WaitForSmokeStateAsync(Func<bool> condition)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Pairing UI did not settle.");
            await Task.Delay(10);
        }
    }

    private static void RequireSmoke(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PairingSmokeTransport : IArchiveTransport, IMediaTransport
    {
        private string albumId = new('a', 32);
        public string DisplayName => "Fixture phone";
        public int PreferredMediaChunkSize => 16384;
        public string SessionId { get; private set; } = "fixture-session-one";
        public int AuthenticateCalls { get; private set; }
        public int CatalogueCalls { get; private set; }
        public bool Disposed { get; private set; }
        public bool RejectCode { get; set; }
        public bool SessionUnavailable { get; set; }
        public bool HoldDisposal { get; set; }
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void NewAlbumSession() { SessionId = "fixture-session-two"; albumId = new('b', 32); }
        public Task<TransferSession> AuthenticateAsync(string pin, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (RejectCode) throw new PairingCodeRejectedException("Fixture authorization expired.");
            if (SessionUnavailable) throw new IOException("Fixture album transfer session is closed.");
            if (pin != "123456") throw new InvalidOperationException("The controller did not reuse the pairing code.");
            AuthenticateCalls++;
            return Task.FromResult(new TransferSession(2, "Fixture phone", SessionId, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<MediaCatalogue> CatalogueAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            CatalogueCalls++;
            return Task.FromResult(new MediaCatalogue(2, [new(albumId, "Sample Album", 24, 27)]));
        }

        public Task<ArchiveCatalogue> ListAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<byte[]> ReadRangeAsync(ArchiveInfo archive, long offset, int length, CancellationToken token) => throw new NotSupportedException();
        public Task<MediaFilePage> FilesAsync(string albumId, int cursor, CancellationToken token) => throw new NotSupportedException();
        public Task<MediaChunk> ChunkAsync(MediaFile file, long offset, int length, CancellationToken token) => throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            DisposalStarted.TrySetResult();
            if (HoldDisposal) await AllowDisposal.Task;
            Disposed = true;
        }
    }
}

internal sealed record PairingLifecycleSmokeResult(bool RefreshWithoutCode, bool NewAlbumWithoutCode, bool TemporarySessionCloseKeepsPairing,
    bool StopKeepsPairing, bool PhysicalDisconnectRevokesPairing, bool AuthorizationRejectionRevokesPairing,
    bool ExplicitDisconnectRevokesPairing, bool ModeChangeWaitsForDisposal, int AuthenticationCalls, int CatalogueCalls);
