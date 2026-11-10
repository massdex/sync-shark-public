using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SyncShark.Core.Tests")]
// The running-application bridge fixture must be able to send deliberately hostile window messages
// (wrong identity, malformed, oversize, replayed) through the real internal transport. This grants the
// existing UI fixture access to internals; the production transport surface stays internal and its
// validation is unchanged.
[assembly: InternalsVisibleTo("SyncShark.UiFixture")]
// The Engine Dropbox adapter reuses Core's internal shared response-body reader for reply bounds and
// idle body reads. This grants that existing assembly access instead of publishing the reader as
// public API.
[assembly: InternalsVisibleTo("SyncShark.Engine")]
