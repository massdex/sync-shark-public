namespace Massdex.SyncShark.V2;

/// <summary>The requested provider effect was not applied; its retained intent is safe to retry.</summary>
public sealed class ProviderEffectBlockedException(string message) : Exception(message) { }
