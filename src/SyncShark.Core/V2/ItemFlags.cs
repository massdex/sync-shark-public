namespace Massdex.SyncShark.V2;

/// <summary>Bit flags describing special states of an item.</summary>
/// <remarks>Placeholder: an online-only cloud-files stub (decision 57). Link: a junction, symlink or mount point that is
/// never followed; it and its path on both sides are excluded from the pair (decision 59).</remarks>
[Flags] public enum ItemFlags { None = 0, Placeholder = 1, Locked = 2, Unreadable = 4, Ignored = 8, Link = 16, CarriedIgnore = 32, ProviderNameRefused = 64, Quarantined = 128, NeedsRehash = 256, RequiresExport = 512, OwnedPointer = 1024 }
