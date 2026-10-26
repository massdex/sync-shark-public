using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record GoogleDriveRevisionEntry(string RelativePath, string FileId, string RevisionId,
    long Size, string Md5, DateTimeOffset? ModifiedUtc, bool KeepForever);

public sealed partial class GoogleDrivePilotClient
{
    private const string RevisionFields = "id,mimeType,size,md5Checksum,modifiedTime,keepForever";
    internal string? RevisionStagingRootForTest { get; set; }

    private async Task<(string FileId, string Version, string RelativePath, GoogleDriveEntryKind Kind)>
        ResolveRevisionLocationAsync(string relativePath, CancellationToken cancellationToken)
    {
        var (target, _, segments) = await ResolveNavigationLocationAsync(_connection.ConnectionId,
            relativePath, cancellationToken);
        return (target.Entry.FileId, target.Entry.Version!,
            segments.Length == 0 ? "." : string.Join('/', segments), target.Entry.Kind);
    }

    public async Task<IReadOnlyList<GoogleDriveRevisionEntry>> ListRevisionsAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        var location = await ResolveRevisionLocationAsync(relativePath, cancellationToken);
        if (location.Kind != GoogleDriveEntryKind.Blob)
            throw new GoogleDriveBlockedException("Byte recovery applies to binary files. Open Google-native documents in their editor to use document version history.");
        var rows = new Dictionary<string, GoogleDriveRevisionEntry>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            using var page = await GetJsonAsync("files/" + Uri.EscapeDataString(location.FileId) +
                "/revisions?pageSize=200&fields=nextPageToken,revisions(" + RevisionFields + ")" +
                (cursor is null ? "" : "&pageToken=" + Uri.EscapeDataString(cursor)), cancellationToken);
            if (!page.RootElement.TryGetProperty("revisions", out var revisions) || revisions.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Google returned an incomplete revision list.");
            foreach (var item in revisions.EnumerateArray())
            {
                var revision = ReadRevision(item, location.RelativePath, location.FileId);
                if (rows.TryGetValue(revision.RevisionId, out var prior) && prior != revision)
                    throw new IOException("Google revision metadata changed during paging. Refresh before restoring.");
                rows[revision.RevisionId] = revision;
            }
            cursor = page.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (cursor is not null && (!cursors.Add(cursor) || cursors.Count >= 100))
                throw new IOException("The bounded Google revision listing could not finish. No restoration started.");
        } while (cursor is not null);
        var after = await ResolveRevisionLocationAsync(relativePath, cancellationToken);
        if (after.FileId != location.FileId || after.Version != location.Version)
            throw new IOException("The Google file changed during revision listing. Refresh before restoring.");
        return rows.Values.OrderByDescending(row => row.ModifiedUtc).ToArray();
    }

    private static GoogleDriveRevisionEntry ReadRevision(JsonElement item, string path, string fileId)
    {
        var id = item.GetProperty("id").GetString();
        var md5 = item.TryGetProperty("md5Checksum", out var checksum) ? checksum.GetString() : null;
        var sizeText = item.TryGetProperty("size", out var sizeElement) ? sizeElement.ToString() : null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 1024 || id.Any(char.IsControl) ||
            md5 is null || md5.Length != 32 || !md5.All(Uri.IsHexDigit) ||
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
            throw new InvalidDataException("Google did not supply an exact binary revision identity, size and checksum.");
        DateTimeOffset? modified = item.TryGetProperty("modifiedTime", out var time) &&
            DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : null;
        return new(path, fileId, id, size, md5.ToLowerInvariant(), modified,
            item.TryGetProperty("keepForever", out var keep) && keep.ValueKind == JsonValueKind.True);
    }

    private async Task<GoogleDriveRevisionEntry> GetRevisionAsync(GoogleDriveRevisionEntry expected,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("files/" + Uri.EscapeDataString(expected.FileId) +
            "/revisions/" + Uri.EscapeDataString(expected.RevisionId) + "?fields=" + RevisionFields, cancellationToken);
        var current = ReadRevision(document.RootElement, expected.RelativePath, expected.FileId);
        if (current.RevisionId != expected.RevisionId || current.Size != expected.Size || current.Md5 != expected.Md5)
            throw new IOException("The selected Google revision is no longer available with its reviewed checksum and size.");
        return current;
    }

    // The caller receives bytes only after exact revision metadata and complete MD5 validation.
    // Re-upload uses independently computed SHA-256, never the revision's MD5 as a sync hash.
    internal async Task<T> WithRevisionBytesAsync<T>(GoogleDriveRevisionEntry expected,
        Func<Stream, string, Task<T>> consume, CancellationToken cancellationToken,
        Func<Func<Task>, Task>? retainRevision = null, Action<string>? onPrivateStage = null)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (expected.Size < 0 || expected.Size > long.MaxValue / 2)
            throw new InvalidDataException("The revision size cannot be reserved safely.");
        var location = await ResolveRevisionLocationAsync(expected.RelativePath, cancellationToken);
        if (location.FileId != expected.FileId || location.Kind != GoogleDriveEntryKind.Blob)
            throw new IOException("The selected file no longer belongs to this pair at its reviewed path.");
        var current = await GetRevisionAsync(expected, cancellationToken);
        var root = Path.GetFullPath(RevisionStagingRootForTest ?? SyncSharkRuntimePaths.ReplacementStagingRoot);
        using var parent = PilotDirectoryLease.OpenProductDirectory(Path.GetDirectoryName(root)!);
        using var staging = PilotDirectoryLease.OpenOrCreateDescendant(parent, Path.GetFileName(root));
        var volume = Path.GetPathRoot(root)!;
        using var reservation = await SyncRecoverySpoolAdmission.ReserveAsync(volume, checked(expected.Size * 2),
            Guid.NewGuid().ToString("N"), new DriveInfo(volume).AvailableFreeSpace, 64L * 1024 * 1024, cancellationToken);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        onPrivateStage?.Invoke(path);
        await using var bytes = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        staging.VerifyBinding();
        if (!string.Equals(PilotDirectoryLease.FinalPath(bytes.SafeFileHandle), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The revision staging path changed during creation.");
        if (!current.KeepForever)
        {
            if (retainRevision is null) await KeepAsync();
            else await retainRevision(KeepAsync);
        }
        async Task KeepAsync()
        {
            // Drive requires a retained revision for media download. This is part of the explicitly
            // previewed restore operation, never a side effect of listing or inspecting history.
            using var keep = await AuthorizedAsync(HttpMethod.Patch, ApiRoot + "files/" +
                Uri.EscapeDataString(expected.FileId) + "/revisions/" + Uri.EscapeDataString(expected.RevisionId) +
                "?fields=" + RevisionFields, cancellationToken);
            keep.Content = new StringContent("{\"keepForever\":true}", Encoding.UTF8, "application/json");
            using var retained = await SendEffectJsonAsync(keep, cancellationToken);
            var readback = ReadRevision(retained.RootElement, expected.RelativePath, expected.FileId);
            if (!readback.KeepForever || readback.RevisionId != expected.RevisionId ||
                readback.Size != expected.Size || readback.Md5 != expected.Md5)
                throw new GoogleDriveEffectUncertainException("Google did not confirm retention of the exact selected revision. No file was restored.");
        }
        using var request = await AuthorizedAsync(HttpMethod.Get, ApiRoot + "files/" + Uri.EscapeDataString(expected.FileId) +
            "/revisions/" + Uri.EscapeDataString(expected.RevisionId) + "?alt=media", cancellationToken);
        Interlocked.Increment(ref _providerCallCount);
        using var response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ThrowIfFailed(response);
        if (response.Content.Headers.ContentLength is { } length && length != expected.Size)
            throw new IOException("The Google revision download has a different length.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[128 * 1024];
        long total = 0;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (read > expected.Size - total) throw new IOException("The Google revision exceeds its reserved size.");
            total += read; md5.AppendData(buffer, 0, read); sha256.AppendData(buffer, 0, read);
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total != expected.Size || !string.Equals(Convert.ToHexString(md5.GetHashAndReset()), expected.Md5, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Google revision bytes failed exact checksum verification.");
        await GetRevisionAsync(expected, cancellationToken);
        var after = await ResolveRevisionLocationAsync(expected.RelativePath, cancellationToken);
        if (after.FileId != expected.FileId || after.Kind != GoogleDriveEntryKind.Blob)
            throw new IOException("The selected file moved outside its reviewed pair path during revision download.");
        staging.VerifyBinding(); parent.VerifyBinding();
        await bytes.FlushAsync(cancellationToken); bytes.Position = 0;
        cancellationToken.ThrowIfCancellationRequested();
        return await consume(bytes, Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }
}
