using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for DropboxPaths (`--engine-checks dropbox`), written by DropboxPaths's writer.</summary>
public static class DropboxPathsChecks
{
    public static void Run()
    {
        var pair = new PairConfig("pair-a", "Pair A", "", "/", "connection-a",
            Array.Empty<string>(), Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null);

        var remoteCases = new (string Root, string Relative, string Remote)[]
        {
            ("/", "", ""),
            ("/", "Child", "/Child"),
            ("/", "Child/Note.txt", "/Child/Note.txt"),
            ("/Parent", "", "/Parent"),
            ("/Parent", "Child/Note.txt", "/Parent/Child/Note.txt"),
            ("/Parent/", "Child", "/Parent/Child"),
            ("/Parent///", "Child", "/Parent/Child"),
            ("/Parent/Nested", "Child", "/Parent/Nested/Child"),
            ("/Parent", "Mixed Case/é:name.txt", "/Parent/Mixed Case/é:name.txt")
        };
        foreach (var item in remoteCases)
        {
            var configured = pair with { DropboxRoot = item.Root };
            Check.Require(DropboxPaths.Remote(configured, item.Relative) == item.Remote,
                $"Remote maps {item.Root} and {item.Relative} exactly.");
            Check.Require(DropboxPaths.Relative(configured, item.Remote) == item.Relative,
                $"Relative reverses Remote for {item.Root} and {item.Relative}.");
        }

        var relativeCases = new (string Root, string Display, string Relative)[]
        {
            ("/", "", ""),
            ("/", "/", ""),
            ("/", "/Mixed Case/é:name.txt", "Mixed Case/é:name.txt"),
            ("/Parent", "/Parent", ""),
            ("/Parent///", "/Parent", ""),
            ("/Parent/", "/parent", ""),
            ("/Parent", "/parent/Child/MiXeD.txt", "Child/MiXeD.txt"),
            ("/Parent/Nested", "/parent/nested/Child", "Child"),
            ("/Parent", "/Parent/Parent/Note.txt", "Parent/Note.txt"),
            ("/Parent", "/Parent/", "")
        };
        foreach (var item in relativeCases)
        {
            Check.Require(DropboxPaths.Relative(pair with { DropboxRoot = item.Root }, item.Display) == item.Relative,
                $"Relative preserves path_display spelling below {item.Root}: {item.Display}.");
        }

        var outside = new (string Root, string Display)[]
        {
            ("/Parent", "/Other"),
            ("/Parent", "/Parentish"),
            ("/Parent", "/Parentish/Child"),
            ("/Parent/Nested", "/Parent/Other")
        };
        foreach (var item in outside)
        {
            try
            {
                _ = DropboxPaths.Relative(pair with { DropboxRoot = item.Root }, item.Display);
                Check.Require(false, $"A display outside the pair root must fail: {item.Display}");
            }
            catch (InventoryIncompleteException error)
            {
                Check.Require(error.InnerException is null && error.Message == Text.Get(TextKey.ReadDropboxFailed,
                    (TextArg.Path, item.Display), (TextArg.Message, "")),
                    $"An out-of-root display is an inventory failure naming the display path: {item.Display}");
            }
        }

        AllocationProbe(pair with { DropboxRoot = "/Parent" });
    }

    private static void AllocationProbe(PairConfig pair)
    {
        const int calls = 1000;
        const string display = "/Parent/Child/Note.txt";
        Action<int> baseline = index => Sink(BaselineRelative(pair, display));
        Action<int> candidate = index => Sink(DropboxPaths.Relative(pair, display));

        var relativeBaseline = AllocProbe.Measure(baseline, calls);
        var relativeCandidate = AllocProbe.Measure(candidate, calls);

        foreach (var (root, relative) in new[] { ("/", ""), ("/Parent", "Child"), ("/Parent///", "Child") })
        {
            var configured = pair with { DropboxRoot = root };
            Action<int> remoteBaseline = index => Sink(BaselineRemote(configured, relative));
            Action<int> remoteCandidate = index => Sink(DropboxPaths.Remote(configured, relative));
            var baselineBytes = AllocProbe.Measure(remoteBaseline, calls);
            var candidateBytes = AllocProbe.Measure(remoteCandidate, calls);
            Check.Require(baselineBytes == candidateBytes,
                $"Remote allocates the same bytes per call as its inline trim/empty-root/concat code: root {root}, relative {relative}.");
        }

        Check.Require(relativeBaseline == relativeCandidate,
            "Relative allocates the same bytes per call as its inline root/equality/boundary/slice code.");
    }

    // The exact pre-migration expression of DropboxPaths.Remote, kept only as the per-item allocation baseline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string BaselineRemote(PairConfig pair, string relative)
    {
        var root = pair.DropboxRoot.TrimEnd(PathNames.PathSeparator[0]);
        return relative.Length == 0 ? root : root + PathNames.PathSeparator + relative;
    }

    // The exact pre-migration expression of DropboxPaths.Relative, kept only as the per-item allocation baseline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string BaselineRelative(PairConfig pair, string pathDisplay)
    {
        var root = pair.DropboxRoot.TrimEnd(PathNames.PathSeparator[0]);
        if (string.Equals(pathDisplay, root, StringComparison.OrdinalIgnoreCase)) return "";
        if (pathDisplay.Length > root.Length && pathDisplay[root.Length] == PathNames.PathSeparator[0] &&
            pathDisplay.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return pathDisplay[(root.Length + 1)..];
        throw new InventoryIncompleteException(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, pathDisplay),
            (TextArg.Message, "")), null);
    }

    // A typed, non-inlined sink keeps the probed call's result live without boxing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(string? value) => _string = value;

    private static string? _string;
}
