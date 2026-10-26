namespace Massdex.SyncShark.V2;

internal sealed partial class RunScope
{
    private HashSet<string> _userIdsA = new(StringComparer.Ordinal);
    private HashSet<string> _userIdsB = new(StringComparer.Ordinal);
    private HashSet<string> _userPaths = new(StringComparer.OrdinalIgnoreCase);

    // Only actual provider deltas enter here, never deferred intake, verification or bulk inventories.
    // Shared paths also prioritize the local proof that is blocking a remote-side change.
    internal void UserChanges(IReadOnlyList<Change> a, IReadOnlyList<Change> b)
    {
        _userIdsA = a.Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
        _userIdsB = b.Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
        _userPaths = a.Concat(b).Select(change => change.Path).Where(path => path.Length != 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool IsUserChange(ItemState item, Side side) =>
        (side == Side.A ? _userIdsA : _userIdsB).Contains(item.ObjectId) ||
        UserPath(item.Path) || UserPath(DropboxNameMapping.MatchPath(item));

    private bool UserPath(string path)
    {
        for (var current = path; current.Length > 0;)
        {
            if (_userPaths.Contains(current)) return true;
            var slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
        return false;
    }
}
