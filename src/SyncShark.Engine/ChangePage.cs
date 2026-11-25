namespace Massdex.SyncShark.Engine;

/// <summary>One page with its next source position, also when the page is empty.</summary>
public sealed record ChangePage(IReadOnlyList<Change> Changes, string? Cursor, JournalPosition? Position)
{
    /// <summary>True only for proven lost continuity; Position or Cursor then supplies the verified fresh source base. A transient read failure throws instead.</summary>
    public bool ContinuityLost { get; init; }
    /// <summary>The local journal prefix fully projected into changes; a raw continuation alone is not proof.</summary>
    public JournalPosition? ProjectedThrough { get; init; }
    /// <summary>This acquisition reached its captured finite boundary; live arrivals do not extend it.</summary>
    public bool CaughtUp { get; init; }
}
