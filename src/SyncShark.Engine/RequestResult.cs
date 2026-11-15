namespace Massdex.SyncShark.Engine;

/// <summary>The finite result of one provider request attempt; a nonserialized workflow fact, never part of a public serialized result.</summary>
public enum RequestResult { Success, CallerStopped, PermanentFailure, TransientFailure, UnknownWrite }
