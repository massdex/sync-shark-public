namespace Massdex.SyncShark.V2;

// A definite pre-submit refusal, carrying the guard owner's existing result across the provider queue.
internal sealed class DeleteSourceUnconfirmedException(ActionResult result) : Exception(result.Reason)
{
    internal ActionResult Result { get; } = result;
}
