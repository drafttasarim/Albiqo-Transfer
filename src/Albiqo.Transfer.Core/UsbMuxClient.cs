using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Albiqo.Transfer.Core;

public sealed class UsbMuxClient
{
    public const int PhonePort = 52783;
    private readonly int servicePort;
    public UsbMuxClient(int servicePort = 27015)
    {
        if (servicePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(servicePort));
        this.servicePort = servicePort;
    }
    private static int nextTag;

    public async Task<IReadOnlyList<UsbDevice>> ListAsync(CancellationToken cancellationToken)
    {
        using var socket = await OpenSocketAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var response = await RequestAsync(stream, new Dictionary<string, object> { ["MessageType"] = "ListDevices" }, cancellationToken).ConfigureAwait(false);
        if (!response.TryGetValue("DeviceList", out var value) || value is not List<object> list)
            throw new IOException("Apple USB service did not return a device list.");
        var devices = new List<UsbDevice>();
        foreach (var item in list.OfType<Dictionary<string, object>>())
        {
            if (!item.TryGetValue("Properties", out var raw) || raw is not Dictionary<string, object> properties) continue;
            if (!properties.TryGetValue("ConnectionType", out var connection) || connection as string != "USB") continue;
            var id = item.TryGetValue("DeviceID", out var rawId) ? Convert.ToUInt32(rawId) : 0;
            if (id == 0) continue;
            var serial = properties.TryGetValue("SerialNumber", out var rawSerial) ? rawSerial as string ?? "" : "";
            var name = properties.TryGetValue("DeviceName", out var rawName) ? rawName as string ?? "iPhone" : "iPhone";
            devices.Add(new(id, serial, name));
        }
        return devices;
    }

    public async ValueTask<Stream> ConnectAsync(uint deviceId, CancellationToken cancellationToken)
    {
        var socket = await OpenSocketAsync(cancellationToken).ConfigureAwait(false);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var response = await RequestAsync(stream, new Dictionary<string, object>
            {
                ["MessageType"] = "Connect", ["DeviceID"] = deviceId,
                ["PortNumber"] = BinaryPrimitives.ReverseEndianness((ushort)PhonePort)
            }, cancellationToken).ConfigureAwait(false);
            if (!response.TryGetValue("Number", out var code) || Convert.ToInt64(code) != 0)
                throw new IOException("The iPhone transfer session is unavailable. Unlock the phone, trust this computer and open PC Transfer in Albiqo.");
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
            throw new IOException("Apple Mobile Device USB service was not found. Install Apple's USB driver/service, use a data cable, then retry. Apple Devices need not be open.", ex);
        }
    }

    private static async Task<Dictionary<string, object>> RequestAsync(Stream stream, Dictionary<string, object> fields, CancellationToken cancellationToken)
    {
        fields["ClientVersionString"] = "AlbiqoTransfer-1.0";
        fields["ProgName"] = "AlbiqoTransfer";
        fields["kLibUSBMuxVersion"] = 3;
        var xml = EncodePlist(fields);
        var tag = unchecked((uint)Interlocked.Increment(ref nextTag));
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)xml.Length + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), tag);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(xml, cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var responseTag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (length < 16 || length > 4 * 1024 * 1024 || version != 1 || type != 8 || responseTag != tag)
            throw new InvalidDataException("Invalid Apple USB service response.");
        var body = new byte[length - 16];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return DecodePlist(body);
    }

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
