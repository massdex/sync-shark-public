namespace Massdex.SyncShark;

/// <summary>The exact stable setup asset selected from GitHub.</summary>
public sealed record SetupRelease(string Version, Uri Download, long Bytes, string Sha256);
