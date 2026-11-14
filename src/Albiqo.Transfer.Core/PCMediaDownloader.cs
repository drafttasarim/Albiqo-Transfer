using System.Security.Cryptography;
using System.Text.Json;

namespace Albiqo.Transfer.Core;

/// <summary>Protocol2: bounded original-resource streaming with PC-only checkpoints.</summary>
public sealed class PCMediaDownloader
{
    private sealed record AlbumMarker(int Version, string AlbumId);
    private sealed record Checkpoint(string FileId, string AlbumId, string Version, string OriginalName, string PartialName,
        string? PublishedName = null, long? FinalSize = null, string? FinalSha256 = null);
    private sealed record Receipt(string FileId, string AlbumId, string Version, string OriginalName, string SavedName, long Size, string Sha256);

    public async Task<MediaAlbumResult> DownloadAlbumAsync(IMediaTransport transport, MediaAlbum album, string destination,
        IProgress<MediaTransferProgress>? progress, CancellationToken token)
    {
        album.Validate();
        destination = MediaFileSafety.DirectoryWithoutLinks(destination);
        var folder = await FindAlbumFolderAsync(destination, album, token).ConfigureAwait(false);
        var state = MediaFileSafety.DirectoryWithoutLinks(Path.Combine(folder, ".albiqo-transfer"));
        var lockPath = MediaFileSafety.Child(state, "transfer.lock");
        await using var albumLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursor = 0; var saved = 0; var skipped = 0; long transferred = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var page = await transport.FilesAsync(album.Id, cursor, token).ConfigureAwait(false);
            page.Validate(album.Id, cursor);
            foreach (var file in page.Files)
            {
                if (!seen.Add(file.Id) || seen.Count > album.FileCount) throw new InvalidDataException("The phone's media listing changed. Reconnect before continuing.");
                var completed = saved + skipped;
                progress?.Report(new(completed, album.FileCount, transferred, 0, null, file.FileName, "checking"));
                var result = await DownloadFileAsync(transport, album, file, folder, state,
                    (bytes, total, stage) => progress?.Report(new(completed, album.FileCount, transferred + bytes.NewBytes, bytes.FileBytes, total, file.FileName, stage)), token).ConfigureAwait(false);
                transferred += result.NewBytes;
                if (result.Skipped) skipped++; else saved++;
                progress?.Report(new(saved + skipped, album.FileCount, transferred, result.Size, result.Size, file.FileName, result.Skipped ? "skipped" : "file_complete"));
            }
            if (page.NextCursor is null) break;
            cursor = page.NextCursor.Value;
            if (cursor > 4_000_000) throw new InvalidDataException("Invalid media page cursor.");
        }
        if (seen.Count != album.FileCount) throw new InvalidDataException("The phone's media listing is incomplete. Saved files remain safe; reconnect to continue.");
        progress?.Report(new(saved + skipped, album.FileCount, transferred, 0, null, "", "complete"));
        return new(folder, saved, skipped, transferred);
    }

    private static async Task<string> FindAlbumFolderAsync(string destination, MediaAlbum album, CancellationToken token)
    {
        var name = MediaFileSafety.FolderName(album.Name);
        foreach (var existing in Directory.EnumerateDirectories(destination, name + "*"))
        {
            token.ThrowIfCancellationRequested();
            var leaf = Path.GetFileName(existing);
            if (leaf != name && !(leaf.StartsWith(name + " (", StringComparison.Ordinal) && leaf.EndsWith(')'))) continue;
            try
            {
                MediaFileSafety.RejectLink(existing);
                var metadata = Path.Combine(existing, ".albiqo-transfer"); MediaFileSafety.RejectLink(metadata);
                var marker = await ReadStateAsync<AlbumMarker>(MediaFileSafety.Child(metadata, "album.json"), token).ConfigureAwait(false);
                if (marker is { Version: 2 } && marker.AlbumId == album.Id) return existing;
            }
            catch (IOException) { /* Existing unrelated or linked directories are never adopted. */ }
            catch (JsonException) { }
        }
        var folder = Path.Combine(destination, name);
        for (var suffix = 2; Directory.Exists(folder) || File.Exists(folder); suffix++) folder = Path.Combine(destination, $"{name} ({suffix})");
        MediaFileSafety.DirectoryWithoutLinks(folder);
        var state = MediaFileSafety.DirectoryWithoutLinks(Path.Combine(folder, ".albiqo-transfer"));
        if (OperatingSystem.IsWindows()) File.SetAttributes(state, File.GetAttributes(state) | FileAttributes.Hidden);
        await WriteStateAsync(MediaFileSafety.Child(state, "album.json"), new AlbumMarker(2, album.Id), token).ConfigureAwait(false);
        return folder;
    }

    private sealed record FileResult(bool Skipped, long Size, long NewBytes);
    private readonly record struct FileProgress(long FileBytes, long NewBytes);

    private static async Task<FileResult> DownloadFileAsync(IMediaTransport transport, MediaAlbum album, MediaFile file, string folder, string state,
        Action<FileProgress, long?, string> progress, CancellationToken token)
    {
        var key = file.Id + "-" + file.Version;
        var receiptPath = MediaFileSafety.Child(state, key + ".complete.json");
        var checkpointPath = MediaFileSafety.Child(state, key + ".pending.json");
        var receipt = await ReadStateAsync<Receipt>(receiptPath, token).ConfigureAwait(false);
        if (Matches(receipt, file) && await VerifyCompletedAsync(folder, receipt!, token).ConfigureAwait(false)) return new(true, receipt!.Size, 0);
        // A user-modified completed file is preserved; only its generated receipt is invalidated.
        if (receipt is not null) File.Delete(receiptPath);
        var checkpoint = await ReadStateAsync<Checkpoint>(checkpointPath, token).ConfigureAwait(false);
        if (!Matches(checkpoint, file)) checkpoint = null;
        if (checkpoint is not null && checkpoint.PublishedName is not null && checkpoint.FinalSize is long finalSize && checkpoint.FinalSha256 is string finalHash)
        {
            var recovered = new Receipt(file.Id, file.AlbumId, file.Version, file.FileName, checkpoint.PublishedName, finalSize, finalHash);
            if (await VerifyCompletedAsync(folder, recovered, token).ConfigureAwait(false))
            {
                await WriteStateAsync(receiptPath, recovered, token).ConfigureAwait(false); File.Delete(checkpointPath);
                return new(true, finalSize, 0);
            }
        }
        string partial;
        if (checkpoint is not null)
        {
            if (!checkpoint.PartialName.StartsWith(key + ".", StringComparison.Ordinal) || !checkpoint.PartialName.EndsWith(".partial", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe media checkpoint.");
            partial = MediaFileSafety.Child(state, checkpoint.PartialName);
            if (!File.Exists(partial)) checkpoint = null;
        }
        if (checkpoint is null)
        {
            var partialName = key + "." + Guid.NewGuid().ToString("N") + ".partial";
            checkpoint = new(file.Id, file.AlbumId, file.Version, file.FileName, partialName);
            partial = MediaFileSafety.Child(state, partialName);
            await WriteStateAsync(checkpointPath, checkpoint, token).ConfigureAwait(false);
            await using var empty = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        }
        partial = MediaFileSafety.Child(state, checkpoint.PartialName);
        long offset; long newBytes = 0; string digest; long size;
        await using (var output = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            // Re-hash retained local bytes once. No full original is staged or downloaded on the phone.
            var retained = new byte[65536];
            while (true)
            {
                var count = await output.ReadAsync(retained, token).ConfigureAwait(false);
                if (count == 0) break;
                hash.AppendData(retained, 0, count);
            }
            offset = output.Length; output.Position = offset;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var length = Math.Clamp(transport.PreferredMediaChunkSize, 1, 262144);
                var chunk = await ReadChunkWithRetryAsync(transport, file, offset, length, token).ConfigureAwait(false);
                try { chunk.Validate(offset, length); }
                catch (InvalidDataException) { File.Delete(checkpointPath); throw; }
                if (chunk.Data.Length > 0)
                {
                    await output.WriteAsync(chunk.Data, token).ConfigureAwait(false);
                    hash.AppendData(chunk.Data); offset += chunk.Data.Length; newBytes += chunk.Data.Length;
                }
                progress(new(offset, newBytes), chunk.TotalSize, "download");
                if (!chunk.Eof) continue;
                size = chunk.TotalSize!.Value;
                await output.FlushAsync(token).ConfigureAwait(false);
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (output.Length != size || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(chunk.Sha256!)))
                {
                    File.Delete(checkpointPath);
                    throw new InvalidDataException("Media verification failed. Retry will download a fresh copy; the suspect partial remains unpublished.");
                }
                progress(new(offset, newBytes), size, "verify");
                break;
            }
        }
        token.ThrowIfCancellationRequested();
        var final = MediaFileSafety.UniqueFile(folder, MediaFileSafety.FileName(file.FileName));
        checkpoint = checkpoint with { PublishedName = Path.GetFileName(final), FinalSize = size, FinalSha256 = digest };
        // This checkpoint closes the crash window between atomic publish and receipt creation.
        await WriteStateAsync(checkpointPath, checkpoint, token).ConfigureAwait(false);
        MediaFileSafety.RejectLink(partial); MediaFileSafety.RejectLink(folder);
        File.Move(partial, final, overwrite: false);
        if (file.CreatedAt.Year >= 1601)
        {
            try { File.SetLastWriteTimeUtc(final, file.CreatedAt.UtcDateTime); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        await WriteStateAsync(receiptPath, new Receipt(file.Id, album.Id, file.Version, file.FileName, Path.GetFileName(final), size, digest), token).ConfigureAwait(false);
        File.Delete(checkpointPath);
        return new(false, size, newBytes);
    }

    private static bool Matches(Receipt? receipt, MediaFile file) => receipt is not null && receipt.FileId == file.Id && receipt.AlbumId == file.AlbumId && receipt.Version == file.Version && receipt.OriginalName == file.FileName;
    private static bool Matches(Checkpoint? checkpoint, MediaFile file) => checkpoint is not null && checkpoint.FileId == file.Id && checkpoint.AlbumId == file.AlbumId && checkpoint.Version == file.Version && checkpoint.OriginalName == file.FileName;

    private static async Task<bool> VerifyCompletedAsync(string folder, Receipt receipt, CancellationToken token)
    {
        if (receipt.Size < 0 || receipt.Sha256 is null || receipt.Sha256.Length != 64 || receipt.Sha256.Any(c => !char.IsAsciiHexDigit(c))) return false;
        var path = MediaFileSafety.Child(folder, receipt.SavedName);
        if (!File.Exists(path) || new FileInfo(path).Length != receipt.Size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        return string.Equals(hash, receipt.Sha256, StringComparison.Ordinal);
    }

    private static async Task<MediaChunk> ReadChunkWithRetryAsync(IMediaTransport transport, MediaFile file, long offset, int length, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await transport.ChunkAsync(file, offset, length, token).ConfigureAwait(false); }
            catch (Exception error) when (attempt < 2 && !token.IsCancellationRequested && error is IOException or TimeoutException or HttpRequestException)
            { await Task.Delay(200 * (attempt + 1), token).ConfigureAwait(false); }
        }
    }

    private static async Task<T?> ReadStateAsync<T>(string path, CancellationToken token) where T : class
    {
        MediaFileSafety.RejectLink(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Media transfer state is too large.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<T>(stream, TransferJson.Options, token).ConfigureAwait(false);
    }

    private static async Task WriteStateAsync<T>(string path, T value, CancellationToken token)
    {
        MediaFileSafety.RejectLink(path); MediaFileSafety.RejectLink(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            { await JsonSerializer.SerializeAsync(stream, value, TransferJson.Options, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
