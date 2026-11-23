namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Render operations for the edge module.</summary>
public static class Render
{
    /// <summary>Render the PutBack notice with the actual stored failure cause and the usual Retry and Ignore choices. For an in-use failure use the named-holder template only when the stored holder is known.</summary>
    public static string Notice(Notice notice) => throw new NotImplementedException();
    /// <summary>Event operation on the supplied facts.</summary>
    public static string Event(RunEvent value) => throw new NotImplementedException();

    /// <summary>Notice for the selected Render operation.</summary>
    public static string Notice(Notice notice, string pair, IEnumerable<string> choices) => throw new NotImplementedException();

    /// <summary>Append only the permitted CLI choice words supplied by the read projection and command catalog.</summary>
    public static string Notice(Notice notice, string pair, IEnumerable<string> choices, string? account = null) => throw new NotImplementedException();
}
