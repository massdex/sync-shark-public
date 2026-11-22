namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The 409 mapping of section 5.4, by error_summary prefix.</summary>
public sealed class DropboxErrors
{
    /// <summary>Classify operation on the supplied facts.</summary>
    public FailureDisposition Classify(System.Net.HttpStatusCode status, System.Text.Json.JsonElement error) => throw new NotImplementedException();

    /// <summary>Classify for the selected DropboxErrors operation.</summary>
    public Classified Classify(System.Net.HttpStatusCode status, System.Text.Json.JsonElement body, string route, bool expectedIsNull, RequestEvidence evidence) => throw new NotImplementedException();
    /// <summary>ClassifyMember for the selected DropboxErrors operation.</summary>
    public Classified ClassifyMember(System.Text.Json.JsonElement failure, string route, bool expectedIsNull) => throw new NotImplementedException();
    /// <summary>Facts known about the attempted request.</summary>
    public readonly record struct RequestEvidence(bool Read, bool Dispatched, bool ProvenNoEffect, bool Transient);
    /// <summary>Cause and effect certainty for a provider refusal.</summary>
    public readonly record struct Classified(
        TextKey Cause, FailureDisposition Disposition, bool ProvenNoEffect, bool Absent = false);
}
