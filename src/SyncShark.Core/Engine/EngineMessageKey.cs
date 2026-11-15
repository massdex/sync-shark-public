namespace Massdex.SyncShark;

/// <summary>Stable keys of the engine catalog's messages; engine-cli.json holds their text (engine design 1.3).</summary>
public enum EngineMessageKey { NeedsEngine, OldCommand, MissingOption, BadValue, BadNumber, CatalogMismatch, ResolveOptionsInvalid, KeepThisTimeLabel, KeepFolderAlwaysLabel, RecoveryCheckLabel, RecoveryAcceptCurrentLabel, RecoveryAbandonIncomingLabel }
