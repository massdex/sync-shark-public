using System.Text;

namespace Massdex.SyncShark.V2;

public static class PathRules
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    internal static string ToExtendedWindowsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }

    public static string NormalizeRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) throw new ArgumentException("A relative path is required.", nameof(value));
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                              IsUnsupportedWindowsSegment(part)))
            throw new ArgumentException("The relative path contains an unsafe segment.", nameof(value));
        return string.Join('/', parts);
    }

    private static bool IsUnsupportedWindowsSegment(string value)
    {
        if (value.EndsWith(' ') || value.EndsWith('.')) return true;
        var stem = value.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    public static string CanonicalComparisonKey(string value) =>
        NormalizeRelative(value).Normalize(NormalizationForm.FormC).ToUpperInvariant();

    internal static string NormalizeInventoryPath(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.StartsWith('/') || value.Contains('\\') ||
            value.Any(char.IsControl) || value.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("Inventory metadata requires a contained relative provider path.", nameof(value));
        return value;
    }

    internal static string NormalizeDropboxRelative(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.StartsWith('/') || value.Contains('\\') || value.Any(char.IsControl) ||
            value.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("A contained relative Dropbox path is required.", nameof(value));
        return value;
    }

    internal static string DropboxComparisonKey(string value) =>
        NormalizeDropboxRelative(value).Normalize(NormalizationForm.FormC).ToUpperInvariant();

    internal static bool IsWindowsTransferPath(string value)
    {
        try { return string.Equals(NormalizeRelative(value), value, StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    public static string ResolveContainedPath(string root, string relativePath)
    {
        var normalizedRoot = NormalizeLocalRoot(root);
        var normalizedRelative = NormalizeRelative(relativePath);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot,
            normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The relative path escapes the selected local root.");
        return combined;
    }

    public static bool IsPathInside(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasExactRelativePath(string root, string candidate, string expectedRelativePath)
    {
        if (!IsPathInside(root, candidate)) return false;
        var observed = NormalizeRelative(Path.GetRelativePath(Path.GetFullPath(root),
            Path.GetFullPath(candidate)));
        return string.Equals(observed, NormalizeRelative(expectedRelativePath), StringComparison.Ordinal);
    }

    public static string NormalizeTopLevelName(string value)
    {
        var normalized = NormalizeRelative(value);
        if (normalized.Contains('/')) throw new ArgumentException("The legacy upload-only workflow accepts top-level files only.", nameof(value));
        return normalized;
    }

    public static string NormalizeLocalRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(full) || string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A bounded local folder below the volume root is required.", nameof(value));
        return full;
    }

    public static string NormalizeDropboxRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = "/" + string.Join('/', value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new ArgumentException("The Dropbox path contains an unsafe segment.", nameof(value));
        return normalized;
    }

    public static string DropboxNamespaceRootIdentity(string rootNamespaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootNamespaceId);
        return "ns:" + rootNamespaceId.Trim();
    }

    public static bool IsDropboxNamespaceRootIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith("ns:", StringComparison.Ordinal) && value.Length > 3;

    public static bool IsDropboxFolderIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        ((value.StartsWith("id:", StringComparison.Ordinal) && value.Length > 3) ||
         IsDropboxNamespaceRootIdentity(value));

    public static bool IsUnsafeAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    public static bool IsOnlineOnlyAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    public static bool IsCloudFilesReparseTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au;

    public static bool IsLocalCloudFilesObject(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && IsCloudFilesReparseTag(reparseTag) &&
        !IsOnlineOnlyAttributes(attributes);

    public static bool IsOnlineOnlyStub(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.Directory) == 0 && IsOnlineOnlyAttributes(attributes) &&
        ((attributes & FileAttributes.ReparsePoint) == 0 || IsCloudFilesReparseTag(reparseTag));

    public static bool IsLinkReparseTag(uint tag) => tag is 0xA0000003u or 0xA000000Cu or 0xA000001Du;

    public static bool IsLink(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && IsLinkReparseTag(reparseTag);

    public static ForeignObjectKind ClassifyForeign(FileAttributes attributes, uint reparseTag) =>
        IsLink(attributes, reparseTag) ? ForeignObjectKind.Link
        : IsLocalCloudFilesObject(attributes, reparseTag) ? ForeignObjectKind.LocalCloud
        : (attributes & FileAttributes.Directory) == 0 && IsOnlineOnlyAttributes(attributes)
            ? ForeignObjectKind.OnlineOnlyFile
            : ForeignObjectKind.None;

    public static bool IsUnsafeObject(FileAttributes attributes, uint reparseTag) =>
        IsUnsafeAttributes(attributes) && !IsLocalCloudFilesObject(attributes, reparseTag);
}
