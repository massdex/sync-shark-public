namespace Massdex.SyncShark.Engine;

/// <summary>Storage certifies that one acquired selection's completion mutation failed with an integrity refusal
/// and its transaction rolled back, so the refusal belongs to that exact item rather than to the whole store.</summary>
public sealed class ItemStoreMutationException : Exception
{
    internal ItemStoreMutationException(WorkSelection selection, int primaryCode, int extendedCode, Exception inner)
        : base(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, selection.Key)), inner)
    {
        Selection = selection;
        PrimaryCode = primaryCode;
        ExtendedCode = extendedCode;
    }

    /// <summary>The exact acquired selection whose completion did not commit.</summary>
    public WorkSelection Selection { get; }

    /// <summary>The SQLite primary result code of the integrity refusal.</summary>
    public int PrimaryCode { get; }

    /// <summary>The SQLite extended result code of the integrity refusal.</summary>
    public int ExtendedCode { get; }
}
