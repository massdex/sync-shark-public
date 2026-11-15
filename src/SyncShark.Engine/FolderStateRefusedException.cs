namespace Massdex.SyncShark.Engine;

/// <summary>A durable folder transition refused stale state without changing it.</summary>
internal sealed class FolderStateRefusedException : InvalidOperationException
{
}
