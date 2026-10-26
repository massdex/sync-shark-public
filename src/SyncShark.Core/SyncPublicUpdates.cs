using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Massdex.SyncShark;

// GitHub is a distribution channel, not an execution authority. Metadata and downloads
// remain untrusted until the installed, pinned-signature update bridge verifies them.
public sealed record SyncPublicUpdate(string Version, Uri Archive, long ArchiveBytes,
    string ArchiveSha256, string InstallerSha256);

public sealed class SyncPublicUpdates : IDisposable
{
    public const string Repository = "massdex/sync-shark-releases";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    public const int MaxInstallerBytes = 1024 * 1024;
    internal const long MaxArchiveBytes = 128L * 1024 * 1024, MaxExpandedBytes = 256L * 1024 * 1024;
    private readonly HttpClient _http;

    public SyncPublicUpdates() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    internal SyncPublicUpdates(HttpMessageHandler handler) => _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<SyncPublicUpdate?> FindAsync(string currentIdentity, CancellationToken cancellationToken = default)
    {
        if (!Version.TryParse(currentIdentity.Split('+')[0], out var current)) return null;
        var offers = new List<(string Version, JsonElement Installer, JsonElement Archive)>();
        // Inspect recent stable releases, not just "latest": each signed installer names
        // its exact supported predecessor, so skipping an intermediate release is unsafe.
        for (var page = 1; page <= 4; page++)
        {
            var uri = new Uri($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            var bytes = await ReadAsync(uri, MaxMetadataBytes, cancellationToken);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 100)
                throw new InvalidDataException("The release channel returned an invalid release list.");
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
                var tag = item.GetProperty("tag_name").GetString() ?? "";
                if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant) ||
                    !Version.TryParse(tag[1..], out var version) || version <= current) continue;
                var assets = item.GetProperty("assets").EnumerateArray().ToArray();
                var installers = assets.Where(asset => asset.GetProperty("name").GetString() == "Install-SyncShark.ps1").ToArray();
                var archives = assets.Where(asset => asset.GetProperty("name").GetString() == $"SyncShark-{tag}-win-x64.zip").ToArray();
                if (installers.Length == 1 && archives.Length == 1)
                    offers.Add((version.ToString(), installers[0].Clone(), archives[0].Clone()));
            }
            if (json.RootElement.GetArrayLength() < 100) break;
        }
        foreach (var offer in offers.OrderByDescending(item => Version.Parse(item.Version)).Take(32))
        {
            var installerUri = AssetUri(offer.Installer, offer.Version, "Install-SyncShark.ps1");
            var installerSize = offer.Installer.GetProperty("size").GetInt64();
            if (installerSize <= 0 || installerSize > MaxInstallerBytes) continue;
            var installer = await ReadAsync(installerUri, MaxInstallerBytes, cancellationToken);
            if (installer.Length != installerSize) throw new IOException("The release installer download was incomplete.");
            var text = new UTF8Encoding(false, true).GetString(installer).TrimStart('\uFEFF');
            var first = text.Split('\n', 2)[0].TrimEnd('\r');
            if (SyncUpdateDiscovery.CompatibleVersion(first, currentIdentity) != offer.Version) continue;
            var archive = AssetUri(offer.Archive, offer.Version, $"SyncShark-v{offer.Version}-win-x64.zip");
            var size = offer.Archive.GetProperty("size").GetInt64();
            var digest = offer.Archive.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
            if (size <= 0 || size > MaxArchiveBytes || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("The release archive has no supported size and checksum.");
            return new(offer.Version, archive, size, digest[7..], Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant());
        }
        return null;
    }

    private static Uri AssetUri(JsonElement asset, string version, string name)
    {
        var expected = $"https://github.com/{Repository}/releases/download/v{version}/{name}";
        if (asset.GetProperty("browser_download_url").GetString() != expected || asset.GetProperty("state").GetString() != "uploaded")
            throw new InvalidDataException("The release asset does not belong to the registered channel.");
        return new Uri(expected);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                !(uri.Host == "api.github.com" || uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com"))
                throw new InvalidDataException("The release channel redirected outside its allowed download service.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SyncShark-Updates/1.0");
            if (uri.Host == "api.github.com")
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location;
                response.Dispose();
                if (next is null) throw new IOException("The release download has an invalid redirect.");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode; response.Dispose();
                throw new IOException(status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    ? "GitHub temporarily limited update checks. Try again later."
                    : "The public update channel is unavailable. Try again later.");
            }
            return response;
        }
        throw new IOException("The release download redirected too many times.");
    }

    private async Task<byte[]> ReadAsync(Uri uri, int maximum, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(uri, cancellationToken);
        if (response.Content.Headers.ContentLength > maximum) throw new IOException("The update response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, maximum, cancellationToken);
        return output.ToArray();
    }

    internal static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536]; long total = 0;
        while (await input.ReadAsync(buffer, cancellationToken) is var count && count != 0)
        {
            total = checked(total + count);
            if (total > maximum) throw new IOException("The update download exceeded its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return total;
    }

    public async Task<SyncDownloadedUpdate> DownloadAsync(SyncPublicUpdate update, string currentIdentity,
        CancellationToken cancellationToken = default, Func<string, Task>? allocated = null)
    {
        var expectedUri = $"https://github.com/{Repository}/releases/download/v{update.Version}/SyncShark-v{update.Version}-win-x64.zip";
        if (!Regex.IsMatch(update.Version, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant) ||
            update.Archive.AbsoluteUri != expectedUri || update.ArchiveBytes is <= 0 or > MaxArchiveBytes ||
            !Regex.IsMatch(update.ArchiveSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(update.InstallerSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The selected update identity is invalid.");
        using var parent = PilotDirectoryLease.OpenProductDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
        // One bounded active download in the existing product staging allocation; no new
        // AppData root, sync folder or installer architecture is introduced.
        var drive = new DriveInfo(Path.GetPathRoot(parent.Path)!);
        if (drive.AvailableFreeSpace < 10L * 1024 * 1024 * 1024 + MaxArchiveBytes + MaxExpandedBytes)
            throw new IOException("Free at least 10.4 GB on the installation drive before downloading an update.");
        var result = new SyncDownloadedUpdate(PilotDirectoryLease.CreateChild(parent, Guid.NewGuid().ToString("N")));
        try
        {
            if (allocated is not null) await allocated(result.Directory);
            using var response = await SendAsync(update.Archive, cancellationToken);
            if (response.Content.Headers.ContentLength is { } length && length != update.ArchiveBytes)
                throw new IOException("The release archive size changed.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var archive = result.CreateFile("package.zip"))
                if (await CopyBoundedAsync(input, archive, update.ArchiveBytes, cancellationToken) != update.ArchiveBytes)
                    throw new IOException("The update archive was truncated.");
            await using (var archive = File.OpenRead(Path.Combine(result.Directory, "package.zip")))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken)).ToLowerInvariant();
                if (digest != update.ArchiveSha256) throw new InvalidDataException("The release archive checksum does not match.");
                archive.Position = 0;
                await result.ExtractAsync(archive, cancellationToken);
            }
            var installer = await File.ReadAllBytesAsync(result.Installer, cancellationToken);
            if (Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant() != update.InstallerSha256 ||
                SyncUpdateDiscovery.CompatibleVersion(Encoding.UTF8.GetString(installer).TrimStart('\uFEFF').Split('\n', 2)[0].TrimEnd('\r'), currentIdentity) != update.Version)
                throw new InvalidDataException("The downloaded installer changed or does not support this installed version.");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose() => _http.Dispose();
}

// Owns only members created by this one download. Cleanup never recursively enumerates
// or removes arbitrary product state. A changed/reparse directory fails closed.
public sealed class SyncDownloadedUpdate : IDisposable
{
    private readonly PilotDirectoryLease _root;
    private readonly Dictionary<string, PilotDirectoryLease> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _files = [];
    private bool _disposed;
    internal SyncDownloadedUpdate(PilotDirectoryLease root) { _root = root; _directories.Add("", root); }
    public string Directory => _root.Path;
    public string Installer => Path.Combine(Directory, "scripts", "Install-SyncShark.ps1");

    // The already verified installer takes over while the app exits. Keep exactly this
    // download; the saved folder pointer lets the next launch recover or clean it.
    public void ReleaseToInstaller()
    {
        if (_disposed) return; _disposed = true;
        foreach (var directory in _directories.Values.Reverse()) directory.Dispose();
    }

    internal FileStream CreateFile(string relative)
    {
        _root.VerifyBinding();
        var parts = relative.Split('/'); var parent = _root; var key = "";
        foreach (var part in parts[..^1])
        {
            var next = key.Length == 0 ? part : key + "/" + part;
            if (!_directories.TryGetValue(next, out var child))
            { child = PilotDirectoryLease.CreateChild(parent, part, hidden: false); _directories.Add(next, child); }
            parent = child; key = next;
        }
        parent.VerifyBinding();
        var path = Path.Combine(parent.Path, parts[^1]);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous);
        _files.Add(path); return file;
    }

    internal async Task ExtractAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is 0 or > 512) throw new InvalidDataException("The update archive has too many members.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var isDirectory = name.EndsWith('/');
            if (isDirectory) name = name[..^1];
            ValidateName(name);
            var parts = name.Split('/');
            for (var depth = 1; depth < parts.Length; depth++)
                directories.Add(string.Join('/', parts.Take(depth)));
            if (isDirectory)
            {
                if (!IsPackageDirectory(name) || entry.Length != 0 ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x4000) ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The update archive contains an unsafe directory.");
                directories.Add(name);
                continue;
            }
            if (!names.Add(name) || entry.Length > 64L * 1024 * 1024 ||
                name == "scripts/Install-SyncShark.ps1" && entry.Length > SyncPublicUpdates.MaxInstallerBytes ||
                (expanded = checked(expanded + entry.Length)) > SyncPublicUpdates.MaxExpandedBytes ||
                ((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update archive has an unsafe, duplicate or oversized member.");
            if (!IsPackageFile(name))
                throw new InvalidDataException("The update archive contains a member outside the release package.");
        }
        // Include implicit parents, the allocation root and the downloaded ZIP. The same
        // limit must remain recoverable by the next launch's bounded cleanup inventory.
        if (names.Count + directories.Count + 2 > 512 || names.Overlaps(directories))
            throw new InvalidDataException("The update archive has too many paths or a file/directory collision.");
        if (!names.Contains("scripts/Install-SyncShark.ps1") || !names.Contains("scripts/SyncShark.Release.psm1") ||
            !names.Contains("package/unsigned-candidate-framework-manifest.json"))
            throw new InvalidDataException("The update archive is missing required release members.");
        foreach (var entry in zip.Entries.Where(item => !item.FullName.EndsWith('/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = entry.Open(); await using var file = CreateFile(entry.FullName);
            if (await SyncPublicUpdates.CopyBoundedAsync(input, file, entry.Length, cancellationToken) != entry.Length)
                throw new IOException("An update archive member was truncated.");
        }
    }

    private static void ValidateName(string name)
    {
        if (name.Length is 0 or > 200 || name.Contains('\\') || name.Split('/').Length > 8)
            throw new InvalidDataException("The update archive contains an unsafe path.");
        foreach (var part in name.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("The update archive contains an unsafe path.");
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            foreach (var directory in _directories.Values) directory.VerifyBinding();
            foreach (var path in _files) File.Delete(path);
            foreach (var directory in _directories.Values.Reverse()) { directory.DeleteOnClose(); directory.Dispose(); }
        }
        finally { foreach (var directory in _directories.Values) directory.Dispose(); }
    }

    private static bool IsPackageDirectory(string relative) =>
        relative is "scripts" or "package" or "package/signed-candidate" ||
        relative.StartsWith("package/signed-candidate/", StringComparison.Ordinal);

    // Extraction and crash cleanup own the same closed package shape. Accepting
    // the fixed worker as data does not authorize execution: the signed installer
    // and protected admission still bind its exact bytes and product signer.
    private static bool IsPackageFile(string relative) =>
        relative is "scripts/Install-SyncShark.ps1" or "scripts/Rollback-SyncShark.ps1" or
            "scripts/SyncShark.Release.psm1" or "scripts/SyncSharkUpdater.exe" or
            "package/unsigned-candidate-framework-manifest.json" ||
        relative.StartsWith("package/signed-candidate/", StringComparison.Ordinal) &&
            Path.GetExtension(relative).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".msix" or ".config" or ".ico" or ".png";

    public static string RequireDownloadDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), SyncSharkRuntimePaths.ReplacementStagingRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(full), "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
            throw new IOException("The retained update folder is outside the download allocation.");
        return full;
    }

    public static void CleanupRetained(string path)
    {
        var full = RequireDownloadDirectory(path);
        if (!System.IO.Directory.Exists(full)) return;
        using var parent = PilotDirectoryLease.OpenProductDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
        var held = new List<PilotDirectoryLease>(); var directories = new List<string>(); var files = new List<string>();
        try
        {
            void Inspect(string directory)
            {
                held.Add(PilotDirectoryLease.OpenProductDirectory(directory)); directories.Add(directory);
                foreach (var member in System.IO.Directory.EnumerateFileSystemEntries(directory))
                {
                    if (files.Count + directories.Count >= 512) throw new IOException("The retained update has unexpected extra members; it was preserved.");
                    var relative = Path.GetRelativePath(full, member).Replace('\\', '/');
                    ValidateName(relative);
                    var attributes = File.GetAttributes(member);
                    if (PilotPath.IsUnsafeAttributes(attributes)) throw new IOException("The retained update changed to an unsafe object; it was preserved.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!IsPackageDirectory(relative))
                            throw new IOException("The retained update has unexpected directories; it was preserved.");
                        Inspect(member);
                    }
                    else
                    {
                        if (relative != "package.zip" && !IsPackageFile(relative))
                            throw new IOException("The retained update has unexpected files; it was preserved.");
                        files.Add(member);
                    }
                }
            }
            Inspect(full);
            foreach (var directory in held) directory.VerifyBinding();
            foreach (var file in files) File.Delete(file);
            for (var index = held.Count - 1; index >= 0; index--)
            { held[index].Dispose(); System.IO.Directory.Delete(directories[index], recursive: false); }
        }
        finally { foreach (var directory in held) directory.Dispose(); }
    }
}
