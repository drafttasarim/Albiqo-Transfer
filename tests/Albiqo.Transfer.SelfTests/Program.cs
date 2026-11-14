using Albiqo.Transfer.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

var tests = new (string Name, Func<Task> Body)[]
{
    ("Windows file names cannot escape destination or use reserved names", () =>
    {
        Equal("İstanbul Tatili.zip", WindowsFileNames.SafeZipName("İstanbul Tatili.zip"));
        Equal("A_B_C_D___.zip", WindowsFileNames.SafeZipName("A/B:C\\D?*|.zip"));
        Equal("_CON.zip", WindowsFileNames.SafeZipName("CON.zip"));
        Equal("_aux.photo.zip", WindowsFileNames.SafeZipName("aux.photo.zip"));
        Equal("Albiqo Album.zip", WindowsFileNames.SafeZipName("..."));
        Equal("Album.zip", WindowsFileNames.SafeZipName("Album. "));
        True(!WindowsFileNames.SafeZipName("../danger").Contains('/'));
        return Task.CompletedTask;
    }),
    ("BLE RX fragments support minimum ATT MTU23", () =>
    {
        var command = new { id = 42U, op = "get", archiveId = "abc", offset = 100L, length = 16384 };
        var packets = BleProtocol.CommandFragments(42, command, 23).ToArray();
        True(packets.Length > 1 && packets.All(x => x.Length <= 20));
        True(packets[0][4] == 1 && packets[^1][4] == 2);
        True(packets.All(x => BinaryPrimitives.ReadUInt32LittleEndian(x) == 42));
        var json = Encoding.UTF8.GetString(packets.SelectMany(x => x.Skip(5)).ToArray());
        True(json.Contains("\"offset\":100") && json.Contains("\"archiveId\":\"abc\""));
        return Task.CompletedTask;
    }),
    ("BLE frames verify request IDs, offsets and bounded lengths", () =>
    {
        var assembly = new BleResponseAssembler(9, 100, 5);
        True(!assembly.Accept(Packet(2, 8, 100, false, [9])));
        True(assembly.Accept(Packet(2, 9, 100, false, [1, 2])));
        True(assembly.Accept(Packet(2, 9, 102, true, [3, 4, 5])));
        True(assembly.IsComplete && assembly.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));
        Throws<InvalidDataException>(() => new BleResponseAssembler(1, 0, 2).Accept(Packet(2, 1, 1, true, [1])));
        Throws<InvalidDataException>(() => new BleResponseAssembler(1, 0, 2).Accept(Packet(2, 1, 0, true, [1, 2, 3])));
        Throws<InvalidDataException>(() => new BleResponseAssembler(1, 0, 2).Accept([1, 2]));
        return Task.CompletedTask;
    }),
    ("Catalogue rejects invalid hashes and duplicate IDs", () =>
    {
        var archive = Archive(new byte[] { 1 });
        archive.Validate();
        Throws<InvalidDataException>(() => (archive with { Sha256 = "bad" }).Validate());
        Throws<InvalidDataException>(() => new ArchiveCatalogue(1, [archive, archive]).Validate());
        return Task.CompletedTask;
    }),
    ("USB plist parses real-shaped device dictionary and cannot resolve external entities", () =>
    {
        var roundtrip = UsbMuxClient.DecodePlist(UsbMuxClient.EncodePlist(new() { ["MessageType"] = "Connect", ["DeviceID"] = 19, ["PortNumber"] = 1234 }));
        Equal("Connect", (string)roundtrip["MessageType"]);
        True(Convert.ToUInt32(roundtrip["DeviceID"]) == 19);
        var body = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist><dict><key>DeviceList</key><array><dict><key>DeviceID</key><integer>3</integer><key>Properties</key><dict><key>ConnectionType</key><string>USB</string></dict></dict></array></dict></plist>");
        True(UsbMuxClient.DecodePlist(body).ContainsKey("DeviceList"));
        return Task.CompletedTask;
    }),
    ("USB tunnel round trip covers service listing, network-order port, PIN auth and HTTP ranges", async () =>
    {
        var data = new byte[1000]; Random.Shared.NextBytes(data); var archive = Archive(data);
        await using var server = new FakeUsbService(archive, data);
        var mux = new UsbMuxClient(server.Port);
        var devices = await mux.ListAsync(default);
        True(devices.Count == 1 && devices[0].DeviceId == 7);
        await using var transport = new UsbArchiveTransport(devices[0], mux);
        var session = await transport.AuthenticateAsync("123456", default);
        True(session.ProtocolVersion == 1 && session.DeviceName == "Test iPhone");
        var catalogue = await transport.ListAsync(default);
        True(catalogue.Archives.Length == 1);
        var range = await transport.ReadRangeAsync(archive, 123, 100, default);
        True(range.SequenceEqual(data.AsSpan(123, 100).ToArray()));
        await using var unauthorized = new UsbArchiveTransport(devices[0], mux);
        await ThrowsAsync<IOException>(() => unauthorized.AuthenticateAsync("999999", default));
    }),
    ("Downloads publish verified ZIP without overwriting an existing file", async () =>
    {
        await WithFolder(async folder =>
        {
            var data = Enumerable.Range(0, 180_000).Select(i => (byte)(i % 251)).ToArray();
            var archive = Archive(data);
            await File.WriteAllTextAsync(Path.Combine(folder, "Album.zip"), "keep");
            await using var transport = new FakeTransport(data);
            var result = await new ArchiveDownloader().DownloadAsync(transport, archive, folder, null, default);
            Equal("Album (2).zip", Path.GetFileName(result));
            True((await File.ReadAllBytesAsync(result)).SequenceEqual(data));
            Equal("keep", await File.ReadAllTextAsync(Path.Combine(folder, "Album.zip")));
            True(!Directory.EnumerateFiles(folder, "*.partial").Any());
        });
    }),
    ("Cancellation keeps partial bytes and retry resumes instead of restarting", async () =>
    {
        await WithFolder(async folder =>
        {
            var data = new byte[5 * 1024 * 1024]; Random.Shared.NextBytes(data); var archive = Archive(data);
            using var cancellation = new CancellationTokenSource();
            await using (var first = new FakeTransport(data) { BeforeRead = offset => { if (offset > 0) cancellation.Cancel(); } })
            {
                await ThrowsAsync<OperationCanceledException>(() => new ArchiveDownloader().DownloadAsync(first, archive, folder, null, cancellation.Token));
            }
            var partial = Directory.EnumerateFiles(folder, "*.partial").Single();
            var kept = new FileInfo(partial).Length;
            True(kept == 4 * 1024 * 1024);
            await using var retry = new FakeTransport(data);
            var result = await new ArchiveDownloader().DownloadAsync(retry, archive, folder, null, default);
            True(retry.FirstOffset == kept);
            True((await File.ReadAllBytesAsync(result)).SequenceEqual(data));
        });
    }),
    ("Hash mismatch never publishes a final ZIP", async () =>
    {
        await WithFolder(async folder =>
        {
            var data = new byte[] { 1, 2, 3, 4 }; var archive = Archive(data) with { Sha256 = new string('0', 64) };
            await using var transport = new FakeTransport(data);
            await ThrowsAsync<InvalidDataException>(() => new ArchiveDownloader().DownloadAsync(transport, archive, folder, null, default));
            True(!Directory.EnumerateFiles(folder, "*.zip").Any());
            True(Directory.EnumerateFiles(folder, "*.partial").Any());
            True(!Directory.EnumerateFiles(folder, "*.resume.json").Any());
        });
    }),
    ("Retry after a corrupted complete partial downloads fresh bytes", async () =>
    {
        await WithFolder(async folder =>
        {
            var good = new byte[] { 10, 20, 30, 40 }; var archive = Archive(good);
            await using (var corrupt = new FakeTransport([10, 20, 30, 41]))
                await ThrowsAsync<InvalidDataException>(() => new ArchiveDownloader().DownloadAsync(corrupt, archive, folder, null, default));
            var suspect = Directory.EnumerateFiles(folder, "*.partial").Single();
            True(!File.Exists(suspect + ".resume.json"));
            await using var retry = new FakeTransport(good);
            var final = await new ArchiveDownloader().DownloadAsync(retry, archive, folder, null, default);
            True(retry.FirstOffset == 0 && File.Exists(suspect));
            True((await File.ReadAllBytesAsync(final)).SequenceEqual(good));
        });
    }),
    ("Unrelated stale partial is preserved rather than overwritten", async () =>
    {
        await WithFolder(async folder =>
        {
            var data = new byte[] { 1, 2, 3 }; var archive = Archive(data);
            var stale = Path.Combine(folder, "Album.zip." + archive.Sha256[..12] + ".partial");
            await File.WriteAllTextAsync(stale, "unrelated");
            await using var transport = new FakeTransport(data);
            await new ArchiveDownloader().DownloadAsync(transport, archive, folder, null, default);
            Equal("unrelated", await File.ReadAllTextAsync(stale));
        });
    }),
    ("A uniquely named replacement partial also resumes after cancellation", async () =>
    {
        await WithFolder(async folder =>
        {
            var data = new byte[5 * 1024 * 1024]; Random.Shared.NextBytes(data); var archive = Archive(data);
            var stale = Path.Combine(folder, "Album.zip." + archive.Sha256[..12] + ".partial");
            await File.WriteAllTextAsync(stale, "unrelated");
            using var cancellation = new CancellationTokenSource();
            await using (var first = new FakeTransport(data) { BeforeRead = offset => { if (offset > 0) cancellation.Cancel(); } })
                await ThrowsAsync<OperationCanceledException>(() => new ArchiveDownloader().DownloadAsync(first, archive, folder, null, cancellation.Token));
            True(Directory.EnumerateFiles(folder, "*.partial").Count() == 2);
            await using var retry = new FakeTransport(data);
            var result = await new ArchiveDownloader().DownloadAsync(retry, archive, folder, null, default);
            True(retry.FirstOffset == 4 * 1024 * 1024);
            Equal("unrelated", await File.ReadAllTextAsync(stale));
            True((await File.ReadAllBytesAsync(result)).SequenceEqual(data));
        });
    })
};
var failures = 0;
foreach (var (name, body) in tests)
{
    try { await body(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} transfer checks passed.");
return failures == 0 ? 0 : 1;

static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task WithFolder(Func<string, Task> action)
{
    var folder = Path.Combine(Path.GetTempPath(), "albiqo-transfer-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    try { await action(folder); } finally { Directory.Delete(folder, recursive: true); }
}
static ArchiveInfo Archive(byte[] data) => new("9b4722bc7db84b6eb98ad82e6faf1351", "Album", "Album.zip", data.Length, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), DateTimeOffset.UtcNow);
static byte[] Packet(byte kind, uint id, ulong offset, bool final, byte[] data)
{
    var packet = new byte[14 + data.Length]; packet[0] = kind; BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), id); BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(5), offset); packet[13] = final ? (byte)1 : (byte)0; data.CopyTo(packet, 14); return packet;
}
sealed class FakeTransport(byte[] data) : IArchiveTransport
{
    public string DisplayName => "USB fake";
    public int PreferredReadSize => 4 * 1024 * 1024;
    public long? FirstOffset { get; private set; }
    public Action<long>? BeforeRead { get; init; }
    public Task<TransferSession> AuthenticateAsync(string pin, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ArchiveCatalogue> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<byte[]> ReadRangeAsync(ArchiveInfo archive, long offset, int length, CancellationToken token)
    {
        BeforeRead?.Invoke(offset);
        token.ThrowIfCancellationRequested();
        FirstOffset ??= offset;
        var result = data.AsSpan((int)offset, length).ToArray();
        return Task.FromResult(result);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeUsbService : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly ArchiveInfo archive;
    private readonly byte[] data;
    private readonly List<Task> connections = [];
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FakeUsbService(ArchiveInfo archive, byte[] data)
    {
        this.archive = archive; this.data = data; listener.Start(); loop = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                lock (connections) connections.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream(); var header = new byte[16];
            await stream.ReadExactlyAsync(header, stop.Token);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var bytes = new byte[length - 16]; await stream.ReadExactlyAsync(bytes, stop.Token);
            var request = UsbMuxClient.DecodePlist(bytes);
            var message = (string)request["MessageType"];
            byte[] body;
            if (message == "ListDevices")
                body = Encoding.UTF8.GetBytes("<plist><dict><key>DeviceList</key><array><dict><key>DeviceID</key><integer>7</integer><key>Properties</key><dict><key>SerialNumber</key><string>test-serial</string><key>ConnectionType</key><string>USB</string><key>DeviceName</key><string>Test iPhone</string></dict></dict><dict><key>DeviceID</key><integer>8</integer><key>Properties</key><dict><key>ConnectionType</key><string>Network</string></dict></dict></array></dict></plist>");
            else
            {
                if (Convert.ToUInt32(request["DeviceID"]) != 7 || Convert.ToUInt16(request["PortNumber"]) != BinaryPrimitives.ReverseEndianness((ushort)52783)) throw new Exception("Incorrect tunnel request.");
                body = UsbMuxClient.EncodePlist(new() { ["MessageType"] = "Result", ["Number"] = 0 });
            }
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length + 16);
            await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(body, stop.Token);
            if (message == "ListDevices") return;
            using var requestBytes = new MemoryStream();
            var single = new byte[1];
            while (requestBytes.Length < 8192)
            {
                if (await stream.ReadAsync(single, stop.Token) == 0) return;
                requestBytes.Write(single);
                var content = requestBytes.GetBuffer(); var n = (int)requestBytes.Length;
                if (n >= 4 && content[n - 4] == 13 && content[n - 3] == 10 && content[n - 2] == 13 && content[n - 1] == 10) break;
            }
            var http = Encoding.ASCII.GetString(requestBytes.ToArray());
            var lines = http.Split("\r\n");
            if (!lines.Any(x => x.Equals("Authorization: Bearer 123456", StringComparison.OrdinalIgnoreCase)))
            { await ReplyAsync(stream, "401 Unauthorized", [], null); return; }
            var path = lines[0].Split(' ')[1];
            if (path == "/v1/session")
                await ReplyAsync(stream, "200 OK", JsonSerializer.SerializeToUtf8Bytes(new TransferSession(1, "Test iPhone", "s", DateTimeOffset.UtcNow.AddHours(1)), TransferJson.Options), null);
            else if (path == "/v1/archives")
                await ReplyAsync(stream, "200 OK", JsonSerializer.SerializeToUtf8Bytes(new ArchiveCatalogue(1, [archive]), TransferJson.Options), null);
            else
            {
                var range = lines.Single(x => x.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)).Split('=')[1].Split('-');
                var start = int.Parse(range[0]); var end = int.Parse(range[1]);
                await ReplyAsync(stream, "206 Partial Content", data.AsSpan(start, end - start + 1).ToArray(), $"Content-Range: bytes {start}-{end}/{data.Length}\r\n");
            }
        }
    }
    private async Task ReplyAsync(NetworkStream stream, string status, byte[] body, string? extra)
    {
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extra}\r\n");
        await stream.WriteAsync(head, stop.Token); await stream.WriteAsync(body, stop.Token);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); await loop;
        Task[] tasks; lock (connections) tasks = connections.ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        stop.Dispose();
    }
}
