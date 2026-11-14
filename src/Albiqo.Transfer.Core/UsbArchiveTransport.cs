using System.Net;
using System.Net.Http.Headers;

namespace Albiqo.Transfer.Core;

public sealed class UsbArchiveTransport : IArchiveTransport, IMediaTransport
{
    private readonly HttpClient client;
    private string? pin;
    public string DisplayName { get; }
    public int PreferredReadSize => 4 * 1024 * 1024;
    public int PreferredMediaChunkSize => 262144;

    public UsbArchiveTransport(UsbDevice device, UsbMuxClient? usbMux = null)
    {
        usbMux ??= new UsbMuxClient();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = (_, token) => usbMux.ConnectAsync(device.DeviceId, token),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 1
        };
        client = new HttpClient(handler) { BaseAddress = new Uri("http://albiqo.local/"), Timeout = Timeout.InfiniteTimeSpan };
        DisplayName = "USB · " + device.Name;
    }

    public async Task<TransferSession> AuthenticateAsync(string pin, CancellationToken cancellationToken)
    {
        if (pin.Length != 6 || pin.Any(c => c is < '0' or > '9')) throw new ArgumentException("Enter the six-digit code shown in Albiqo.");
        this.pin = pin;
        var response = await JsonAsync<TransferSession>("v1/session", cancellationToken).ConfigureAwait(false);
        if (response.ProtocolVersion is not (1 or 2)) throw new InvalidDataException("Update Albiqo Transfer to use this phone session.");
        return response;
    }

    public async Task<ArchiveCatalogue> ListAsync(CancellationToken cancellationToken)
    {
        var catalogue = await JsonAsync<ArchiveCatalogue>("v1/archives", cancellationToken).ConfigureAwait(false);
        catalogue.Validate();
        return catalogue;
    }

    public async Task<byte[]> ReadRangeAsync(ArchiveInfo archive, long offset, int length, CancellationToken cancellationToken)
    {
        if (offset < 0 || length is < 1 or > 4 * 1024 * 1024 || offset > archive.Size - length) throw new ArgumentOutOfRangeException(nameof(offset));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = MakeRequest("v1/archives/" + Uri.EscapeDataString(archive.Id));
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            await CheckStatusAsync(response, timeout.Token).ConfigureAwait(false);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != offset || range.To != offset + length - 1 || range.Length != archive.Size)
                throw new InvalidDataException("The phone returned an unexpected file range.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            var extra = new byte[1];
            if (await stream.ReadAsync(extra, timeout.Token).ConfigureAwait(false) != 0) throw new InvalidDataException("File range exceeds requested size.");
            return bytes;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("The phone did not finish the file range in time. Keep Albiqo open and retry.", error); }
    }

    private HttpRequestMessage MakeRequest(string path)
    {
        if (pin is null) throw new InvalidOperationException("Authenticate before transferring.");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pin);
        return request;
    }

    public async Task<MediaCatalogue> CatalogueAsync(CancellationToken token)
    {
        var result = await JsonAsync<MediaCatalogue>("v2/catalogue", token, 524288).ConfigureAwait(false);
        result.Validate(); return result;
    }

    public async Task<MediaFilePage> FilesAsync(string albumId, int cursor, CancellationToken token)
    {
        MediaValidation.Id(albumId);
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor));
        var result = await JsonAsync<MediaFilePage>($"v2/albums/{albumId}/files/{cursor}", token, 524288).ConfigureAwait(false);
        result.Validate(albumId, cursor); return result;
    }

    public async Task<MediaChunk> ChunkAsync(MediaFile file, long offset, int length, CancellationToken token)
    {
        MediaValidation.Id(file.Id);
        if (offset < 0 || length is < 1 or > 262144) throw new ArgumentOutOfRangeException(nameof(offset));
        var result = await JsonAsync<MediaChunk>($"v2/files/{file.Id}/chunk/{offset}/{length}", token, 524288, 600).ConfigureAwait(false);
        result.Validate(offset, length); return result;
    }

    private async Task<T> JsonAsync<T>(string path, CancellationToken cancellationToken, int maximumBytes = 32768, int timeoutSeconds = 30)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = MakeRequest(path);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            await CheckStatusAsync(response, timeout.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var bytes = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > maximumBytes) throw new InvalidDataException("The phone response is too large.");
                output.Write(bytes, 0, count);
            }
            return TransferJson.Decode<T>(output.ToArray());
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("The phone did not finish the media request in time. Keep Albiqo open and retry.", error); }
    }

    private static Task CheckStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
            throw new IOException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The session code is incorrect or expired.",
                HttpStatusCode.TooManyRequests => "Too many attempts. Start a new transfer session on your phone.",
                HttpStatusCode.NotFound => "This prepared album is no longer available.",
                _ => $"The phone could not complete the request ({(int)response.StatusCode})."
            });
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { pin = null; client.Dispose(); return ValueTask.CompletedTask; }
}
