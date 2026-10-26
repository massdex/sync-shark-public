namespace Massdex.SyncShark.V2;

/// <summary>A definitive structured provider refusal, not evidence that the target changed.</summary>
internal sealed class ProviderWriteBlockedException(string code, string repair, Exception inner)
    : IOException($"Dropbox refused the write ({code}).", inner)
{
    internal string Code { get; } = code;
    internal string Repair { get; } = repair;
}
