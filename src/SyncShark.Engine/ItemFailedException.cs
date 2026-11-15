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
    /// <summary>The provider adapter's settlement disposition; Permanent unless its classifier proved otherwise.</summary>
    public FailureDisposition Disposition { get; init; }
    /// <summary>The provider's wait for a temporary failure and the monotonic timestamp of its receipt, if any.</summary>
    public TimeSpan? ProviderWait { get; init; }
    public long? ProviderWaitStarted { get; init; }
    /// <summary>The intended move of an unknown-effect Dropbox move, for its stored Failure subject.</summary>
    public IssueSubject.UnknownMove? Move { get; init; }
}
