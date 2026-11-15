using Albiqo.Transfer.Core;
using System.Buffers.Binary;
using System.Text;

internal static class UsbAttachmentChecks
{
    private static readonly UsbDevice Phone = new(71, "fixture-71", "Test iPhone");
    public static IEnumerable<(string Name, Func<Task> Body)> All => new (string, Func<Task>)[]
    {
        ("USB authorization watches the target attachment and ignores other phones", async () =>
        {
            var detach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new ScriptedMuxService(async (stream, _, token) =>
            {
                await BeginAsync(stream, token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, ScriptedMuxService.AttachedXml(72), token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, Detached(72), token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, ScriptedMuxService.AttachedXml(71), token);
                await detach.Task.WaitAsync(token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, Detached(71), token);
                await Task.Delay(Timeout.Infinite, token);
            });
            await using var watch = await new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone, default);
            Check(watch.IsConnected && !watch.Disconnected.IsCancellationRequested, "Wrong phone ended authorization.");
            detach.SetResult(); await watch.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!watch.IsConnected && watch.Disconnected.IsCancellationRequested && watch.Failure is null, "Detach did not end the attachment.");
        }),
        ("USB rapid detach and reattach cannot revive the old authorization", async () =>
        {
            var detach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new ScriptedMuxService(async (stream, _, token) =>
            {
                await BeginAsync(stream, token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, ScriptedMuxService.AttachedXml(71), token);
                await detach.Task.WaitAsync(token);
                var disconnected = Packet(1, 8, Encoding.UTF8.GetBytes(Detached(71)));
                var reattached = Packet(1, 8, Encoding.UTF8.GetBytes(ScriptedMuxService.AttachedXml(71)));
                await stream.WriteAsync(disconnected.Concat(reattached).ToArray(), token);
                await Task.Delay(Timeout.Infinite, token);
            });
            await using var watch = await new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone, default);
            var notifications = 0; using var registration = watch.Disconnected.Register(() => Interlocked.Increment(ref notifications));
            detach.SetResult(); await watch.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!watch.IsConnected && notifications == 1, "A new cable generation reused the old authorization.");
        }),
        ("USB attachment startup verifies both serial and device identifier", async () =>
        {
            foreach (var message in new[]
            {
                ScriptedMuxService.AttachedXml(71).Replace("fixture-71", "different-phone"),
                ScriptedMuxService.AttachedXml(73).Replace("fixture-73", "fixture-71")
            })
            {
                await using var server = new ScriptedMuxService(async (stream, _, token) =>
                {
                    await BeginAsync(stream, token);
                    await ScriptedMuxService.SendXmlAsync(stream, 0, message, token);
                    await Task.Delay(Timeout.Infinite, token);
                });
                try
                {
                    await new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone, default);
                    throw new Exception("Mismatched identity was accepted.");
                }
                catch (IOException error) when (error is not UsbMuxException) { }
            }
        }),
        ("USB device replacement invalidates an already authorized attachment", async () =>
        {
            var replace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new ScriptedMuxService(async (stream, _, token) =>
            {
                await BeginAsync(stream, token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, ScriptedMuxService.AttachedXml(71), token);
                await replace.Task.WaitAsync(token);
                await ScriptedMuxService.SendXmlAsync(stream, 0,
                    ScriptedMuxService.AttachedXml(71).Replace("fixture-71", "different-phone"), token);
                await Task.Delay(Timeout.Infinite, token);
            });
            await using var watch = await new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone, default);
            replace.SetResult(); await watch.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!watch.IsConnected && watch.Failure is IOException, "Reused device ID retained authorization.");
        }),
        ("USB service EOF and malformed events fail closed", async () =>
        {
            foreach (var malformed in new[] { false, true })
            {
                var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var server = new ScriptedMuxService(async (stream, _, token) =>
                {
                    await BeginAsync(stream, token);
                    await ScriptedMuxService.SendXmlAsync(stream, 0, ScriptedMuxService.AttachedXml(71), token);
                    await fail.Task.WaitAsync(token);
                    if (malformed)
                    {
                        var invalid = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(invalid, 15);
                        await stream.WriteAsync(invalid, token);
                        await Task.Delay(Timeout.Infinite, token);
                    }
                });
                await using var watch = await new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone, default);
                fail.SetResult(); await watch.Completion.WaitAsync(TimeSpan.FromSeconds(2));
                Check(!watch.IsConnected && watch.Disconnected.IsCancellationRequested && watch.Failure is not null,
                    "Uncertain USB monitor retained authorization.");
            }
        }),
        ("USB watcher negotiates native v0 Listen and receives detach events", async () =>
        {
            var detach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new ScriptedMuxService(async (stream, connection, token) =>
            {
                var request = await ScriptedMuxService.ReadAsync(stream, token);
                if (connection == 1)
                {
                    ScriptedMuxService.ExpectPlist(request, "Listen");
                    await ScriptedMuxService.SendResultAsync(stream, request.Tag, 6, true, token); return;
                }
                Check(request.Version == 0 && request.Type == 3 && request.Body.Length == 0, "Expected v0 Listen.");
                await ScriptedMuxService.SendResultAsync(stream, request.Tag, 0, true, token);
                var record = new byte[268]; BinaryPrimitives.WriteUInt32LittleEndian(record, 71);
                Encoding.ASCII.GetBytes("fixture-71").CopyTo(record, 6);
                await ScriptedMuxService.SendAsync(stream, 0, 4, 0, record, token);
                await detach.Task.WaitAsync(token);
                var id = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(id, 71);
                await ScriptedMuxService.SendAsync(stream, 0, 5, 0, id, token);
                await Task.Delay(Timeout.Infinite, token);
            });
            await using var watch = await new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone, default);
            Check(watch.IsConnected && server.ConnectionCount == 2, "Legacy watcher did not become ready.");
            detach.SetResult(); await watch.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!watch.IsConnected && watch.Failure is null, "Native detach did not invalidate authorization.");
        }),
        ("USB transfer-operation cancellation does not own the physical attachment", async () =>
        {
            await using var server = new ScriptedMuxService(async (stream, _, token) =>
            {
                await BeginAsync(stream, token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, ScriptedMuxService.AttachedXml(71), token);
                await Task.Delay(Timeout.Infinite, token);
            });
            using var operation = new CancellationTokenSource();
            var watch = await new UsbMuxClient(server.Port, TimeSpan.FromMilliseconds(500)).WatchAttachmentAsync(Phone, operation.Token);
            operation.Cancel();
            // Staying idle beyond packet-response timeout must not look like cable removal.
            await Task.Delay(650);
            Check(watch.IsConnected, "Cancelling a transfer or idle USB closed pairing.");
            await watch.DisposeAsync(); await watch.DisposeAsync();
            Check(watch.Disconnected.IsCancellationRequested && watch.Failure is null, "Explicit disconnect did not clear authorization cleanly.");
        }),
        ("USB watcher startup cannot authorize an absent or cancelled attachment", async () =>
        {
            await using var server = new ScriptedMuxService(async (stream, _, token) =>
            {
                await BeginAsync(stream, token);
                await ScriptedMuxService.SendXmlAsync(stream, 0, ScriptedMuxService.AttachedXml(72), token);
                await Task.Delay(Timeout.Infinite, token);
            });
            try
            {
                await new UsbMuxClient(server.Port, TimeSpan.FromMilliseconds(100)).WatchAttachmentAsync(Phone, default);
                throw new Exception("Absent target was accepted.");
            }
            catch (UsbMuxException error) when (error.Failure == UsbMuxFailure.ResponseTimeout) { }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone, cancel.Token));
            await ExpectAsync<IOException>(() => new UsbMuxClient(server.Port).WatchAttachmentAsync(Phone with { Serial = "" }, default));
        })
    };

    private static async Task BeginAsync(System.Net.Sockets.NetworkStream stream, CancellationToken token)
    {
        var request = await ScriptedMuxService.ReadAsync(stream, token);
        ScriptedMuxService.ExpectPlist(request, "Listen");
        await ScriptedMuxService.SendResultAsync(stream, request.Tag, 0, false, token);
    }
    private static string Detached(uint id)
        => $"<plist><dict><key>MessageType</key><string>Detached</string><key>DeviceID</key><integer>{id}</integer></dict></plist>";
    private static byte[] Packet(uint version, uint type, byte[] body)
    {
        var packet = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), type);
        body.CopyTo(packet, 16); return packet;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
