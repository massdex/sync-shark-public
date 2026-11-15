namespace Massdex.SyncShark.Engine;

/// <summary>A committed effect, as counted in RepairSummary.Effects (section 6.1).</summary>
public enum EffectKind { Uploaded, Downloaded, FolderCreated, DeletedLocal, DeletedDropbox, MovedLocal,
    MovedDropbox, Recorded, Forgotten, KeptBoth, Replaced }
