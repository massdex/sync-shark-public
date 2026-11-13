namespace Massdex.SyncShark.Engine;

/// <summary>Admission of Dropbox-touching file actions (D33); implemented by the Dropbox limiter.</summary>
public interface IAdmission
{
    Task<IAsyncDisposable> AdmitAsync(bool priority, CancellationToken ct);
    void ActionCompleted(bool success);
    /// <summary>The current level; Repair saves it at the end of a run (D33 start level).</summary>
    int Level { get; }
    /// <summary>Enters one outer Dropbox-touching operation's place in the account work window, held until recording and
    /// hold release. <paramref name="singleFile"/> describes the whole operation and is never true for a composite.</summary>
    Task<IAsyncDisposable> EnterWorkAsync(bool singleFile, CancellationToken ct);
    /// <summary>Revokes direct-upload eligibility for the caller's current work scope. Idempotent and monotonic; it never
    /// grants eligibility.</summary>
    void DisallowDirectUpload();
    /// <summary>Admits one pure upload's byte consumption in the account upload pool, released after staging or a
    /// validated direct publication has closed the source.</summary>
    Task<IAsyncDisposable> AdmitUploadAsync((string AccountId, string RouteName, string SourceIdentity) identity, CancellationToken ct);
    /// <summary>Reports one successful pure-upload byte completion; growth is capped at <see cref="WorkCapacity"/> and
    /// never changes the ordinary level.</summary>
    void UploadStaged();
    /// <summary>The account-wide outstanding-work ceiling W = checked(2 * BatchMaxEntries), derived once from the
    /// provider fact for producer bookkeeping.</summary>
    int WorkCapacity { get; }
}
