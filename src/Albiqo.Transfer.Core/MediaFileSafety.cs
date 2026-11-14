using System.Text;

namespace Albiqo.Transfer.Core;

public static class MediaFileSafety
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };

    public static string FolderName(string name) => Component(name, 120);
    public static string FileName(string name)
    {
        var safe = Component(name, int.MaxValue);
        var extension = Path.GetExtension(safe);
        if (extension.Length > 24) return Truncate(safe, 180);
        var stem = safe[..^extension.Length];
        if (stem.Length + extension.Length <= 180) return safe;
        return Truncate(stem, 180 - extension.Length) + extension;
    }

    private static string Component(string name, int maximum)
    {
        var builder = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var text = rune.ToString();
            builder.Append(rune.Value == 0xfffd || rune.Value < 32 || rune.Value == 127 || (text.Length == 1 && "<>:\"/\\|?*".Contains(text[0])) ? "_" : text);
        }
        var result = builder.ToString().Normalize(NormalizationForm.FormC).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(result) || result is "." or "..") result = "Albiqo";
        if (Reserved.Contains(result.Split('.')[0].TrimEnd(' ', '.'))) result = "_" + result;
        return Truncate(result, maximum).TrimEnd('.', ' ');
    }

    private static string Truncate(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        var count = maximum;
        if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
        return value[..count];
    }

    internal static void RejectLink(string path)
    {
        // File.Exists is false for a dangling link; GetAttributes still identifies that link.
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files or folders cannot be used for direct transfer."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static string DirectoryWithoutLinks(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new IOException("Invalid destination.");
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part); RejectLink(current);
            if (File.Exists(current) && !Directory.Exists(current)) throw new IOException("A destination component is a file.");
            Directory.CreateDirectory(current); RejectLink(current);
        }
        return path;
    }

    internal static string Child(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new InvalidDataException("Unsafe saved file name.");
        var path = Path.Combine(directory, name); RejectLink(path); return path;
    }

    internal static string UniqueFile(string directory, string name)
    {
        var candidate = Child(directory, name);
        var extension = Path.GetExtension(name); var stem = name[..^extension.Length];
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++) candidate = Child(directory, $"{stem} ({n}){extension}");
        return candidate;
    }
}
