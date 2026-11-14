using Albiqo.Transfer.Core;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class MediaWireChecks
{
    public static IEnumerable<(string Name, Func<Task> Body)> All => new (string, Func<Task>)[]
    {
        ("USB protocol2 authenticates, decodes omitted nullable fields and streams a maximum base64 chunk", async () =>
        {
            const string album = "11111111111111111111111111111111", file = "22222222222222222222222222222222";
            var bytes = Enumerable.Repeat((byte)255, 262144).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await using var server = new ScriptedMuxService(async (stream, _, token) =>
            {
                var request = await ScriptedMuxService.ReadAsync(stream, token);
                ScriptedMuxService.ExpectPlist(request, "Connect");
                await ScriptedMuxService.SendResultAsync(stream, request.Tag, 0, false, token);
                var http = await ReadHttpAsync(stream, token);
                if (!http.Contains("Authorization: Bearer 123456\r\n", StringComparison.OrdinalIgnoreCase))
                { await ReplyAsync(stream, "401 Unauthorized", "{}", token); return; }
                var path = http.Split(' ')[1];
                var body = path switch
                {
                    "/v1/session" => "{\"protocolVersion\":2,\"deviceName\":\"Test iPhone\",\"sessionId\":\"session\",\"expiresAt\":\"2026-12-31T12:00:00Z\"}",
                    "/v2/catalogue" => $"{{\"protocolVersion\":2,\"albums\":[{{\"id\":\"{album}\",\"name\":\"Album\",\"assetCount\":1,\"fileCount\":1}}]}}",
                    $"/v2/albums/{album}/files/0" => $"{{\"protocolVersion\":2,\"albumId\":\"{album}\",\"files\":[{{\"id\":\"{file}\",\"albumId\":\"{album}\",\"fileName\":\"IMG.HEIC\",\"version\":\"{new string('a',32)}\",\"createdAt\":\"2021-02-27T12:24:00Z\"}}]}}",
                    $"/v2/files/{file}/chunk/0/262144" => $"{{\"offset\":0,\"data\":\"{Convert.ToBase64String(bytes)}\",\"eof\":true,\"totalSize\":262144,\"sha256\":\"{hash}\"}}",
                    _ => throw new Exception("Unexpected direct route: " + path)
                };
                await ReplyAsync(stream, "200 OK", body, token);
            });
            await using var transport = new UsbArchiveTransport(new UsbDevice(7, "test", "Test iPhone"), new UsbMuxClient(server.Port));
            var session = await transport.AuthenticateAsync("123456", default);
            if (session.ProtocolVersion != 2) throw new Exception("Protocol2 not negotiated.");
            var catalogue = await transport.CatalogueAsync(default);
            var page = await transport.FilesAsync(catalogue.Albums.Single().Id, 0, default);
            if (page.NextCursor is not null) throw new Exception("Missing optional cursor must decode as null.");
            var result = await transport.ChunkAsync(page.Files.Single(), 0, transport.PreferredMediaChunkSize, default);
            if (!result.Eof || result.TotalSize != bytes.Length || result.Sha256 != hash || !result.Data.SequenceEqual(bytes))
                throw new Exception("Direct HTTP body changed.");
            await using var unauthorized = new UsbArchiveTransport(new UsbDevice(7, "test", "Test iPhone"), new UsbMuxClient(server.Port));
            try { await unauthorized.AuthenticateAsync("654321", default); throw new Exception("Wrong PIN accepted."); }
            catch (IOException) { }
        }),
        ("BLE protocol2 chunk JSON crosses minimum-MTU notifications without binary offset confusion", () =>
        {
            var data = Enumerable.Repeat((byte)255, 16384).ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            var json = JsonSerializer.SerializeToUtf8Bytes(new MediaChunk(16384, data, true, 32768, digest), TransferJson.Options);
            if (json.Length > 32768) throw new Exception("Maximum chunk exceeds BLE JSON bound.");
            var assembler = new BleResponseAssembler(51, 0, 32768);
            for (var at = 0; at < json.Length; at += 6)
            {
                var count = Math.Min(6, json.Length - at); var packet = new byte[14 + count];
                packet[0] = 1;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), 51);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(5), (ulong)at);
                packet[13] = (byte)(at + count == json.Length ? 1 : 0); json.AsSpan(at, count).CopyTo(packet.AsSpan(14));
                assembler.Accept(packet);
            }
            var decoded = TransferJson.Decode<MediaChunk>(assembler.Bytes); decoded.Validate(16384, 16384);
            if (assembler.Kind != 1 || !decoded.Data.SequenceEqual(data)) throw new Exception("BLE protocol2 JSON lost bytes.");
            return Task.CompletedTask;
        })
    };
    private static async Task<string> ReadHttpAsync(NetworkStream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var one = new byte[1];
        while (bytes.Length < 8192)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException();
            bytes.Write(one); var n = (int)bytes.Length; var buffer = bytes.GetBuffer();
            if (n >= 4 && buffer[n-4] == 13 && buffer[n-3] == 10 && buffer[n-2] == 13 && buffer[n-1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new Exception("HTTP headers too long.");
    }
    private static async Task ReplyAsync(NetworkStream stream, string status, string body, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, token); await stream.WriteAsync(bytes, token);
    }
}
