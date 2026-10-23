using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Massdex.SyncShark;

// Reviewed Google Drive Trash transport: the provider-bound listing and restore half of the existing
// Recovery surface. It is deliberately narrow. It lists only trashed items whose parent chain is proved
// back to this client's bound root folder, and it restores only the exact object a caller reviewed, using
// the existing guarded v2 conditional metadata write. It does not implement account-wide rewind, file
// requests, permanent deletion, emptyTrash or any overwrite/rename effect, and it never creates a copy.
public sealed record GoogleDriveTrashEntry(
    string ConnectionId,
    string RootFolderId,
    string RelativePath,
    string FileId,
    string? Version,
    GoogleDriveEntryKind Kind,
    string? Name,
    long Size,
    string? ContentHash,
    string? ParentId,
    string? DriveId,
    bool CanRestore);

public sealed record GoogleDriveTrashPage(
    IReadOnlyList<GoogleDriveTrashEntry> Entries,
    string? ContinueCursor,
    IReadOnlyList<string> Warnings);

public sealed partial class GoogleDrivePilotClient
{
    public const int MaximumTrashPageSize = 100;

    // Bounds for the ancestry proof: the parent chain may not be deeper than this, and one listing call
    // may not read more than this many metadata objects. Exhausting either bound fails the call visibly
    // instead of continuing with ancestry that cannot be trusted.
    internal const int MaximumTrashAncestorDepth = 128;
    internal const int MaximumTrashMetadataReads = 500;

    private const string TrashCursorKind = "google-drive-trash-cursor-v1";

    // explicitlyTrashed distinguishes an item that was trashed itself from one that only inherits a
    // trashed ancestor; canUntrash is the capability Google reports for this account and item.
    private const string TrashMetadataFields =
        "id,name,mimeType,parents,trashed,explicitlyTrashed,size,md5Checksum,sha256Checksum,version," +
        "headRevisionId,driveId,shortcutDetails,capabilities(canUntrash,canDownload)";

    private sealed record TrashMetadata(GoogleDriveInventoryEntry Entry, bool ExplicitlyTrashed, bool CanUntrash);

    private sealed record TrashMembership(string? RelativePath, bool HasTrashedAncestor, string Reason);

    private sealed class TrashReadBudget(int maximumReads)
    {
        private int _reads;

        internal void ConsumeMetadataRead()
        {
            _reads++;
            if (_reads > maximumReads)
                throw new GoogleDriveBlockedException(
                    "The Google Drive trash listing exceeded its bounded budget of " +
                    maximumReads.ToString(CultureInfo.InvariantCulture) +
                    " metadata reads, so the remaining ancestry cannot be proved. Nothing was changed.");
        }
    }

    // Provider-bound page of trashed items that are verified descendants of this pair's root folder.
    // pageSize is restricted to 1..100; the continuation cursor binds the account, the root folder and
    // the exact corpus in addition to the provider page token.
    public async Task<GoogleDriveTrashPage> ListTrashPageAsync(string? cursor = null, int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (pageSize < 1 || pageSize > MaximumTrashPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize),
                "A Google Drive trash page size must be between 1 and " +
                MaximumTrashPageSize.ToString(CultureInfo.InvariantCulture) + ".");
        // A malformed or differently bound cursor is refused before any Google Drive call. A cursor
        // cannot move a page between accounts, roots or corpora.
        var resumeToken = string.IsNullOrWhiteSpace(cursor) ? null :
            ReadTrashCursor(cursor!, verifyCorpus: !_allowSharedDrives || _sharedDriveId is not null);
        await VerifyAccountAsync(cancellationToken);
        await VerifyRootIdentityAsync(cancellationToken);
        if (resumeToken is not null) ReadTrashCursor(cursor!);
        var corpusKind = _sharedDriveId is null ? "user" : "drive";
        var corpusId = _sharedDriveId ?? _connection.PermissionId;
        var url = "files?q=" + Uri.EscapeDataString("trashed = true") +
                  "&pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture) +
                  "&fields=nextPageToken,incompleteSearch,files(" + TrashMetadataFields + ")" +
                  TrashCorpusParameters();
        if (resumeToken is not null)
            url += "&pageToken=" + Uri.EscapeDataString(RequireOpaqueCursor(resumeToken));
        using var document = await GetJsonAsync(url, cancellationToken);
        var root = document.RootElement;
        var warnings = new List<string>();
        if (root.TryGetProperty("incompleteSearch", out var incomplete) &&
            incomplete.ValueKind == JsonValueKind.True)
            warnings.Add("Google reported an incomplete trash search, so this page may omit trashed items.");
        var entries = new List<GoogleDriveTrashEntry>();
        var budget = new TrashReadBudget(MaximumTrashMetadataReads);
        var cache = new Dictionary<string, GoogleDriveInventoryEntry>(StringComparer.Ordinal);
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Google returned an invalid Trash listing. Refresh Recovery; an empty Trash cannot be inferred.");
        foreach (var item in files.EnumerateArray())
            {
                TrashMetadata metadata;
                try
                {
                    metadata = ReadTrashMetadata(item);
                }
                catch (InvalidDataException)
                {
                    warnings.Add("A trashed Google Drive item had no stable id and was excluded.");
                    continue;
                }
                var membership = await ProveTrashMembershipAsync(metadata, budget, cache, cancellationToken);
                var relativePath = membership.RelativePath;
                if (relativePath is null)
                {
                    warnings.Add("Trashed Google Drive item " + metadata.Entry.FileId + " ('" +
                                 (metadata.Entry.Name ?? "unnamed") + "') was not listed: " +
                                 membership.Reason + ".");
                    continue;
                }
                // A shortcut is retained in the listing with its real kind, but this lane never follows
                // or restores a shortcut object, so it is never offered as restorable.
                var canRestore = metadata.CanUntrash && metadata.ExplicitlyTrashed &&
                                 !membership.HasTrashedAncestor &&
                                 metadata.Entry.Kind != GoogleDriveEntryKind.Shortcut;
                entries.Add(new GoogleDriveTrashEntry(_connection.ConnectionId, _rootFolderId,
                    relativePath, metadata.Entry.FileId, metadata.Entry.Version, metadata.Entry.Kind,
                    metadata.Entry.Name, metadata.Entry.Size, metadata.Entry.Sha256 ?? metadata.Entry.Md5,
                    metadata.Entry.Parents.Count == 1 ? metadata.Entry.Parents[0] : null,
                    metadata.Entry.DriveId, canRestore));
            }
        var next = root.TryGetProperty("nextPageToken", out var nextElement) ? nextElement.GetString() : null;
        if (!string.IsNullOrEmpty(next) && resumeToken is not null &&
            string.Equals(next, resumeToken, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "Google Drive repeated the same trash page token, so the listing cannot finish safely. " +
                "Nothing was changed.");
        return new GoogleDriveTrashPage(entries,
            string.IsNullOrEmpty(next) ? null : EncodeTrashCursor(corpusKind, corpusId, next!),
            warnings);
    }

    // Restores exactly one reviewed trashed item. The reviewed identity, version, kind, name, parent,
    // corpus, content identity and trash state are all re-proved against fresh provider metadata and the
    // bound root before the single guarded metadata write, and the live state is read back afterwards.
    public async Task<GoogleDriveInventoryEntry> RestoreTrashAsync(GoogleDriveTrashEntry expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!string.Equals(expected.ConnectionId, _connection.ConnectionId, StringComparison.Ordinal) ||
            !string.Equals(expected.RootFolderId, _rootFolderId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "This trashed Google Drive item was reviewed for a different account or root folder. " +
                "Nothing was restored; refresh the trash listing for this pair.");
        if (!_capabilities.AtomicConditionalWrites)
            throw new GoogleDriveBlockedException(DriveDestructiveUnsupported);
        if (string.IsNullOrWhiteSpace(expected.FileId))
            throw new InvalidDataException("The reviewed trashed Google Drive item has no stable id.");
        if (!expected.CanRestore)
            throw new GoogleDriveBlockedException(
                "This reviewed Google Drive trash entry was not marked restorable (its capability, trash " +
                "state or parent chain prevented it). Nothing was restored.");
        await VerifyAccountAsync(cancellationToken);
        await VerifyRootIdentityAsync(cancellationToken);

        var reviewed = await GetTrashMetadataAsync(expected.FileId, cancellationToken);
        var current = reviewed.Entry;
        if (!string.Equals(current.FileId, expected.FileId, StringComparison.Ordinal))
            throw new InvalidDataException("The Google Drive item identity changed after review.");
        if (current.Kind != expected.Kind)
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item is no longer the reviewed kind of object. Nothing was restored.");
        if (current.Kind == GoogleDriveEntryKind.Shortcut)
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item is a shortcut, and this lane neither follows nor restores " +
                "shortcuts. Nothing was restored.");
        if (!string.Equals(current.Name, expected.Name, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item was renamed after it was reviewed. Nothing was restored.");
        if (!string.Equals(current.Version, expected.Version, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item changed version after it was reviewed (expected " +
                (expected.Version ?? "unknown") + ", found " + (current.Version ?? "unknown") +
                "). Nothing was restored.");
        if (!current.Trashed)
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item is no longer in the trash, so there is nothing to restore.");
        if (current.Parents.Count != 1)
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item no longer has exactly one parent, so its destination is " +
                "ambiguous. Nothing was restored.");
        if (!string.Equals(current.Parents[0], expected.ParentId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item moved to a different parent after it was reviewed. " +
                "Nothing was restored.");
        if (!string.Equals(current.DriveId, expected.DriveId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item now belongs to a different corpus than the reviewed " +
                "entry. Nothing was restored.");
        if (current.Size != expected.Size)
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item changed size after it was reviewed. Nothing was restored.");
        var currentDigest = current.Sha256 ?? current.Md5;
        if (!string.Equals(currentDigest, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item content identity changed after it was reviewed. " +
                "Nothing was restored.");
        VerifyCorpusMembership(current);

        var budget = new TrashReadBudget(MaximumTrashMetadataReads);
        var cache = new Dictionary<string, GoogleDriveInventoryEntry>(StringComparer.Ordinal);
        var membership = await ProveTrashMembershipAsync(reviewed, budget, cache, cancellationToken);
        if (membership.RelativePath is null)
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item is not a verified descendant of the bound root folder (" +
                membership.Reason + "). Nothing was restored.");
        if (membership.HasTrashedAncestor)
            throw new GoogleDriveBlockedException(
                "A parent folder of the reviewed Google Drive item is still in the trash. Restore that " +
                "folder first; the item was not changed.");
        if (!reviewed.CanUntrash)
            throw new GoogleDriveBlockedException(
                "Google Drive reports that this account cannot restore this item (canUntrash is false). " +
                "Nothing was restored.");
        if (!reviewed.ExplicitlyTrashed)
            throw new GoogleDriveBlockedException(
                "Google Drive reports this item is in the trash because of an ancestor rather than " +
                "explicitly. Restore the owning folder first; the item was not changed.");
        if (string.IsNullOrEmpty(current.Name))
            throw new GoogleDriveBlockedException(
                "The reviewed Google Drive item reports no name, so its destination cannot be checked. " +
                "Nothing was restored.");
        // An active item with the same name in the destination would make the restored path ambiguous.
        var destination = current.Parents[0];
        var siblings = await ListChildrenByNameAsync(destination, current.Name, cancellationToken);
        if (siblings.Any(item => !string.Equals(item.FileId, current.FileId, StringComparison.Ordinal)))
            throw new GoogleDriveBlockedException(
                "Google Drive already contains an active item named '" + current.Name + "' in the " +
                "destination folder. The restore was refused so no duplicate or overwrite is created.");

        // One guarded metadata write on the exact reviewed object; no overwrite, rename, permanent
        // deletion, emptyTrash or retry, and no recursive child effect for a folder.
        var restored = await PatchConditionalAsync(current, new { labels = new { trashed = false } }, null, cancellationToken);
        if (restored.Trashed ||
            !string.Equals(restored.FileId, current.FileId, StringComparison.Ordinal) ||
            restored.Kind != current.Kind ||
            !string.Equals(restored.Name, current.Name, StringComparison.Ordinal) ||
            !restored.Parents.ToHashSet(StringComparer.Ordinal).SetEquals(current.Parents) ||
            !string.Equals(restored.DriveId, current.DriveId, StringComparison.Ordinal) ||
            restored.Size != current.Size ||
            !string.Equals(restored.Sha256 ?? restored.Md5, currentDigest, StringComparison.OrdinalIgnoreCase))
            throw new GoogleDriveEffectUncertainException(
                "The Google Drive restore readback does not show the reviewed item live in its reviewed " +
                "location, so whether the item was restored is unknown.");
        return restored with { RelativePath = expected.RelativePath };
    }

    private async Task<TrashMetadata> GetTrashMetadataAsync(string fileId,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            "files/" + Uri.EscapeDataString(fileId) + "?fields=" + TrashMetadataFields +
            "&supportsAllDrives=" + (SharedDrives ? "true" : "false"), cancellationToken);
        return ReadTrashMetadata(document.RootElement);
    }

    private TrashMetadata ReadTrashMetadata(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idElement) ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
            throw new InvalidDataException("A Google Drive item has no stable id.");
        var entry = ReadInventoryEntry(item, string.Empty);
        var explicitlyTrashed = item.TryGetProperty("explicitlyTrashed", out var explicitElement) &&
                                explicitElement.ValueKind == JsonValueKind.True;
        var canUntrash = false;
        if (item.TryGetProperty("capabilities", out var capabilities) &&
            capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("canUntrash", out var canUntrashElement) &&
            canUntrashElement.ValueKind == JsonValueKind.True)
            canUntrash = true;
        return new TrashMetadata(entry, explicitlyTrashed, canUntrash);
    }

    // Membership is proved only by a bounded, unique, non-cyclic parent chain that reaches the bound root
    // folder inside the same corpus. Unknown ancestry never becomes root membership.
    private async Task<TrashMembership> ProveTrashMembershipAsync(TrashMetadata metadata, TrashReadBudget budget,
        Dictionary<string, GoogleDriveInventoryEntry> cache, CancellationToken cancellationToken)
    {
        var entry = metadata.Entry;
        if (string.Equals(entry.FileId, _rootFolderId, StringComparison.Ordinal))
            return new TrashMembership(null, false, "it is the bound root folder itself");
        try
        {
            VerifyCorpusMembership(entry);
        }
        catch (GoogleDriveBlockedException error)
        {
            return new TrashMembership(null, false, error.Message);
        }
        if (entry.Parents.Count == 0)
            return new TrashMembership(null, false, "it has no parent inside the bound folder");
        if (entry.Parents.Count != 1)
            return new TrashMembership(null, false, "it has more than one parent, so its path is ambiguous");
        var chain = new List<GoogleDriveInventoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { entry.FileId };
        var hasTrashedAncestor = false;
        var parentId = entry.Parents[0];
        while (!string.Equals(parentId, _rootFolderId, StringComparison.Ordinal))
        {
            if (!seen.Add(parentId))
                throw new GoogleDriveBlockedException(
                    "The parent chain of trashed Google Drive item " + entry.FileId +
                    " is cyclic, so its ancestry cannot be proved. Nothing was changed.");
            if (chain.Count + 1 > MaximumTrashAncestorDepth)
                throw new GoogleDriveBlockedException(
                    "The parent chain of trashed Google Drive item " + entry.FileId +
                    " exceeded the bounded depth of " +
                    MaximumTrashAncestorDepth.ToString(CultureInfo.InvariantCulture) +
                    ", so its ancestry cannot be trusted. Nothing was changed.");
            var ancestor = await ReadTrashAncestorAsync(parentId, budget, cache, cancellationToken);
            if (ancestor.Kind == GoogleDriveEntryKind.Shortcut)
                return new TrashMembership(null, hasTrashedAncestor,
                    "an ancestor is a shortcut, and shortcuts are never followed");
            if (ancestor.Parents.Count == 0)
                return new TrashMembership(null, hasTrashedAncestor,
                    "its parent chain leaves the bound folder without reaching the bound root");
            if (ancestor.Parents.Count != 1)
                return new TrashMembership(null, hasTrashedAncestor,
                    "an ancestor has more than one parent, so the path is ambiguous");
            try
            {
                VerifyCorpusMembership(ancestor);
            }
            catch (GoogleDriveBlockedException error)
            {
                return new TrashMembership(null, hasTrashedAncestor, error.Message);
            }
            if (ancestor.Trashed) hasTrashedAncestor = true;
            chain.Add(ancestor);
            parentId = ancestor.Parents[0];
        }
        string relative;
        try
        {
            relative = PilotPath.NormalizeRelative(string.Join('/',
                chain.AsEnumerable().Reverse().Select(item => item.Name ?? string.Empty)
                    .Append(entry.Name ?? string.Empty).Where(segment => segment.Length > 0)));
        }
        catch (ArgumentException)
        {
            return new TrashMembership(null, hasTrashedAncestor,
                "its derived path cannot be represented safely on this machine");
        }
        return new TrashMembership(relative, hasTrashedAncestor, string.Empty);
    }

    private async Task<GoogleDriveInventoryEntry> ReadTrashAncestorAsync(string fileId, TrashReadBudget budget,
        Dictionary<string, GoogleDriveInventoryEntry> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(fileId, out var cached)) return cached;
        budget.ConsumeMetadataRead();
        var ancestor = await GetFileAsync(fileId, cancellationToken);
        cache[fileId] = ancestor;
        return ancestor;
    }

    private string TrashCorpusParameters()
    {
        if (_sharedDriveId is null) return "&corpora=user";
        return "&corpora=drive&driveId=" + Uri.EscapeDataString(_sharedDriveId) +
               "&supportsAllDrives=true&includeItemsFromAllDrives=true";
    }

    private string EncodeTrashCursor(string corpusKind, string corpusId, string pageToken) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = TrashCursorKind,
            ["account"] = _connection.AccountKey,
            ["root"] = _rootFolderId,
            ["corpus"] = corpusKind,
            ["corpusId"] = corpusId,
            ["token"] = RequireOpaqueCursor(pageToken)
        })));

    // Rejects a malformed cursor, and a cursor issued for another account, root folder or corpus, before
    // anything is sent to Google Drive.
    private string ReadTrashCursor(string cursor, bool verifyCorpus = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        if (Encoding.UTF8.GetByteCount(cursor) > MaximumCursorBytes)
            throw new InvalidDataException(
                "The Google Drive trash page cursor is larger than the bounded limit.");
        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            throw new InvalidDataException(
                "The Google Drive trash page cursor is not a cursor this build issued.");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                "The Google Drive trash page cursor is not a cursor this build issued.");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("kind", out var kind) ||
                !string.Equals(kind.GetString(), TrashCursorKind, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The Google Drive trash page cursor is not a cursor this build issued.");
            var account = root.TryGetProperty("account", out var accountElement) ? accountElement.GetString() : null;
            var rootId = root.TryGetProperty("root", out var rootElement) ? rootElement.GetString() : null;
            var corpus = root.TryGetProperty("corpus", out var corpusElement) ? corpusElement.GetString() : null;
            var corpusId = root.TryGetProperty("corpusId", out var corpusIdElement)
                ? corpusIdElement.GetString()
                : null;
            var token = root.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException("The Google Drive trash page cursor has no page token.");
            var expectedCorpus = _sharedDriveId is null ? "user" : "drive";
            var expectedCorpusId = _sharedDriveId ?? _connection.PermissionId;
            if (!string.Equals(account, _connection.AccountKey, StringComparison.Ordinal) ||
                !string.Equals(rootId, _rootFolderId, StringComparison.Ordinal) ||
                (verifyCorpus && (!string.Equals(corpus, expectedCorpus, StringComparison.Ordinal) ||
                !string.Equals(corpusId, expectedCorpusId, StringComparison.Ordinal))))
                throw new InvalidDataException(
                    "This Google Drive trash page cursor was issued for a different account, root folder " +
                    "or corpus. Refresh the trash listing instead of continuing the stored page.");
            return token!;
        }
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            1 => throw new FormatException("A base64url value cannot have a single trailing character."),
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }
}
