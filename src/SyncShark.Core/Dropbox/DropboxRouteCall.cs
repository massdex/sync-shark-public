namespace Massdex.SyncShark;

/// <summary>One call of a Dropbox route: the route, its JSON argument and optional content streams.</summary>
public sealed record DropboxRouteCall(
    DropboxRoute Route,
    string ArgumentJson,
    string? PathRootNamespaceId = null,
    Stream? ContentIn = null,
    Stream? ContentOut = null);
