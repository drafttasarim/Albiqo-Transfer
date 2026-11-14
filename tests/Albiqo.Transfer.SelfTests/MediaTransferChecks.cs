using Albiqo.Transfer.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class MediaTransferChecks
{
    private const string AlbumId = "11111111111111111111111111111111";
    private const string Version1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public static IEnumerable<(string Name, Func<Task> Body)> All => new (string, Func<Task>)[]
    {
        ("Direct media preserves originals, duplicate file names, extensions and completed receipts", async () =>
        {
            await InFolder(async root =>
            {
                var files = new[] { File(1, "IMG_0001.HEIC"), File(2, "IMG_0001.MOV"), File(3, "IMG_0001.HEIC") };
                var source = new Source(files); var album = Album(files.Length);
                var result = await new PCMediaDownloader().DownloadAlbumAsync(source, album, root, null, default);
                Assert(result.SavedFiles == 3 && result.SkippedFiles == 0);
                Assert(System.IO.File.Exists(Path.Combine(result.Folder, "IMG_0001.HEIC")) && System.IO.File.Exists(Path.Combine(result.Folder, "IMG_0001 (2).HEIC")));
                Assert(System.IO.File.Exists(Path.Combine(result.Folder, "IMG_0001.MOV")) && !Directory.EnumerateFiles(result.Folder, "*.zip").Any());
                var retry = new Source(files);
                var again = await new PCMediaDownloader().DownloadAlbumAsync(retry, album, root, null, default);
                Assert(again.Folder == result.Folder && again.SavedFiles == 0 && again.SkippedFiles == 3 && retry.Offsets.Count == 0);
            });
        }),
        ("Direct media cancellation resumes retained bytes and hashes the full prefix", async () =>
        {
            await InFolder(async root =>
            {
                var file = File(1, "Large.mov"); var album = Album(1);
                using var cancellation = new CancellationTokenSource();
                var first = new Source([file]) { BeforeRead = offset => { if (offset > 0) cancellation.Cancel(); } };
                await Expect<OperationCanceledException>(() => new PCMediaDownloader().DownloadAlbumAsync(first, album, root, null, cancellation.Token));
                var partial = Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Single();
                Assert(new FileInfo(partial).Length == 1024);
                var retry = new Source([file]);
                var result = await new PCMediaDownloader().DownloadAlbumAsync(retry, album, root, null, default);
                Assert(retry.Offsets.First() == 1024 && result.SavedFiles == 1);
                Assert((await System.IO.File.ReadAllBytesAsync(Path.Combine(result.Folder, "Large.mov"))).SequenceEqual(retry.Data(file)));
            });
        }),
        ("Direct media accepts a verified empty EOF after a full final data block", async () =>
        {
            await InFolder(async root =>
            {
                var file = File(1, "exact.jpg"); var source = new Source([file]) { EmptyFinalChunk = true };
                var result = await new PCMediaDownloader().DownloadAlbumAsync(source, Album(1), root, null, default);
                Assert(result.SavedFiles == 1 && source.Offsets.Last() == source.Data(file).Length);
            });
        }),
        ("Direct media rejects truncated EOF and never publishes an original", async () =>
        {
            await InFolder(async root =>
            {
                var file = File(1, "truncated.heic"); var source = new Source([file]) { TruncatedEof = true };
                await Expect<InvalidDataException>(() => new PCMediaDownloader().DownloadAlbumAsync(source, Album(1), root, null, default));
                Assert(!Directory.EnumerateFiles(root, "*.heic", SearchOption.AllDirectories).Any());
            });
        }),
        ("Direct media rejects hash mismatch and retry downloads a fresh file", async () =>
        {
            await InFolder(async root =>
            {
                var file = File(1, "photo.jpg"); var bad = new Source([file]) { BadDigest = true };
                await Expect<InvalidDataException>(() => new PCMediaDownloader().DownloadAlbumAsync(bad, Album(1), root, null, default));
                Assert(!Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Any());
                var good = new Source([file]); var result = await new PCMediaDownloader().DownloadAlbumAsync(good, Album(1), root, null, default);
                Assert(good.Offsets.First() == 0 && result.SavedFiles == 1);
            });
        }),
        ("Direct media source version changes do not reuse an older partial", async () =>
        {
            await InFolder(async root =>
            {
                var file = File(1, "changed.jpg"); using var cancellation = new CancellationTokenSource();
                var first = new Source([file]) { BeforeRead = offset => { if (offset > 0) cancellation.Cancel(); } };
                await Expect<OperationCanceledException>(() => new PCMediaDownloader().DownloadAlbumAsync(first, Album(1), root, null, cancellation.Token));
                var newer = file with { Version = new string('b', 64) }; var second = new Source([newer]);
                var result = await new PCMediaDownloader().DownloadAlbumAsync(second, Album(1), root, null, default);
                Assert(second.Offsets.First() == 0 && result.SavedFiles == 1);
                Assert(Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any());
            });
        }),
        ("Direct media user-modified completed originals are preserved with a new copy", async () =>
        {
            await InFolder(async root =>
            {
                var file = File(1, "edited.jpg"); var result = await new PCMediaDownloader().DownloadAlbumAsync(new Source([file]), Album(1), root, null, default);
                var original = Path.Combine(result.Folder, "edited.jpg"); var changed = await System.IO.File.ReadAllBytesAsync(original); changed[0] ^= 0xff;
                await System.IO.File.WriteAllBytesAsync(original, changed);
                var retry = await new PCMediaDownloader().DownloadAlbumAsync(new Source([file]), Album(1), root, null, default);
                Assert(retry.SavedFiles == 1 && retry.SkippedFiles == 0 && System.IO.File.Exists(Path.Combine(result.Folder, "edited (2).jpg")));
                Assert((await System.IO.File.ReadAllBytesAsync(original)).SequenceEqual(changed));
            });
        }),
        ("Direct media pages remain bounded and are all downloaded", async () =>
        {
            await InFolder(async root =>
            {
                var files = Enumerable.Range(1, 35).Select(i => File(i, $"photo{i}.jpg")).ToArray(); var source = new Source(files);
                var result = await new PCMediaDownloader().DownloadAlbumAsync(source, Album(35), root, null, default);
                Assert(result.SavedFiles == 35 && source.Cursors.SequenceEqual(new[] { 0, 32 }));
            });
        }),
        ("Direct media Windows names cannot escape the album and preserve long extensions", async () =>
        {
            Assert(MediaFileSafety.FileName("CON.jpg") == "_CON.jpg");
            Assert(MediaFileSafety.FileName("COM¹.mov") == "_COM¹.mov");
            Assert(MediaFileSafety.FileName(new string('x', 300) + ".HEIC").EndsWith(".HEIC", StringComparison.Ordinal));
            Assert(!MediaFileSafety.FolderName("../../Album").Contains('/'));
            await InFolder(async root =>
            {
                var file = File(1, "A/B:C?*.mov");
                var result = await new PCMediaDownloader().DownloadAlbumAsync(new Source([file]), Album(1) with { Name = "CON" }, root, null, default);
                Assert(Path.GetFileName(result.Folder) == "_CON" && System.IO.File.Exists(Path.Combine(result.Folder, "A_B_C__.mov")));
            });
        }),
        ("Direct media rejects symlinked destination directories", async () =>
        {
            await InFolder(async root =>
            {
                var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
                var linked = Path.Combine(root, "linked"); Directory.CreateSymbolicLink(linked, outside);
                await Expect<IOException>(() => new PCMediaDownloader().DownloadAlbumAsync(new Source([File(1, "test.jpg")]), Album(1), linked, null, default));
                Assert(!Directory.EnumerateFileSystemEntries(outside).Any());
            });
        }),
        ("Direct media rejects a completed-file reparse link without touching its target", async () =>
        {
            await InFolder(async root =>
            {
                var file = File(1, "safe.jpg"); var result = await new PCMediaDownloader().DownloadAlbumAsync(new Source([file]), Album(1), root, null, default);
                var target = Path.Combine(root, "keep.txt"); await System.IO.File.WriteAllTextAsync(target, "keep");
                var original = Path.Combine(result.Folder, "safe.jpg"); System.IO.File.Delete(original); System.IO.File.CreateSymbolicLink(original, target);
                await Expect<IOException>(() => new PCMediaDownloader().DownloadAlbumAsync(new Source([file]), Album(1), root, null, default));
                Assert(await System.IO.File.ReadAllTextAsync(target) == "keep");
            });
        }),
        ("Direct media rejects empty nonfinal chunks and missing EOF proofs", () =>
        {
            Throws<InvalidDataException>(() => new MediaChunk(0, [], false, null, null).Validate(0, 1024));
            Throws<InvalidDataException>(() => new MediaChunk(0, [1], true, 1, null).Validate(0, 1024));
            Throws<InvalidDataException>(() => new MediaChunk(0, [1], true, 2, new string('a', 64)).Validate(0, 1024));
            return Task.CompletedTask;
        })
    };

    private static MediaFile File(int id, string name) => new(id.ToString("x32"), AlbumId, name, Version1, DateTimeOffset.UtcNow);
    private static MediaAlbum Album(int count) => new(AlbumId, "İstanbul Tatili", count, count);
    private static void Assert(bool value) { if (!value) throw new Exception("Direct media assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task InFolder(Func<string, Task> action)
    {
        // macOS /var and /tmp are OS symlinks; use the real temporary root for link-rejection checks.
        var temp = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temp, "albiqo-media-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await action(root); } finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Source(MediaFile[] files) : IMediaTransport
    {
        public string DisplayName => "Test direct";
        public int PreferredMediaChunkSize => 1024;
        public List<long> Offsets { get; } = [];
        public List<int> Cursors { get; } = [];
        public Action<long>? BeforeRead { get; init; }
        public bool EmptyFinalChunk { get; init; }
        public bool TruncatedEof { get; init; }
        public bool BadDigest { get; init; }
        public byte[] Data(MediaFile file) => Enumerable.Range(0, 4096).Select(i => (byte)((i + Convert.ToInt32(file.Id[^4..], 16)) % 251)).ToArray();
        public Task<MediaCatalogue> CatalogueAsync(CancellationToken token) => Task.FromResult(new MediaCatalogue(2, [Album(files.Length)]));
        public Task<MediaFilePage> FilesAsync(string albumId, int cursor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Cursors.Add(cursor);
            return Task.FromResult(new MediaFilePage(2, albumId, files.Skip(cursor).Take(32).ToArray(), cursor + 32 < files.Length ? cursor + 32 : null));
        }
        public Task<MediaChunk> ChunkAsync(MediaFile file, long offset, int length, CancellationToken token)
        {
            BeforeRead?.Invoke(offset); token.ThrowIfCancellationRequested(); Offsets.Add(offset);
            var data = Data(file); var count = (int)Math.Min(length, data.Length - offset);
            var eof = TruncatedEof || (EmptyFinalChunk ? count == 0 : offset + count == data.Length);
            var digest = eof ? BadDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() : null;
            return Task.FromResult(new MediaChunk(offset, data.AsSpan((int)offset, count).ToArray(), eof, eof ? data.Length : null, digest));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
