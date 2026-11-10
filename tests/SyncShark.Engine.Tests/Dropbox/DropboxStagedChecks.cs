using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for DropboxStaged (`--engine-checks dropbox`), written by DropboxStaged's writer.</summary>
public static class DropboxStagedChecks
{
    public static void Run()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new[] { byte.MaxValue } })
        {
            using var content = new MemoryStream(bytes);
            content.Position = 0;
            var source = new Entry(nameof(Entry), false, content.Length, DateTimeOffset.UnixEpoch,
                nameof(Entry.Id), nameof(Entry.Revision), nameof(Entry.ContentHash), null);
            var single = DropboxStaged.Single(nameof(DropboxStaged.Single), content, source);
            foreach (var total in new[] { default(long), long.MaxValue })
            {
                var session = DropboxStaged.Session(nameof(DropboxStaged.Session),
                    nameof(DropboxStaged.SessionId), total, source, nameof(DropboxStaged.ContentHash));
                foreach (var staged in new[] { single, session })
                {
                    var isSingle = ReferenceEquals(staged, single);
                    var path = isSingle ? nameof(DropboxStaged.Single) : nameof(DropboxStaged.Session);
                    var position = content.Position;
                    IStaged contract = staged;
                    foreach (var dispose in new[] { false, true, true })
                    {
                        if (dispose)
                            Check.Require(contract.DisposeAsync().IsCompletedSuccessfully,
                                nameof(DropboxStaged.DisposeAsync));

                        Check.Require(contract.Path == path, nameof(DropboxStaged.Path));
                        Check.Require(ReferenceEquals(staged.Source, source), nameof(DropboxStaged.Source));
                        Check.Require(contract.ConsumesSourceOnPublish == isSingle,
                            nameof(IStaged.ConsumesSourceOnPublish));
                        Check.Require(isSingle
                            ? ReferenceEquals(staged.Content, content) && staged.SessionId is null
                                && staged.Total == content.Length
                            : staged.Content is null && staged.SessionId == nameof(DropboxStaged.SessionId)
                                && staged.Total == total, nameof(DropboxStaged));
                        Check.Require(content.CanRead && content.CanSeek && content.Position == position,
                            nameof(DropboxStaged.Content));
                    }
                }
            }
        }
    }
}
