using System.Security.Cryptography;
using System.Text.Json;

namespace Albiqo.Transfer.Core;

public sealed class ArchiveDownloader
{
    private sealed record ResumeInfo(string ArchiveId, long Size, string Sha256);

    public async Task<string> DownloadAsync(IArchiveTransport transport, ArchiveInfo archive, string destination,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        archive.Validate();
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var name = WindowsFileNames.SafeZipName(archive.FileName);
        var tag = archive.Sha256[..12];
        var partial = Path.Combine(destination, name + "." + tag + ".partial");
        var sidecar = partial + ".resume.json";
        var expected = new ResumeInfo(archive.Id, archive.Size, archive.Sha256);
        var resume = await CanResumeAsync(partial, sidecar, expected, cancellationToken).ConfigureAwait(false);
        if (!resume)
        {
            // A previous hash failure or an unrelated file can require a unique partial name.
            // Discover those sidecars too, so stopping that fresh attempt still resumes normally.
            foreach (var candidate in Directory.EnumerateFiles(destination, name + "." + tag + ".*.partial"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await CanResumeAsync(candidate, candidate + ".resume.json", expected, cancellationToken).ConfigureAwait(false))
                { partial = candidate; sidecar = candidate + ".resume.json"; resume = true; break; }
            }
        }
        if (!resume && (File.Exists(partial) || File.Exists(sidecar)))
        {
            partial = Path.Combine(destination, name + "." + tag + "." + Guid.NewGuid().ToString("N")[..8] + ".partial");
            sidecar = partial + ".resume.json";
        }
        if (File.Exists(partial) && (File.GetAttributes(partial) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The partial file is a link. Choose another destination.");
        if (!resume)
        {
            await using var metadata = new FileStream(sidecar, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await JsonSerializer.SerializeAsync(metadata, expected, TransferJson.Options, cancellationToken).ConfigureAwait(false);
        }

        await using (var output = new FileStream(partial, resume ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var offset = output.Length;
            output.Position = offset;
            progress?.Report(new(offset, archive.Size, "download"));
            while (offset < archive.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = (int)Math.Min(Math.Clamp(transport.PreferredReadSize, 1, 4 * 1024 * 1024), archive.Size - offset);
                var data = await ReadWithRetryAsync(transport, archive, offset, length, cancellationToken).ConfigureAwait(false);
                if (data.Length != length) throw new InvalidDataException("The phone returned an incomplete file block.");
                await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                offset += data.Length;
                progress?.Report(new(offset, archive.Size, "download"));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output.Length != archive.Size) throw new InvalidDataException("Downloaded size does not match the phone.");
            progress?.Report(new(archive.Size, archive.Size, "verify"));
            output.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(archive.Sha256)))
            {
                // Preserve the suspect bytes, but never resume them on the next attempt.
                File.Delete(sidecar);
                throw new InvalidDataException("Verification failed. The file was not published. Retry downloads a fresh copy; the previous partial file is preserved.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var final = UniquePath(destination, name);
        // No-overwrite move publishes only a complete, hash-verified archive.
        File.Move(partial, final, overwrite: false);
        File.Delete(sidecar);
        progress?.Report(new(archive.Size, archive.Size, "complete"));
        return final;
    }

    private static async Task<byte[]> ReadWithRetryAsync(IArchiveTransport transport, ArchiveInfo archive, long offset, int length, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await transport.ReadRangeAsync(archive, offset, length, token).ConfigureAwait(false); }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested && ex is IOException or TimeoutException or HttpRequestException)
            { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false); }
        }
    }

    private static async Task<bool> CanResumeAsync(string partial, string sidecar, ResumeInfo expected, CancellationToken token)
    {
        if (!File.Exists(partial) || !File.Exists(sidecar)) return false;
        if ((File.GetAttributes(partial) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) != 0) return false;
        if (new FileInfo(partial).Length > expected.Size || new FileInfo(sidecar).Length > 4096) return false;
        try
        {
            await using var stream = File.OpenRead(sidecar);
            return await JsonSerializer.DeserializeAsync<ResumeInfo>(stream, TransferJson.Options, token).ConfigureAwait(false) == expected;
        }
        catch (JsonException) { return false; }
    }

    private static string UniquePath(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        for (var number = 2; File.Exists(candidate) || Directory.Exists(candidate); number++)
            candidate = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + $" ({number}).zip");
        return candidate;
    }
}
