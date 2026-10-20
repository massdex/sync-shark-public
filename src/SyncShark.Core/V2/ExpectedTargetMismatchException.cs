namespace Massdex.SyncShark.V2;

/// <summary>Thrown when an effect's ExpectedTarget does not match the endpoint, or a read source changed since planning.</summary>
public sealed class ExpectedTargetMismatchException : Exception
{
    public ExpectedTargetMismatchException(string message) : base(message)
    {
    }

    public ExpectedTargetMismatchException(string message, Exception? inner) : base(message, inner)
    {
    }
}
