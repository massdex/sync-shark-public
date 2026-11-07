namespace Massdex.SyncShark.Engine;

/// <summary>Any other single-item failure → Failure with this cause.</summary>
public sealed class ItemFailedException(string path, TextKey cause, Exception? inner)
    : Exception(Text.Get(cause, (TextArg.Path, path), (TextArg.Message, inner?.Message ?? "")), inner)
{
    /// <summary>The pair-relative path of the failed item.</summary>
    public string Path { get; } = path;
    /// <summary>The reason key used for the raised issue.</summary>
    public TextKey Cause { get; } = cause;
    /// <summary>The exact native item observed by a journal reader, when known.</summary>
    public string? LocalId { get; init; }
}
