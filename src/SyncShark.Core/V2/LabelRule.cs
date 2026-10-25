namespace Massdex.SyncShark.V2;

/// <summary>How a keep-both copy is labelled: format tokens {name}, {side}, {time}, {ext}; side display names.</summary>
public sealed record LabelRule(string Format, string TimeFormat, TimeSpan UtcOffset, string NameA, string NameB);
