using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SyncUpdateCandidate(string InstallerPath, string Version);

// Discovery is untrusted input selection only. Nothing returned here is executable or
// "verified": the existing signed update bridge must independently bind every member.
public static class SyncUpdateDiscovery
{
    internal static string? CompatibleVersion(string firstLine, string currentIdentity)
    {
        const string prefix = "# SyncSharkUpdateContract: ";
        if (firstLine.Length > 16384 || !firstLine.StartsWith(prefix, StringComparison.Ordinal) ||
            !Version.TryParse(currentIdentity.Split('+')[0], out var current)) return null;
        try
        {
            using var json = JsonDocument.Parse(firstLine[prefix.Length..], new JsonDocumentOptions { MaxDepth = 8 });
            var value = json.RootElement;
            return value.GetProperty("product").GetString() == "Massdex.SyncShark" &&
                value.GetProperty("previousVersion").GetString() == currentIdentity &&
                Version.TryParse(value.GetProperty("version").GetString(), out var version) && version > current
                ? version.ToString() : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static IReadOnlyList<SyncUpdateCandidate> FindCandidates(string deliveryFolder, string currentIdentity)
    {
        var root = Path.GetFullPath(deliveryFolder);
        if (!Path.IsPathFullyQualified(deliveryFolder) || !Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose an available ordinary folder containing delivered Sync Shark packages.");
        if (!Version.TryParse(currentIdentity.Split('+')[0], out var current)) return [];
        var paths = new List<string> { Path.Combine(root, "Install-SyncShark.ps1"), Path.Combine(root, "scripts", "Install-SyncShark.ps1") };
        foreach (var child in Directory.EnumerateDirectories(root).Take(64))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            paths.Add(Path.Combine(child, "scripts", "Install-SyncShark.ps1"));
        }
        var result = new List<SyncUpdateCandidate>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(path).Length > 1024 * 1024 ||
                    (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0) continue;
                using var reader = new StreamReader(path);
                var first = reader.ReadLine();
                if (first is not null && CompatibleVersion(first, currentIdentity) is { } version)
                    result.Add(new(path, version));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return result.OrderByDescending(item => Version.Parse(item.Version)).ToArray();
    }
}
