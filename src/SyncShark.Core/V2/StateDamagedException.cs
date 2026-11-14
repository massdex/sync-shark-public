namespace Massdex.SyncShark.V2;

/// <summary>Raised when the v2 state database is missing, corrupt or of an unknown schema version.</summary>
/// <remarks>System.IO.InvalidDataException is sealed, so this derives from <see cref="Exception"/> instead.</remarks>
public sealed class StateDamagedException : Exception
{
    /// <summary>Creates the exception with a message describing the damage.</summary>
    public StateDamagedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying SQLite failure.</summary>
    public StateDamagedException(string message, Exception inner) : base(message, inner)
    {
    }
}
