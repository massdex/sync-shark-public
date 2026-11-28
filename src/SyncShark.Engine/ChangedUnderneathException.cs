namespace Massdex.SyncShark.Engine;

/// <summary>A precondition found the item changed or gone → Failure (FailChanged).</summary>
public class ChangedUnderneathException(string path, Exception? inner = null)
    : Exception(Text.Get(TextKey.FailChanged, (TextArg.Path, path)), inner)
{
    /// <summary>The item's relative path that no longer matches the expected version.</summary>
    public string Path { get; } = path;

    /// <summary>The endpoint that observed the failure, if known.</summary>
    public Side? FailedSide { get; set; }

    /// <summary>The complete selected Local content observed before any effect, when available.</summary>
    public Entry? ObservedLocal { get; init; }

    /// <summary>The saved binding that already owns the refused missing parent's counterpart identity (2.1).</summary>
    public SyncedEntry? BoundAt { get; init; }

    /// <summary>True when e, at any depth of its wrapping, says a pair root is missing or replaced: the member's refusal, never an item failure.</summary>
    internal static bool RootLost(PairConfig pair, Exception? e)
    {
        for (; e != null; e = e.InnerException)
            if (e is ChangedUnderneathException moved
                ? PathNames.Same(moved.Path, pair.LocalRoot) || PathNames.Same(moved.Path, pair.DropboxRoot)
                : e is ItemFailedException { Cause: TextKey.RootLocalIdentityChanged or TextKey.LocalRootMissing })
                return true;
        return false;
    }
}
