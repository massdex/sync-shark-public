namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The closed reply shapes consumed by the Dropbox adapter, not an extensible decoder registry.</summary>
public static class DropboxReplyShapes
{
    /// <summary>Job operation on the supplied facts.</summary>
    public static JobReply Job(System.Text.Json.JsonElement response, AcceptedJob job) => throw new NotImplementedException();

    /// <summary>Page for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<DropboxPage> Page(PairConfig pair) => throw new NotImplementedException();
    /// <summary>Listed for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<Listed> Listed(PairConfig pair) => throw new NotImplementedException();
    /// <summary>Metadata for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<Entry> Metadata(PairConfig pair, string? untaggedKind = null, bool nested = false) => throw new NotImplementedException();
    /// <summary>SessionId for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<string> SessionId() => throw new NotImplementedException();
    /// <summary>Void for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<ValueTuple> Void() => throw new NotImplementedException();
    /// <summary>Cursor for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<string> Cursor() => throw new NotImplementedException();
    /// <summary>Longpoll for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<(bool Changes, TimeSpan? Backoff)> Longpoll() => throw new NotImplementedException();
    /// <summary>AccountRoot for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<(string AccountId, string RootNamespaceId, string HomeNamespaceId)> AccountRoot() => throw new NotImplementedException();
    /// <summary>HistoryHead for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<(bool IsDeleted, bool HasHead, string? ServerDeleted, bool TimeWrongType, string? Id, string? Revision, string? NativePath, string? ContentHash, bool HashWrongType)> HistoryHead() => throw new NotImplementedException();
    /// <summary>Submit for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<(AcceptedJob? Accepted, JobReply Reply)> Submit(PairConfig pair, string route, IReadOnlyList<JobMember> launch) => throw new NotImplementedException();
    /// <summary>Submit for the selected DropboxReplyShapes operation.</summary>
    public static (AcceptedJob? Accepted, JobReply Reply) Submit(System.Text.Json.JsonElement root, PairConfig pair, string route, IReadOnlyList<JobMember> launch) => throw new NotImplementedException();
    /// <summary>Job for the selected DropboxReplyShapes operation.</summary>
    public static DropboxReply<JobReply> Job(PairConfig pair, AcceptedJob job) => throw new NotImplementedException();
    /// <summary>Job for the selected DropboxReplyShapes operation.</summary>
    public static JobReply Job(System.Text.Json.JsonElement response, AcceptedJob job, PairConfig? pair) => throw new NotImplementedException();
}
