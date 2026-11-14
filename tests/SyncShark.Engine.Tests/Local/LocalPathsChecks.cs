using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for LocalPaths and the moved PathNames.Parent cases (`--engine-checks local`), written by
/// LocalPaths's writer.</summary>
public static class LocalPathsChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Check.Require(!LocalPaths.RootExists(root), "A missing root is not an existing directory.");
        Directory.CreateDirectory(root);
        try
        {
            Check.Require(LocalPaths.RootExists(root), "An existing directory is a valid root.");
            var file = Path.Combine(root, "Item.txt");
            File.WriteAllText(file, "");
            Check.Require(!LocalPaths.RootExists(file), "An existing file is not a directory root.");
        }
        finally
        {
            Directory.Delete(root, true);
        }

        var fullCases = new (string Relative, string Expected)[]
        {
            ("Item.txt", Path.Combine(root, "Item.txt")),
            ("Parent/Child/Item.txt", Path.Combine(root, "Parent", "Child", "Item.txt")),
            (@"Parent\Item.txt", Path.Combine(root, "Parent", "Item.txt")),
            ("Parent//Item.txt", Path.Combine(root, "Parent", "Item.txt"))
        };
        foreach (var (relative, expected) in fullCases)
        {
            Check.Require(LocalPaths.Full(root, relative) == expected,
                $"Full resolves {relative} within the root.");
            Check.Require(LocalPaths.Full(root + Path.DirectorySeparatorChar, relative) == expected,
                $"Full accepts a trailing root separator for {relative}.");
        }

        foreach (var relative in new[]
        {
            "", "..", "../Outside.txt", @"Parent\..\Outside.txt", "Parent/./Item.txt",
            "Parent/CON.txt", "Parent/Item.txt:stream", "Parent/Item. ",
            Path.Combine(root, "Absolute.txt")
        })
        {
            var rejected = false;
            try
            {
                LocalPaths.Full(root, relative);
            }
            catch (ItemFailedException error)
            {
                rejected = error.Path == relative && error.Cause == TextKey.LocalNameInvalid
                    && error.InnerException is ArgumentException;
                Check.Require(!error.Message.Contains('{') && error.InnerException is { } inner &&
                    error.Message.Contains(inner.Message),
                    "Invalid local paths retain the original error without template placeholders.");
            }
            Check.Require(rejected, $"Full rejects an unsafe relative path: {relative}.");
        }

        var parentCases = new (string Relative, string Expected)[]
        {
            ("", ""),
            ("Item.txt", ""),
            ("Parent", ""),
            ("Parent/Item.txt", "Parent"),
            ("Parent/Child/Item.txt", "Parent/Child"),
            ("Mixed Case/Child", "Mixed Case")
        };
        foreach (var (relative, expected) in parentCases)
            Check.Require(PathNames.Parent(relative) == expected,
                $"PathNames.Parent preserves the exact parent spelling of {relative}.");
    }
}
