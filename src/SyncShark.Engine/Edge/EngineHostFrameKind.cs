namespace Massdex.SyncShark.Engine.Edge;

/// <summary>The frame kinds of the one-command engine host pipe (8.8): one Arguments request, one
/// Accepted acknowledgment, streamed standard output and error, and one Exit.</summary>
public enum EngineHostFrameKind : byte
{
    Arguments, Accepted, StandardOutput, StandardError, Exit, SubscribeStatus, Status
}
