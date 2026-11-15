using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Albiqo.Transfer.Core;

/// <summary>A single observed physical attachment, never a remembered trusted device.</summary>
public sealed class UsbAttachmentWatch : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken disconnected;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int attached;
    private int disposed;
    private Exception? failure;

    internal UsbAttachmentWatch(Stream stream, Func<Action, CancellationToken, Task> monitor)
    {
        this.stream = stream;
        disconnected = stop.Token;
        Completion = RunAsync(monitor);
    }

    public CancellationToken Disconnected => disconnected;
    public bool IsConnected => Volatile.Read(ref attached) != 0 && !disconnected.IsCancellationRequested;
    public Task Completion { get; }
    public Exception? Failure => Volatile.Read(ref failure);
    internal Task Ready => ready.Task;

    private async Task RunAsync(Func<Action, CancellationToken, Task> monitor)
    {
        try
        {
            await monitor(() =>
            {
                Volatile.Write(ref attached, 1);
                ready.TrySetResult();
            }, disconnected).ConfigureAwait(false);
            ready.TrySetException(new IOException("The selected USB attachment ended before it could be verified."));
        }
        catch (OperationCanceledException) when (disconnected.IsCancellationRequested)
        {
            ready.TrySetCanceled(disconnected);
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref disposed) == 0) Volatile.Write(ref failure, error);
            ready.TrySetException(error);
        }
        finally
        {
            // Cancellation invalidates callers synchronously, before any reconnect can be reused.
            stop.Cancel();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { await Completion.ConfigureAwait(false); return; }
        stop.Cancel();
        await stream.DisposeAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
        stop.Dispose();
    }
}

public sealed partial class UsbMuxClient
{
    /// <summary>
    /// Establish Listen before caching a successful pairing code. The startup token does not
    /// own the returned watch: stopping a transfer must not end the physical attachment.
    /// </summary>
    public async Task<UsbAttachmentWatch> WatchAttachmentAsync(UsbDevice device, CancellationToken startToken)
    {
        if (device.DeviceId == 0 || string.IsNullOrWhiteSpace(device.Serial))
            throw new IOException("The selected phone's USB identity could not be verified. Find phones again before pairing.");
        var stream = await OpenAttachmentListenAsync(1, startToken).ConfigureAwait(false);
        var watch = new UsbAttachmentWatch(stream,
            (attached, token) => MonitorAttachmentAsync(stream, device, attached, token));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(startToken);
            timeout.CancelAfter(responseTimeout);
            try { await watch.Ready.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (!startToken.IsCancellationRequested)
            { throw ResponseTimedOut(error); }
            startToken.ThrowIfCancellationRequested();
            if (!watch.IsConnected) throw watch.Failure ?? new IOException("The selected USB attachment has ended.");
            return watch;
        }
        catch { await watch.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<NetworkStream> OpenAttachmentListenAsync(uint version, CancellationToken token)
    {
        var socket = await OpenSocketAsync(token).ConfigureAwait(false);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var response = version == 1
                ? await RequestPlistAsync(stream, new() { ["MessageType"] = "Listen" }, token).ConfigureAwait(false)
                : await RequestBinaryAsync(stream, 3, [], token).ConfigureAwait(false);
            var result = ResultNumber(response);
            if (result == 6 && version == 1)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return await OpenAttachmentListenAsync(0, token).ConfigureAwait(false);
            }
            if (result != 0) throw Unsupported(result);
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task MonitorAttachmentAsync(Stream stream, UsbDevice target, Action attached, CancellationToken token)
    {
        var sawTarget = false;
        while (true)
        {
            // Listen may be quiet for hours. Bound partial packets, not a healthy idle connection.
            var packet = await ReadPacketAsync(stream, token, Timeout.InfiniteTimeSpan).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The Apple USB attachment monitor stopped.");
            UsbDevice? added = null;
            uint? removed = null;
            if (packet.Version == 1 && packet.Type == 8)
            {
                var message = DecodePlist(packet.Body);
                var kind = message.TryGetValue("MessageType", out var raw) ? raw as string : null;
                if (kind == "Attached")
                {
                    added = DeviceFromPlist(message);
                    // A formerly USB ID reappearing as a network device is not this attachment.
                    if (added is null && EventDeviceId(message) == target.DeviceId)
                        throw new IOException("The selected phone is no longer on the verified USB attachment.");
                }
                else if (kind == "Detached") removed = UInt32Value(message, "DeviceID");
                else if (kind != "Paired") throw new InvalidDataException("Invalid Apple USB attachment event.");
            }
            else if (packet.Version == 0 && packet.Type == 4 && packet.Body.Length == 268)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(packet.Body);
                if (id == 0) throw new InvalidDataException("Invalid Apple USB device identifier.");
                var end = Array.IndexOf(packet.Body, (byte)0, 6, 256);
                var serial = Encoding.UTF8.GetString(packet.Body, 6, end < 0 ? 256 : end - 6);
                added = new(id, serial, "iPhone");
            }
            else if (packet.Version == 0 && packet.Type is 5 or 6 && packet.Body.Length == 4)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(packet.Body);
                if (id == 0) throw new InvalidDataException("Invalid Apple USB device identifier.");
                if (packet.Type == 5) removed = id;
            }
            else throw new InvalidDataException("Invalid Apple USB attachment packet.");

            if (removed == target.DeviceId) return; // Even an immediate reattach starts a new scope.
            if (added is null) continue;
            var sameSerial = string.Equals(added.Serial, target.Serial, StringComparison.Ordinal);
            if (added.DeviceId != target.DeviceId)
            {
                if (sameSerial) throw new IOException("The phone's USB attachment identifier changed.");
                continue;
            }
            if (!sameSerial || sawTarget)
                throw new IOException("The selected USB attachment was replaced. Pair again.");
            sawTarget = true;
            attached();
        }
    }

    private static uint EventDeviceId(Dictionary<string, object> message)
        => message.TryGetValue("Properties", out var properties) && properties is Dictionary<string, object> values && values.ContainsKey("DeviceID")
            ? UInt32Value(values, "DeviceID") : UInt32Value(message, "DeviceID");
}
