using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Albiqo.Transfer.Core;

public enum UsbMuxFailure { ServiceUnavailable, ResponseTimeout, UnsupportedProtocol }

public sealed class UsbMuxException(UsbMuxFailure failure, string message, Exception? inner = null) : IOException(message, inner)
{
    public UsbMuxFailure Failure { get; } = failure;
}

public sealed class UsbMuxClient
{
    public const int PhonePort = 52783;
    private readonly int servicePort;
    private readonly TimeSpan responseTimeout;
    private static int nextTag;
    private sealed record MuxPacket(uint Version, uint Type, uint Tag, byte[] Body);

    public UsbMuxClient(int servicePort = 27015, TimeSpan? responseTimeout = null)
    {
        if (servicePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(servicePort));
        this.servicePort = servicePort;
        this.responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(5);
        if (this.responseTimeout <= TimeSpan.Zero || this.responseTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
    }

    public async Task<IReadOnlyList<UsbDevice>> ListAsync(CancellationToken cancellationToken)
    {
        uint listenVersion = 1;
        using (var socket = await OpenSocketAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var response = await RequestPlistAsync(stream, new() { ["MessageType"] = "ListDevices" }, cancellationToken).ConfigureAwait(false);
            if (response.TryGetValue("DeviceList", out var value))
            {
                if (value is not List<object> list) throw new InvalidDataException("Invalid Apple USB device list.");
                var devices = new List<UsbDevice>();
                foreach (var item in list)
                {
                    if (item is not Dictionary<string, object> entry) throw new InvalidDataException("Invalid Apple USB device entry.");
                    var device = DeviceFromPlist(entry);
                    if (device is not null) devices.Add(device);
                }
                return devices;
            }
            var result = ResultNumber(response);
            // Older Apple services implement Listen rather than ListDevices. A v0
            // binary RESULT_BADVERSION is also a valid response to our v1 request.
            if (result == 6) listenVersion = 0;
            else if (result is not (0 or 1)) throw Unsupported(result);
        }
        return await ListenAsync(listenVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<UsbDevice>> ListenAsync(uint version, CancellationToken cancellationToken)
    {
        using var socket = await OpenSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        Dictionary<string, object> response;
        if (version == 1)
            response = await RequestPlistAsync(stream, new() { ["MessageType"] = "Listen" }, cancellationToken).ConfigureAwait(false);
        else
            response = await RequestBinaryAsync(stream, 3, [], cancellationToken).ConfigureAwait(false);
        var result = ResultNumber(response);
        if (result == 6 && version == 1) return await ListenAsync(0, cancellationToken).ConfigureAwait(false);
        if (result != 0) throw Unsupported(result);

        var devices = new Dictionary<uint, UsbDevice>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var first = true;
        while (started.Elapsed < TimeSpan.FromSeconds(2))
        {
            // Listen has no end marker: the initial Attached events are followed
            // by an idle socket. Do not leave enumeration waiting indefinitely.
            var packet = await ReadPacketAsync(stream, cancellationToken,
                first ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            if (packet is null) break;
            first = false;
            if (packet.Version == 1 && packet.Type == 8)
            {
                var message = DecodePlist(packet.Body);
                var kind = message.TryGetValue("MessageType", out var raw) ? raw as string : null;
                if (kind == "Attached")
                {
                    var device = DeviceFromPlist(message);
                    if (device is not null) devices[device.DeviceId] = device;
                }
                else if (kind == "Detached") devices.Remove(UInt32Value(message, "DeviceID"));
                else if (kind != "Paired") throw new InvalidDataException("Invalid Apple USB discovery event.");
            }
            else if (packet.Version == 0 && packet.Type == 4 && packet.Body.Length == 268)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(packet.Body);
                if (id == 0) throw new InvalidDataException("Invalid Apple USB device identifier.");
                var end = Array.IndexOf(packet.Body, (byte)0, 6, 256);
                var serial = Encoding.UTF8.GetString(packet.Body, 6, end < 0 ? 256 : end - 6);
                devices[id] = new(id, serial, "iPhone");
            }
            else if (packet.Version == 0 && packet.Type is 5 or 6 && packet.Body.Length == 4)
            {
                if (packet.Type == 5) devices.Remove(BinaryPrimitives.ReadUInt32LittleEndian(packet.Body));
            }
            else throw new InvalidDataException("Invalid Apple USB discovery packet.");
        }
        return devices.Values.ToArray();
    }

    private static UsbDevice? DeviceFromPlist(Dictionary<string, object> item)
    {
        if (!item.TryGetValue("Properties", out var raw) || raw is not Dictionary<string, object> properties)
            throw new InvalidDataException("Invalid Apple USB device properties.");
        if (!properties.TryGetValue("ConnectionType", out var connection) || connection is not string type)
            throw new InvalidDataException("Missing Apple USB connection type.");
        if (type == "Network") return null;
        if (type != "USB") throw new InvalidDataException("Unknown Apple USB connection type.");
        var id = properties.ContainsKey("DeviceID") ? UInt32Value(properties, "DeviceID") : UInt32Value(item, "DeviceID");
        if (id == 0) throw new InvalidDataException("Invalid Apple USB device identifier.");
        var serial = properties.TryGetValue("SerialNumber", out var rawSerial) ? rawSerial as string ?? "" : "";
        var name = properties.TryGetValue("DeviceName", out var rawName) ? rawName as string ?? "iPhone" : "iPhone";
        return new(id, serial, name);
    }

    public async ValueTask<Stream> ConnectAsync(uint deviceId, CancellationToken cancellationToken)
    {
        if (deviceId == 0) throw new ArgumentOutOfRangeException(nameof(deviceId));
        return await ConnectVersionAsync(deviceId, 1, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Stream> ConnectVersionAsync(uint deviceId, uint version, CancellationToken cancellationToken)
    {
        var socket = await OpenSocketAsync(cancellationToken).ConfigureAwait(false);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            Dictionary<string, object> response;
            if (version == 1)
                response = await RequestPlistAsync(stream, new()
                {
                    ["MessageType"] = "Connect", ["DeviceID"] = deviceId,
                    ["PortNumber"] = BinaryPrimitives.ReverseEndianness((ushort)PhonePort)
                }, cancellationToken).ConfigureAwait(false);
            else
            {
                var body = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(body, deviceId);
                BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), PhonePort);
                response = await RequestBinaryAsync(stream, 2, body, cancellationToken).ConfigureAwait(false);
            }
            var result = ResultNumber(response);
            if (result == 6 && version == 1)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return await ConnectVersionAsync(deviceId, 0, cancellationToken).ConfigureAwait(false);
            }
            if (result is 1 or 6) throw Unsupported(result);
            if (result != 0)
                throw new IOException($"The iPhone transfer session is unavailable (Apple USB result {result}). Unlock the phone, trust this computer and open PC Transfer in Albiqo.");
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<Socket> OpenSocketAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, servicePort), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new UsbMuxException(UsbMuxFailure.ServiceUnavailable,
                "Apple Mobile Device USB service was not found. Install Apple's USB driver/service, use a data cable, then retry. Apple Devices need not be open.", ex);
        }
    }

    private Task<Dictionary<string, object>> RequestPlistAsync(Stream stream, Dictionary<string, object> fields, CancellationToken cancellationToken)
    {
        fields["ClientVersionString"] = "AlbiqoTransfer-1.0";
        fields["ProgName"] = "AlbiqoTransfer";
        fields["kLibUSBMuxVersion"] = 3;
        return RequestAsync(stream, 1, 8, EncodePlist(fields), cancellationToken);
    }

    private Task<Dictionary<string, object>> RequestBinaryAsync(Stream stream, uint type, byte[] body, CancellationToken cancellationToken)
        => RequestAsync(stream, 0, type, body, cancellationToken);

    private async Task<Dictionary<string, object>> RequestAsync(Stream stream, uint version, uint type, byte[] body, CancellationToken cancellationToken)
    {
        var tag = unchecked((uint)Interlocked.Increment(ref nextTag));
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)body.Length + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), tag);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(responseTimeout);
        try
        {
            await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
            var packet = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Empty Apple USB reply.");
            if (packet.Tag != tag) throw new InvalidDataException("Apple USB response tag mismatch.");
            if (packet.Version == 1 && packet.Type == 8) return DecodePlist(packet.Body);
            if (packet.Version is 0 or 1 && packet.Type == 1 && packet.Body.Length == 4)
                return new() { ["MessageType"] = "Result", ["Number"] = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(packet.Body) };
            throw new InvalidDataException("Invalid Apple USB service response.");
        }
        catch (OperationCanceledException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw ResponseTimedOut(ex);
        }
    }

    private async Task<MuxPacket?> ReadPacketAsync(Stream stream, CancellationToken cancellationToken, TimeSpan? idleTimeout = null)
    {
        var header = new byte[16];
        var offset = 0;
        if (idleTimeout is { } idle)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(idle);
            try
            {
                if (await stream.ReadAsync(header.AsMemory(0, 1), wait.Token).ConfigureAwait(false) == 0)
                    throw new EndOfStreamException("Apple USB discovery connection closed unexpectedly.");
                offset = 1;
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(responseTimeout);
        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(offset), timeout.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (length < 16 || length > 4 * 1024 * 1024 || version > 1)
                throw new InvalidDataException("Invalid Apple USB packet header.");
            var body = new byte[length - 16];
            await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
            return new(version, type, tag, body);
        }
        catch (OperationCanceledException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw ResponseTimedOut(ex);
        }
    }

    private static uint UInt32Value(Dictionary<string, object> values, string key)
        => values.TryGetValue(key, out var value) && value is ulong number && number <= uint.MaxValue
            ? (uint)number : throw new InvalidDataException($"Invalid Apple USB {key}.");
    private static uint ResultNumber(Dictionary<string, object> response)
        => response.TryGetValue("MessageType", out var kind) && kind as string == "Result"
            ? UInt32Value(response, "Number") : throw new InvalidDataException("Missing Apple USB result.");
    private static UsbMuxException Unsupported(uint result) => new(UsbMuxFailure.UnsupportedProtocol,
        $"The installed Apple USB service does not support this request (result {result}). Update Apple Devices or Apple Mobile Device Support.");
    private static UsbMuxException ResponseTimedOut(Exception inner) => new(UsbMuxFailure.ResponseTimeout,
        "Apple USB service did not respond in time. Reconnect the unlocked iPhone and retry.", inner);

    public static byte[] EncodePlist(Dictionary<string, object> values)
    {
        var dict = new XElement("dict");
        foreach (var (key, value) in values)
        {
            dict.Add(new XElement("key", key));
            dict.Add(value is string text ? new XElement("string", text) : new XElement("integer", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("plist", new XAttribute("version", "1.0"), dict)).ToString());
    }

    public static Dictionary<string, object> DecodePlist(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        return ParseElement(document.Root?.Element("dict") ?? throw new InvalidDataException("Invalid USB plist.")) as Dictionary<string, object>
            ?? throw new InvalidDataException("Invalid USB plist dictionary.");
    }

    private static object ParseElement(XElement element) => element.Name.LocalName switch
    {
        "dict" => ParseDictionary(element),
        "array" => element.Elements().Select(ParseElement).ToList(),
        "integer" => ulong.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture),
        "string" => element.Value,
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(element.Value),
        _ => throw new InvalidDataException("Unsupported USB plist type.")
    };

    private static Dictionary<string, object> ParseDictionary(XElement element)
    {
        var children = element.Elements().ToArray();
        if (children.Length % 2 != 0) throw new InvalidDataException("Invalid USB dictionary length.");
        var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < children.Length; i += 2)
        {
            if (children[i].Name.LocalName != "key") throw new InvalidDataException("Invalid USB dictionary key.");
            dictionary.Add(children[i].Value, ParseElement(children[i + 1]));
        }
        return dictionary;
    }
}
