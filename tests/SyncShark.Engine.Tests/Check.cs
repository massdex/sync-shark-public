namespace Massdex.SyncShark.Engine.Tests;

/// <summary>A named check and the assertion used by the runner.</summary>
public sealed record Check(string Name, Func<Task> Run)
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
