namespace Massdex.SyncShark.V2;

/// <summary>The formats the cloud provider itself said it supports for one document. An empty list means the
/// provider reported no export_info at all, so no export button is offered; null means an older notice whose
/// formats were never captured.</summary>
public sealed record NoticeExportInfo(string? DefaultFormat, IReadOnlyList<string> Formats);
