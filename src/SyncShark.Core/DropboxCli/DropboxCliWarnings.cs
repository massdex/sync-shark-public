namespace Massdex.SyncShark;

/// <summary>The non-fatal warnings of one Dropbox command, carried in its result and on the progress channel.</summary>
public sealed class DropboxCliWarnings(Action<string> progress)
{
    private readonly List<string> _messages = new();

    /// <summary>The warnings to embed in a command result, or null when there are none.</summary>
    public IReadOnlyList<string>? Result => _messages.Count == 0 ? null : _messages;

    /// <summary>Records one warning and reports it on the progress channel for humans.</summary>
    public void Add(string message)
    {
        _messages.Add(message);
        progress(message);
    }
}
