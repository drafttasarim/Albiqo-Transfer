using Albiqo.Transfer.Core;
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Albiqo.Transfer.Windows;

public sealed record NearbyPhone(ulong Address, string Name)
{
    public override string ToString() => Name;
}

public sealed class BluetoothArchiveTransport : IArchiveTransport, IMediaTransport
{
    private readonly BluetoothLEDevice device;
    private readonly GattDeviceService service;
    private readonly GattSession session;
    private readonly GattCharacteristic rx;
    private readonly GattCharacteristic tx;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly ConcurrentDictionary<uint, PendingResponse> pending = new();
    private int nextId;
    private bool disposed;
    public string DisplayName => "Bluetooth · " + device.Name;
    public int PreferredReadSize => Math.Clamp((session.MaxPduSize - 3 - BleProtocol.ResponseHeaderSize) * 32, 512, 16384);

    public int PreferredMediaChunkSize => PreferredReadSize;

    private sealed class PendingResponse(uint id, long offset, int length)
    {
        public readonly object Lock = new();
        public readonly BleResponseAssembler Assembler = new(id, offset, length);
        public readonly TaskCompletionSource<(byte Kind, byte[] Bytes)> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private BluetoothArchiveTransport(BluetoothLEDevice device, GattDeviceService service, GattSession session, GattCharacteristic rx, GattCharacteristic tx)
    {
        this.device = device; this.service = service; this.session = session; this.rx = rx; this.tx = tx;
        tx.ValueChanged += OnValueChanged;
    }

    public static async Task<IReadOnlyList<NearbyPhone>> FindAsync(CancellationToken token)
    {
        var phones = new ConcurrentDictionary<ulong, NearbyPhone>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleProtocol.Service);
        watcher.Received += (_, e) =>
        {
            if (!e.Advertisement.ServiceUuids.Contains(BleProtocol.Service)) return;
            var name = string.IsNullOrWhiteSpace(e.Advertisement.LocalName) ? "Albiqo iPhone" : e.Advertisement.LocalName;
            phones[e.BluetoothAddress] = new(e.BluetoothAddress, name);
        };
        var stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Stopped += (_, e) => stopped.TrySetResult(e.Error == BluetoothError.Success ? null : e.Error.ToString());
        watcher.Start();
        try
        {
            var wait = Task.Delay(TimeSpan.FromSeconds(8), token);
            var completed = await Task.WhenAny(wait, stopped.Task);
            if (completed == stopped.Task && await stopped.Task is string error) throw new IOException("Bluetooth is unavailable: " + error);
            token.ThrowIfCancellationRequested();
            return phones.Values.OrderBy(x => x.Name).ToArray();
        }
        finally { watcher.Stop(); }
    }

    public static async Task<BluetoothArchiveTransport> ConnectAsync(NearbyPhone phone, CancellationToken token)
    {
        // Called from the UI thread: Windows may present its standard pairing/consent prompt.
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(phone.Address).AsTask(token)
            ?? throw new IOException("The phone is no longer reachable.");
        GattDeviceService? service = null;
        GattSession? session = null;
        BluetoothArchiveTransport? transport = null;
        try
        {
            if (!device.DeviceInformation.Pairing.IsPaired)
            {
                var paired = await device.DeviceInformation.Pairing.PairAsync(DevicePairingProtectionLevel.Encryption).AsTask(token);
                if (paired.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                    throw new IOException("Bluetooth pairing was not completed. Approve pairing on the phone and retry.");
            }
            var services = await device.GetGattServicesForUuidAsync(BleProtocol.Service, BluetoothCacheMode.Uncached).AsTask(token);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException("Open the PC Transfer screen in Albiqo and enable Bluetooth transfer.");
            service = services.Services[0];
            foreach (var unused in services.Services.Skip(1)) unused.Dispose();
            var rxResult = await service.GetCharacteristicsForUuidAsync(BleProtocol.Rx, BluetoothCacheMode.Uncached).AsTask(token);
            var txResult = await service.GetCharacteristicsForUuidAsync(BleProtocol.Tx, BluetoothCacheMode.Uncached).AsTask(token);
            if (rxResult.Status != GattCommunicationStatus.Success || txResult.Status != GattCommunicationStatus.Success ||
                rxResult.Characteristics.Count != 1 || txResult.Characteristics.Count != 1)
                throw new IOException("The phone does not expose the Albiqo transfer service.");
            var rx = rxResult.Characteristics[0];
            var tx = txResult.Characteristics[0];
            rx.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
            tx.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token)
                ?? throw new IOException("Cannot open the Bluetooth session.");
            session.MaintainConnection = true;
            transport = new(device, service, session, rx, tx);
            var status = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token);
            if (status != GattCommunicationStatus.Success) throw new IOException("Cannot subscribe to encrypted Bluetooth transfer notifications.");
            return transport;
        }
        catch
        {
            if (transport is not null) await transport.DisposeAsync();
            else { session?.Dispose(); service?.Dispose(); device.Dispose(); }
            throw;
        }
    }

    public async Task<TransferSession> AuthenticateAsync(string pin, CancellationToken token)
    {
        if (pin.Length != 6 || pin.Any(c => c is < '0' or > '9')) throw new ArgumentException("Enter the six-digit code shown in Albiqo.");
        var bytes = await RequestAsync(id => new { id, op = "auth", pin }, 0, 32768, false, token);
        var result = TransferJson.Decode<TransferSession>(bytes);
        if (result.ProtocolVersion is not (1 or 2)) throw new InvalidDataException("Update Albiqo Transfer to use this phone session.");
        return result;
    }

    public async Task<ArchiveCatalogue> ListAsync(CancellationToken token)
    {
        var bytes = await RequestAsync(id => new { id, op = "list" }, 0, 32768, false, token);
        var catalogue = TransferJson.Decode<ArchiveCatalogue>(bytes);
        catalogue.Validate();
        return catalogue;
    }

    public Task<byte[]> ReadRangeAsync(ArchiveInfo archive, long offset, int length, CancellationToken token)
    {
        if (offset < 0 || length is < 1 or > 65536 || offset > archive.Size - length) throw new ArgumentOutOfRangeException(nameof(offset));
        return RequestAsync(id => new { id, op = "get", archiveId = archive.Id, offset, length }, offset, length, true, token);
    }

    public async Task<MediaCatalogue> CatalogueAsync(CancellationToken token)
    {
        var result = TransferJson.Decode<MediaCatalogue>(await RequestAsync(id => new { id, op = "catalog" }, 0, 32768, false, token));
        result.Validate(); return result;
    }

    public async Task<MediaFilePage> FilesAsync(string albumId, int cursor, CancellationToken token)
    {
        if (cursor < 0 || albumId.Length != 32 || albumId.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid album page.");
        var result = TransferJson.Decode<MediaFilePage>(await RequestAsync(id => new { id, op = "files", albumId, cursor }, 0, 32768, false, token));
        result.Validate(albumId, cursor); return result;
    }

    public async Task<MediaChunk> ChunkAsync(MediaFile file, long offset, int length, CancellationToken token)
    {
        if (offset < 0 || length is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(offset));
        var result = TransferJson.Decode<MediaChunk>(await RequestAsync(id => new { id, op = "chunk", fileId = file.Id, offset, length }, 0, 32768, false, token, 600));
        result.Validate(offset, length); return result;
    }

    private async Task<byte[]> RequestAsync(Func<uint, object> command, long offset, int length, bool expectFile, CancellationToken token, int minimumTimeoutSeconds = 30)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await commandGate.WaitAsync(token);
        uint id = unchecked((uint)Interlocked.Increment(ref nextId));
        var response = new PendingResponse(id, offset, length);
        try
        {
            pending[id] = response;
            foreach (var packet in BleProtocol.CommandFragments(id, command(id), Math.Max(23, (int)session.MaxPduSize)))
            {
                using var writer = new DataWriter();
                writer.WriteBytes(packet);
                var status = await rx.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(token);
                if (status != GattCommunicationStatus.Success) throw new IOException("Bluetooth write failed. Keep Albiqo open and nearby.");
            }
            // The smallest ATT MTU leaves only6 data bytes per notification; allow real slow-radio sessions.
            var payload = Math.Max(6, session.MaxPduSize - 3 - BleProtocol.ResponseHeaderSize);
            var seconds = Math.Clamp(length / (double)payload * 0.15, minimumTimeoutSeconds, 600);
            var result = await response.Completion.Task.WaitAsync(TimeSpan.FromSeconds(seconds), token);
            if (result.Kind == 3)
            {
                var error = TransferJson.Decode<RemoteError>(result.Bytes);
                throw new IOException(error.Code == "unauthorized" ? "The session code is incorrect or expired." : "Albiqo: " + error.Message);
            }
            if (result.Kind != (expectFile ? 2 : 1)) throw new InvalidDataException("The phone returned the wrong response type.");
            return result.Bytes;
        }
        finally { pending.TryRemove(id, out _); commandGate.Release(); }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var packet = new byte[args.CharacteristicValue.Length];
            using (var reader = DataReader.FromBuffer(args.CharacteristicValue)) reader.ReadBytes(packet);
            if (packet.Length < BleProtocol.ResponseHeaderSize) return;
            var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1));
            if (!pending.TryGetValue(id, out var response)) return;
            lock (response.Lock)
            {
                try
                {
                    response.Assembler.Accept(packet);
                    if (response.Assembler.IsComplete) response.Completion.TrySetResult((response.Assembler.Kind, response.Assembler.Bytes));
                }
                catch (Exception error) { response.Completion.TrySetException(error); }
            }
        }
        catch (Exception error)
        {
            foreach (var response in pending.Values) response.Completion.TrySetException(error);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        tx.ValueChanged -= OnValueChanged;
        foreach (var response in pending.Values) response.Completion.TrySetCanceled();
        session.MaintainConnection = false;
        session.Dispose(); service.Dispose(); device.Dispose();
        // Avoid waiting on a disconnected radio during close; disposal removes the subscription.
        return ValueTask.CompletedTask;
    }
}
