namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Inner roots and the drive-root bin are held out without hiding similarly named siblings.</summary>
public static class PathScopeChecks
{
    public static Task RunAsync()
    {
        const string root = @"T:\";
        var outer = new PairConfig("a", "A", root, "/A", "test", [], [], FirstRunPolicy.NewestWins, true, null);
        var inner = outer with { PairId = "b", LocalRoot = Path.Combine(root, "Inner"), DropboxRoot = "/A/RemoteInner" };
        var scope = new PathScope(outer, [outer, inner], volumeBin: Path.Combine(root, ResourceNames.RootBinFolder));
        Check.Require(scope.Excludes("inner/file", null, false) && scope.Excludes(null, "RemoteInner/file", false), "Nested roots");
        Check.Require(scope.Excludes("Recycle Bin/item", null, false) && !scope.Excludes("Recycle Bin Extra/item", null, false), "Volume bin boundary");
        Check.Require(!scope.Excludes("innermost/file", null, false), "Unrelated sibling");
        return Task.CompletedTask;
    }
}
