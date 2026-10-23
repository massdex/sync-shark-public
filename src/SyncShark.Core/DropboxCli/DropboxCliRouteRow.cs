namespace Massdex.SyncShark;

/// <summary>One row of the user-token route table, with an optional grant check.</summary>
public sealed record DropboxCliRouteRow(string Name, string Style, string Host, string Policy, string Scope,
    bool? Granted, bool Deprecated, bool Preview, string? Special) : IDropboxCliData;
