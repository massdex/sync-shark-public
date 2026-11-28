namespace Massdex.SyncShark.Engine;

/// <summary>Any other single-item failure → Failure with this cause.</summary>
public sealed class ItemFailedException(string path, TextKey cause, Exception? inner)
    : Exception(Text.Get(cause, (TextArg.Path, path), (TextArg.Message, inner?.Message ?? "")), inner)
{
    internal static void RequireReadable(Side side,Entry? entry)
    {
        if(entry is {Skip:nameof(TextKey.SkipUnreadable)}) throw new ItemFailedException(entry.Path,side==Side.Local?TextKey.ReadLocalFailed:TextKey.ReadDropboxFailed,null);
    }

    /// <summary>The refusal as safe facts only: Dropbox's route, HTTP status and error tag, or a Windows HRESULT; never a message or a path.</summary>
    public string? Provider { get; init; }

    /// <summary>The pair-relative path of the failed item.</summary>
    public string Path { get; } = path;
    /// <summary>The reason key used for the raised issue.</summary>
    public TextKey Cause { get; } = cause;
    /// <summary>The program observed holding this local file, when available.</summary>
    public string? Holder { get; init; }
    /// <summary>The exact native item observed by a journal reader, when known.</summary>
    public string? LocalId { get; init; }
    /// <summary>The provider adapter's settlement disposition; Permanent unless its classifier proved otherwise.</summary>
    public FailureDisposition Disposition { get; init; }
    /// <summary>The provider's wait for a temporary failure and the monotonic timestamp of its receipt, if any.</summary>
    public TimeSpan? ProviderWait { get; init; }
    public long? ProviderWaitStarted { get; init; }
    /// <summary>Dropbox's stated wait left at <paramref name="now"/> (default: the clock's current time) on the receipt's clock, or null when
    /// it stated none (absent or zero); an elapsed positive wait is zero, not unstated.</summary>
    internal TimeSpan? WaitLeft(TimeProvider clock,long? now=null) => ProviderWait is {} wait && wait>TimeSpan.Zero
        ? TimeSpan.FromTicks(Math.Max(TimeSpan.Zero.Ticks,(ProviderWaitStarted is {} start ? wait-clock.GetElapsedTime(start,now??clock.GetTimestamp()) : wait).Ticks)) : null;
    // Of two no-effect refusals in one replay unit keep the one whose stated wait ends last, both read at ONE clock sample (an unstated
    // wait never displaces a stated one; ties keep the first). A null refusal is ignored.
    internal static ItemFailedException? Later(ItemFailedException? kept,ItemFailedException? next,TimeProvider clock)
    {
        if(kept==null || next==null) return kept??next;
        var now=clock.GetTimestamp();
        return (next.WaitLeft(clock,now)??TimeSpan.MinValue)>(kept.WaitLeft(clock,now)??TimeSpan.MinValue) ? next : kept;
    }
    /// <summary>The intended move of an unknown-effect Dropbox move, for its stored Failure subject.</summary>
    public IssueSubject.UnknownMove? Move { get; init; }
}
