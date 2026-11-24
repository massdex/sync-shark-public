using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxPaths.</summary>
public static class DropboxPathsChecks
{
    /// <summary>Verifies the DropboxPaths contract through the module surface.</summary>
    public static Task RunAsync()
    {
        var pair = new PairConfig("pair-a", "Pair A", "", "/", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null);

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
                Check.Require(error.InnerException is null, $"An out-of-root display must be an inventory failure: {item.Display}");
            }
        }

        Check.Require(DropboxPaths.ContainedRelative("/Mixed//Case/Note.txt") == "Mixed/Case/Note.txt",
            "ContainedRelative normalises separators.");
        try
        {
            _ = DropboxPaths.ContainedRelative("..");
            Check.Require(false, "A traversing relative path must be refused.");
        }
        catch (EngineRequestException error)
        {
            Check.Require(error.Key == TextKey.ExclusionPathInvalid, "A refused path names the exclusion-path cause.");
        }
        return Task.CompletedTask;
    }
}
