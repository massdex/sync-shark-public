namespace Massdex.SyncShark.V2;

/// <summary>Thrown when a write may or may not have been applied; never retried blindly, resolved by observe.</summary>
public sealed class UncertainEffectException : Exception
{
    public UncertainEffectException(string message) : base(message)
    {
    }

    public UncertainEffectException(string message, Exception? inner) : base(message, inner)
    {
    }
}
