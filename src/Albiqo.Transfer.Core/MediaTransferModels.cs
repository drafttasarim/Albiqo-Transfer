using System.Text.RegularExpressions;

namespace Albiqo.Transfer.Core;

public sealed record MediaAlbum(string Id, string Name, int AssetCount, int FileCount)
{
    public void Validate()
    {
        MediaValidation.Id(Id);
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 500 || AssetCount < 0 || FileCount < 0 || AssetCount > 1_000_000 || FileCount > 4_000_000)
            throw new InvalidDataException("Invalid album metadata.");
    }
}
public sealed record MediaFile(string Id, string AlbumId, string FileName, string Version, DateTimeOffset CreatedAt)
{
    public void Validate(string albumId)
    {
        MediaValidation.Id(Id); MediaValidation.Id(AlbumId);
        if (AlbumId != albumId || string.IsNullOrWhiteSpace(FileName) || FileName.Length > 500 || !Regex.IsMatch(Version ?? "", "^(?:[a-f0-9]{32}|[a-f0-9]{64})$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid media file metadata.");
    }
}
public sealed record MediaCatalogue(int ProtocolVersion, MediaAlbum[] Albums)
{
    public void Validate()
    {
        if (ProtocolVersion != 2 || Albums is null || Albums.Length > 4096) throw new InvalidDataException("Unsupported media catalogue.");
        foreach (var album in Albums) album.Validate();
        if (Albums.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != Albums.Length) throw new InvalidDataException("Duplicate album identifiers.");
    }
}
public sealed record MediaFilePage(int ProtocolVersion, string AlbumId, MediaFile[] Files, int? NextCursor)
{
    public void Validate(string albumId, int cursor)
    {
        if (ProtocolVersion != 2 || AlbumId != albumId || Files is null || Files.Length > 32 || NextCursor is < 0 || NextCursor <= cursor || (NextCursor is not null && Files.Length == 0))
            throw new InvalidDataException("Invalid media file page.");
        foreach (var file in Files) file.Validate(albumId);
    }
}
public sealed record MediaChunk(long Offset, byte[] Data, bool Eof, long? TotalSize, string? Sha256)
{
    public void Validate(long offset, int length)
    {
        if (Offset != offset || Data is null || Data.Length > length || offset < 0 || offset > long.MaxValue - Data.Length || (!Eof && Data.Length == 0))
            throw new InvalidDataException("Invalid or truncated media chunk.");
        var end = offset + Data.Length;
        if (TotalSize is < 0 || (TotalSize is not null && TotalSize < end)) throw new InvalidDataException("Invalid media size.");
        if (Eof && (TotalSize != end || !Regex.IsMatch(Sha256 ?? "", "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Media EOF lacks a verified size and SHA-256.");
        if (!Eof && Sha256 is not null) throw new InvalidDataException("Unexpected nonfinal media digest.");
    }
}
public sealed record MediaTransferProgress(int CompletedFiles, int TotalFiles, long BytesTransferred, long CurrentFileBytes, long? CurrentFileTotal, string FileName, string Stage);
public sealed record MediaAlbumResult(string Folder, int SavedFiles, int SkippedFiles, long BytesTransferred);

public interface IMediaTransport : IAsyncDisposable
{
    string DisplayName { get; }
    int PreferredMediaChunkSize { get; }
    Task<MediaCatalogue> CatalogueAsync(CancellationToken cancellationToken);
    Task<MediaFilePage> FilesAsync(string albumId, int cursor, CancellationToken cancellationToken);
    Task<MediaChunk> ChunkAsync(MediaFile file, long offset, int length, CancellationToken cancellationToken);
}

internal static class MediaValidation
{
    internal static void Id(string value)
    {
        if (!Regex.IsMatch(value ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid media identifier.");
    }
}
