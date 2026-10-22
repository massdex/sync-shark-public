using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>Projects stored recovery rows onto the window's catalog records.</summary>
internal static class RecoveryMapping
{
    private const string RetainedHealth = "retained";
    private const string EmptyIdentityJson = "{}";

    /// <summary>One page of a pair's recovery catalog, filtered by search and ordered newest first.</summary>
    internal static SyncRecoveryCatalogPage CatalogPage(PairDef pair, IReadOnlyList<RecoveryRecord> records,
        int archiveOffset, int pageSize, string? search)
    {
        var matched = records
            .Where(record => search is null || record.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.Created)
            .ToArray();
        var items = matched.Skip(archiveOffset).Take(pageSize).Select(record => Item(record, pair)).ToArray();
        return new SyncRecoveryCatalogPage(items, archiveOffset + items.Length,
            HasMoreArchives: matched.Length > archiveOffset + items.Length, DeletedCursor: null,
            HasMoreDeleted: false);
    }

    /// <summary>The recovery usage summary for a pair.</summary>
    internal static SyncRecoveryUsageSummary Usage(string pairId, IReadOnlyList<RecoveryRecord> records,
        DateTimeOffset now, V2Settings settings) => new(pairId, records.Count, records.Sum(record => record.Size),
        records.Count == 0 ? null : records.Min(record => record.Created),
        records.Count(record => record.Expires <= now), (int)settings.RecoveryRetention.TotalDays);

    private static SyncRecoveryCatalogItem Item(RecoveryRecord record, PairDef pair)
    {
        var root = record.Side == Side.A ? pair.A : pair.B;
        return new SyncRecoveryCatalogItem(
            Key: record.RecoveryId,
            PairId: record.PairId,
            PairName: pair.Name,
            Source: string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase)
                ? SyncRecoverySource.LocalArchive
                : SyncRecoverySource.DropboxArchive,
            RelativePath: record.Path,
            Connection: SyncProviders.DisplayName(root.Provider),
            Location: record.Location,
            Size: record.Size,
            RetainedUtc: record.Created,
            Retention: record.Expires.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
            Health: RetainedHealth,
            ContentHash: record.Hash,
            Identity: null,
            Revision: record.VersionId,
            IdentityJson: EmptyIdentityJson,
            CanRestore: !string.IsNullOrEmpty(record.Hash) &&
                (string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(root.Provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase)),
            Provider: root.Provider,
            LogicalSide: record.Side.ToString());
    }
}
