namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Owns a download response and forwards source failures to its existing staging attempt.</summary>
public sealed class ResponseStream
{
    /// <summary>Open operation on the supplied facts.</summary>
    public Stream Open(HttpResponseMessage response, Stream content) => throw new NotImplementedException();

    /// <summary>Open for the selected ResponseStream operation.</summary>
    public Stream Open(HttpResponseMessage response, Stream content, CancellationToken callerToken, Action<BodyOutcome>? ended) => throw new NotImplementedException();
    /// <summary>Classifies bodyoutcome for the selected operation.</summary>
    public enum BodyOutcome { Completed, Cancelled, Failed }
}
