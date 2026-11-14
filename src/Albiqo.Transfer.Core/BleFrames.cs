using System.Buffers.Binary;
using System.Text.Json;

namespace Albiqo.Transfer.Core;

public static class BleProtocol
{
    public static readonly Guid Service = Guid.Parse("A4B10000-7E7A-4AD8-9ED8-4AB1C0A0B100");
    public static readonly Guid Rx = Guid.Parse("A4B10001-7E7A-4AD8-9ED8-4AB1C0A0B100");
    public static readonly Guid Tx = Guid.Parse("A4B10002-7E7A-4AD8-9ED8-4AB1C0A0B100");
    public const int ResponseHeaderSize = 14;

    public static IEnumerable<byte[]> CommandFragments(uint id, object command, int maxPduSize)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(command, TransferJson.Options);
        if (json.Length > 512) throw new InvalidDataException("Bluetooth command exceeds512 bytes.");
        var capacity = maxPduSize - 3 - 5;
        if (capacity < 1) throw new InvalidDataException("Invalid Bluetooth MTU.");
        for (var at = 0; at < json.Length; at += capacity)
        {
            var count = Math.Min(capacity, json.Length - at);
            var fragment = new byte[count + 5];
            BinaryPrimitives.WriteUInt32LittleEndian(fragment, id);
            fragment[4] = (byte)((at == 0 ? 1 : 0) | (at + count == json.Length ? 2 : 0));
            json.AsSpan(at, count).CopyTo(fragment.AsSpan(5));
            yield return fragment;
        }
    }
}

public sealed class BleResponseAssembler(uint requestId, long expectedOffset, int maximumLength)
{
    private readonly MemoryStream buffer = new();
    private byte? kind;
    public bool IsComplete { get; private set; }
    public byte Kind => kind ?? throw new InvalidDataException("No response received.");
    public byte[] Bytes => IsComplete ? buffer.ToArray() : throw new InvalidDataException("Incomplete response.");

    public bool Accept(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < BleProtocol.ResponseHeaderSize) throw new InvalidDataException("Truncated Bluetooth header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]) != requestId) return false;
        if (IsComplete) return false;
        var type = packet[0];
        if (type is < 1 or > 3 || packet[13] > 1) throw new InvalidDataException("Invalid Bluetooth response.");
        if (kind is not null && kind != type) throw new InvalidDataException("Bluetooth response kind changed.");
        kind = type;
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(packet[5..]);
        var start = type == 2 ? expectedOffset : 0;
        if (offset != checked((ulong)(start + buffer.Length))) throw new InvalidDataException("Missing or reordered Bluetooth bytes.");
        var payload = packet[BleProtocol.ResponseHeaderSize..];
        var limit = type == 2 ? maximumLength : 32768;
        if (payload.Length > limit - buffer.Length) throw new InvalidDataException("Bluetooth response exceeds limit.");
        buffer.Write(payload);
        IsComplete = packet[13] == 1;
        if (IsComplete && buffer.Length == 0 && type != 2) throw new InvalidDataException("Empty Bluetooth JSON response.");
        return true;
    }
}
