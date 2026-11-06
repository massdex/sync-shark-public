using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SyncShark.Core.Tests")]
// The UI fixture drives the same in-process CLI operations surface and window CLI actions the product
// uses. This grants that existing assembly access to internals; the production surface stays internal.
[assembly: InternalsVisibleTo("SyncShark.UiFixture")]
// The Engine Dropbox adapter reuses Core's internal shared response-body reader for reply bounds and
// idle body reads. This grants that existing assembly access instead of publishing the reader as
// public API.
[assembly: InternalsVisibleTo("SyncShark.Engine")]
