using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Albiqo.Transfer.Core;

public sealed record ArchiveInfo(string Id, string AlbumName, string FileName, long Size, string Sha256, DateTimeOffset CreatedAt)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 200 || Id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(AlbumName) || AlbumName.Length > 500 ||
            string.IsNullOrWhiteSpace(FileName) || FileName.Length > 500 || Size < 0 ||
            !Regex.IsMatch(Sha256 ?? "", "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid archive metadata.");
    }
}
public sealed record ArchiveCatalogue(int ProtocolVersion, ArchiveInfo[] Archives)
{
    public void Validate()
    {
        if (ProtocolVersion != 1 || Archives is null || Archives.Length > 32)
            throw new InvalidDataException("Unsupported transfer catalogue.");
        foreach (var archive in Archives) archive.Validate();
        if (Archives.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Archives.Length)
            throw new InvalidDataException("Duplicate archive identifiers.");
    }
}
public sealed record TransferSession(int ProtocolVersion, string DeviceName, string SessionId, DateTimeOffset ExpiresAt);
public sealed record RemoteError(string Code, string Message);
public sealed record TransferProgress(long Completed, long Total, string Stage);
public sealed record UsbDevice(uint DeviceId, string Serial, string Name);

public interface IArchiveTransport : IAsyncDisposable
{
    string DisplayName { get; }
    int PreferredReadSize => 65536;
    Task<TransferSession> AuthenticateAsync(string pin, CancellationToken cancellationToken);
    Task<ArchiveCatalogue> ListAsync(CancellationToken cancellationToken);
    Task<byte[]> ReadRangeAsync(ArchiveInfo archive, long offset, int length, CancellationToken cancellationToken);
}

public static class TransferJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        MaxDepth = 12
    };
    public static T Decode<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes, Options)
        ?? throw new InvalidDataException("The phone returned an empty response.");
}

public static class WindowsFileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    public static string SafeZipName(string name)
    {
        var baseName = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        baseName = new string(baseName.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (baseName.Length == 0 || baseName is "." or "..") baseName = "Albiqo Album";
        if (Reserved.Contains(baseName.Split('.')[0])) baseName = "_" + baseName;
        if (baseName.Length > 120) baseName = baseName[..120].TrimEnd('.', ' ');
        return baseName + ".zip";
    }
}
