namespace Massdex.SyncShark.V2;

/// <summary>Connects one provider operation to the Executor's durable intent without provider-owned database access.</summary>
public sealed class ProviderEffectContext
{
    /// <summary>The current action; batch entries capture this before leaving the staging worker.</summary>
    public static AsyncLocal<ProviderEffectContext?> Current { get; } = new();
    private readonly Func<ProviderEffectCheckpoint, Task> _record;

    /// <summary>A free-standing context (tests, tools) with its own recorder and no owning committer.</summary>
    internal ProviderEffectContext(Func<ProviderEffectCheckpoint, Task> record) => _record = record;

    /// <summary>A context owned by the Executor's committer, carrying its action so a batch lane can group many
    /// entries' checkpoints into one durable write.</summary>
    internal ProviderEffectContext(SyncAction action, StoreCommitter owner, Func<ProviderEffectCheckpoint, Task> record)
    {
        Action = action;
        Owner = owner;
        _record = record;
    }

    /// <summary>The action this context records evidence for, or null for a free-standing context.</summary>
    internal SyncAction? Action { get; }

    /// <summary>The committer that owns this context's writes, or null for a free-standing context.</summary>
    internal StoreCommitter? Owner { get; }

    /// <summary>Completes only after the session/job evidence is durable; an effect must not outrun this task.</summary>
    public Task RecordAsync(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _record(new ProviderEffectCheckpoint(kind, id, DateTimeOffset.UtcNow));
    }
}
