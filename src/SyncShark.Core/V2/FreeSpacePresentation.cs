using System.Globalization;

namespace Massdex.SyncShark.V2;

public static class FreeSpacePresentation
{
    /// <summary>Only structured notices group; legacy text stays visible, without guessing volumes from prose.</summary>
    public static IReadOnlyList<FreeSpaceNoticeSummary> Group(IEnumerable<Notice> notices) => notices
        .Where(notice => notice.FreeSpace is not null)
        .GroupBy(notice => Normalize(notice.FreeSpace!.VolumeRoot), StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var members = group.OrderBy(notice => notice.PairId, StringComparer.Ordinal)
                .ThenBy(notice => notice.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(notice => notice.NoticeId, StringComparer.Ordinal).ToArray();
            long required = 0;
            var paths = members.GroupBy(notice => (notice.PairId, Path: notice.Path.ToUpperInvariant())).ToArray();
            foreach (var path in paths)
            {
                var bytes = Math.Max(0, path.Max(notice => notice.FreeSpace!.RequiredBytes));
                required = bytes > long.MaxValue - required ? long.MaxValue : required + bytes;
            }
            // Conservative observed range, not a promise of currently available space.
            var free = members.Select(notice => notice.FreeSpace!.FreeBytes).ToArray();
            var reserve = members.Select(notice => notice.FreeSpace!.ReserveBytes).ToArray();
            return new FreeSpaceNoticeSummary(group.Key, paths.Length, required,
                free.Any(value => value is null) ? null : free.Min(),
                reserve.Any(value => value is null) ? null : reserve.Max(),
                members.Select(notice => SingleLine(notice.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3).ToArray(), members);
        }).ToArray();

    private static string Normalize(string? root) => string.IsNullOrWhiteSpace(root) ? "Unknown volume" :
        root.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    private static string SingleLine(string path)
    {
        var text = path.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 120 ? text[..117] + "..." : text;
    }
}
