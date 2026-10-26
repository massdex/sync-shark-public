namespace Massdex.SyncShark;

public static class SyncLocalRootAvailability
{
    // Bounded root metadata only, never a network crawl or a directory-content scan.
    public static bool? Observe(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !Path.IsPathFullyQualified(path)) return null;
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}
