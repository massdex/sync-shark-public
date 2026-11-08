using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for DropboxEntries (`--engine-checks dropbox`), written by DropboxEntries's writer.</summary>
public static class DropboxEntriesChecks
{
    private const int SingleByteChunkSize = 1;

    public static void Run()
    {
        var pair = new PairConfig("pair", "Pair A", "", "/Parent", "connection",
            Array.Empty<string>(), Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null);
        ResponseShapes(pair);
        IncrementalProjection(pair);
        ReplyTokenValidation();
        UploadResults();
        Helpers(pair);
        AllocationProbe(pair);
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        const string path = "Child/Mixed É.txt";
        const long size = 4294967296;
        var cases = new (string Tag, bool? Downloadable)[]
        {
            (DropboxProtocol.TagDeleted, null),
            (DropboxProtocol.TagFolder, null),
            (DropboxProtocol.TagFile, false),
            (DropboxProtocol.TagFile, true),
            (DropboxProtocol.TagFile, null)
        };

        foreach (var root in new[] { "/Parent", "/" })
        {
            var currentPair = pair with { DropboxRoot = root };
            foreach (var item in cases)
            {
                var fields = new Dictionary<string, object?>
                {
                    [DropboxProtocol.Tag] = item.Tag,
                    [DropboxProtocol.PathDisplay] = root.TrimEnd('/') + "/" + path
                };
                Entry? expected = null;
                if (item.Tag != DropboxProtocol.TagDeleted)
                {
                    fields[DropboxProtocol.Id] = "id:item";
                    var folder = item.Tag == DropboxProtocol.TagFolder;
                    if (!folder)
                    {
                        fields[DropboxProtocol.Size] = size;
                        fields[DropboxProtocol.ClientModified] = modified;
                        fields[DropboxProtocol.Rev] = "revision";
                        fields[DropboxProtocol.ContentHash] = "content-hash";
                        if (item.Downloadable.HasValue)
                            fields[DropboxProtocol.IsDownloadable] = item.Downloadable.Value;
                    }
                    expected = new Entry(path, folder, folder ? 0 : size,
                        folder ? default : modified, "id:item", folder ? "" : "revision",
                        folder ? "" : "content-hash",
                        item.Downloadable == false ? nameof(TextKey.SkipNotDownloadable) : null);
                }

                // An invalid server timestamp must never affect parsing.
                fields["server_modified"] = "not-a-timestamp";
                var metadata = JsonSerializer.SerializeToElement(fields);
                var actual = DropboxEntries.Parse(metadata, currentPair);
                Check.Require(actual == new Listed(path, expected),
                    $"Metadata fields, relative spelling and skip state: {root}, {item}");

                var response = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    [DropboxProtocol.Metadata] = metadata
                });
                Check.Require(DropboxEntries.Parse(response.GetProperty(DropboxProtocol.Metadata), currentPair) == actual,
                    "Metadata extracted from a writer response uses the same parser.");
            }
        }

        var rootFolder = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.TagFolder,
            [DropboxProtocol.PathDisplay] = pair.DropboxRoot,
            [DropboxProtocol.Id] = "id:root",
            [DropboxProtocol.Size] = "not-a-size",
            [DropboxProtocol.ClientModified] = "not-a-timestamp",
            [DropboxProtocol.IsDownloadable] = false
        });
        Check.Require(DropboxEntries.Parse(rootFolder, pair) == new Listed("",
            new Entry("", true, 0, default, "id:root", "", "", null)),
            "A root folder has an empty relative path and ignores file-only metadata.");

        var validFile = new Dictionary<string, object?>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.TagFile,
            [DropboxProtocol.PathDisplay] = "/Parent/Child.txt",
            [DropboxProtocol.Id] = "id:file",
            [DropboxProtocol.Size] = size,
            [DropboxProtocol.ClientModified] = modified,
            [DropboxProtocol.Rev] = "revision",
            [DropboxProtocol.ContentHash] = "content-hash"
        };
        var invalidFields = new (string Field, object? Value)[]
        {
            (DropboxProtocol.Tag, "unknown"),
            (DropboxProtocol.PathDisplay, null),
            (DropboxProtocol.Id, null),
            (DropboxProtocol.Size, "not-a-size"),
            (DropboxProtocol.ClientModified, "not-a-timestamp"),
            (DropboxProtocol.Rev, null),
            (DropboxProtocol.ContentHash, null),
            (DropboxProtocol.IsDownloadable, "false")
        };
        foreach (var invalid in invalidFields)
        {
            var fields = new Dictionary<string, object?>(validFile) { [invalid.Field] = invalid.Value };
            Check.Require(Rejects(JsonSerializer.SerializeToElement(fields), pair),
                $"Invalid metadata must not become a usable entry: {invalid.Field}");
        }

        foreach (var downloadable in new[] { false, true })
        {
            var withoutHash = new Dictionary<string, object?>(validFile)
            {
                [DropboxProtocol.IsDownloadable] = downloadable
            };
            withoutHash.Remove(DropboxProtocol.ContentHash);
            var entry = DropboxEntries.Parse(JsonSerializer.SerializeToElement(withoutHash), pair).Entry;
            Check.Require(entry is { ContentHash: "" } &&
                entry.Skip == (downloadable ? null : nameof(TextKey.SkipNotDownloadable)),
                "Missing optional content_hash is empty without losing the non-downloadable skip.");
        }

        foreach (var pathName in new[] { "CON", "con.txt", "Folder/AUX", "NUL/Child", "a:b.txt", "CON:note",
            "COM10", "a_b.txt" })
        foreach (var folder in new[] { false, true })
        {
            var fields = new Dictionary<string, object?>(validFile)
            {
                [DropboxProtocol.PathDisplay] = "/Parent/" + pathName,
                [DropboxProtocol.Tag] = folder ? DropboxProtocol.TagFolder : DropboxProtocol.TagFile,
                [DropboxProtocol.IsDownloadable] = false
            };
            // Option C validates the raw Dropbox spelling: every name here is Windows-invalid except the
            // high-numbered device name and the literal underscore, and no mapped substitution rescues them.
            var reserved = pathName is not ("COM10" or "a_b.txt");
            var entry = DropboxEntries.Parse(JsonSerializer.SerializeToElement(fields), pair).Entry;
            Check.Require(entry?.Skip == (reserved ? nameof(TextKey.SkipReservedName)
                : folder ? null : nameof(TextKey.SkipNotDownloadable)),
                "The shared parser owns reserved-name skips on the raw Dropbox spelling, ahead of non-downloadable skips.");
            fields[DropboxProtocol.Tag] = DropboxProtocol.TagDeleted;
            Check.Require(DropboxEntries.Parse(JsonSerializer.SerializeToElement(fields), pair).Entry is null,
                "Reserved-name tombstones stay tombstones.");
        }

        validFile.Remove(DropboxProtocol.ClientModified);
        validFile["server_modified"] = modified;
        Check.Require(Rejects(JsonSerializer.SerializeToElement(validFile), pair),
            "A server timestamp cannot substitute for missing client_modified.");
    }

    // The incremental selected-field projection (READING 8.3): one item at a time, winning values only, then the
    // existing Parse/RequireLive semantics. Nothing reaches Parse until the item's closing brace is consumed.
    private static void IncrementalProjection(PairConfig pair)
    {
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var expected = new Entry("Child É.txt", false, 1, modified, "id:file", "revision", "", null);
        var emoji = new Entry("Child " + char.ConvertFromUtf32(0x1F600) + ".txt", false, 1, modified,
            "id:file", "revision", "", null);
        const string file = "{\".tag\":\"file\",\"path_display\":\"/Parent/Child \\u00C9.txt\",\"id\":\"id:file\"," +
            "\"size\":1,\"client_modified\":\"2026-01-02T03:04:05Z\",\"rev\":\"revision\"}";

        // Byte cuts at every boundary: a JSON \u escape, a literal multi-byte UTF-8 sequence and a surrogate pair.
        foreach (var chunk in new[] { 1, 2, 3, 5, 64 })
        {
            using (var projected = Project(file, chunk))
                Check.Require(DropboxEntries.Parse(projected.RootElement, pair).Entry == expected,
                    $"An item projects identically when split into {chunk}-byte chunks, including a JSON escape.");
            using (var projected = Project(FileText("\"/Parent/Child É.txt\""), chunk))
                Check.Require(DropboxEntries.Parse(projected.RootElement, pair).Entry == expected,
                    $"An item projects identically when split into {chunk}-byte chunks, including literal multi-byte UTF-8.");
        }
        using (var projected = Project(FileText("\"/Parent/Child \\uD83D\\uDE00.txt\""), 1))
            Check.Require(DropboxEntries.Parse(projected.RootElement, pair).Entry == emoji,
                "A surrogate pair split across single bytes projects identically.");

        const string prefix = "{\".tag\":\"file\",\"path_display\":\"/Parent/Child.txt\",\"id\":\"id:file\",";
        const string suffix = ",\"client_modified\":\"2026-01-02T03:04:05Z\",\"rev\":\"revision\"}";

        using (var duplicate = Project(prefix + "\"size\":1,\"size\":2" + suffix, 1))
            Check.Require(DropboxEntries.Parse(duplicate.RootElement, pair).Entry?.Size == 2,
                "A later duplicate selected field replaces the earlier value.");
        using (var respelled = Project("{\".tag\":\"file\",\"path_display\":\"/Parent/First.txt\"," +
                "\"path_display\":\"/Parent/Second.txt\",\"id\":\"id:file\",\"size\":1" + suffix, 1))
            Check.Require(DropboxEntries.Parse(respelled.RootElement, pair).Entry?.Path == "Second.txt",
                "A later duplicate path_display wins.");
        using (var recovered = Project(prefix + "\"size\":\"bad\",\"size\":2" + suffix, 1))
            Check.Require(DropboxEntries.Parse(recovered.RootElement, pair).Entry?.Size == 2,
                "An invalid first duplicate is replaced by the valid last value.");
        using (var inverted = Project(prefix + "\"size\":2,\"size\":\"bad\"" + suffix, 1))
            Check.Require(Rejects(inverted.RootElement, pair),
                "A valid first duplicate does not survive an invalid last value.");
        Check.Require(ProjectRejects(prefix + "\"size\":not-json,\"size\":2" + suffix),
            "Invalid JSON in an overwritten value still fails the item.");
        Check.Require(ProjectRejects("{\".tag\":\"file\",\"path_display\":\"/Parent/Child.txt\""),
            "An unterminated item never publishes a partial document.");

        // Invalid-first/valid-last recovers and valid-first/invalid-last fails for every recognised field.
        foreach (var (field, invalid) in InvalidValues)
        {
            using (var recoveredField = Project(Duplicated(field, invalid, Valid(field)), 1))
                Check.Require(!Rejects(recoveredField.RootElement, pair),
                    $"Invalid-first/valid-last duplicate recovers the {field} field.");
            using (var invertedField = Project(Duplicated(field, Valid(field), invalid), 1))
                Check.Require(Rejects(invertedField.RootElement, pair),
                    $"A valid-first/invalid-last duplicate is rejected for the {field} field.");
        }

        foreach (var (field, _) in FileFields)
        foreach (var nested in new[] { "{}", "[]", "{\"" + field + "\":" + Valid(field) + "}",
            "[{\"" + field + "\":" + Valid(field) + "}]" })
        foreach (var (json, rejects) in new[]
        {
            (Duplicated(field, nested), true),
            (Duplicated(field, Valid(field), nested), true),
            (Duplicated(field, nested, Valid(field)), false)
        })
        {
            using var baseline = JsonDocument.Parse(json);
            using var projected = Project(json, SingleByteChunkSize);
            Check.Require(Rejects(baseline.RootElement, pair) == rejects &&
                Rejects(projected.RootElement, pair) == rejects,
                "Nested recognised values replace the whole field without capturing descendants: " + field);
            if (!rejects)
                Check.Require(DropboxEntries.Parse(projected.RootElement, pair) ==
                    DropboxEntries.Parse(baseline.RootElement, pair),
                    "A later scalar replaces a nested recognised value: " + field);
        }

        foreach (var (fields, rejects) in new[]
        {
            ("\"\\u0073ize\":2", false),
            ("\"size\":\"bad\",\"\\u0073ize\":2", false),
            ("\"\\u0073ize\":\"bad\",\"size\":2", false),
            ("\"size\":2,\"\\u0073ize\":\"bad\"", true),
            ("\"\\u0073ize\":2,\"size\":\"bad\"", true)
        })
        {
            var json = prefix + fields + suffix;
            using var baseline = JsonDocument.Parse(json);
            using var projected = Project(json, 1);
            Check.Require(Rejects(baseline.RootElement, pair) == rejects &&
                Rejects(projected.RootElement, pair) == rejects,
                "Escaped known names preserve duplicate winning-value validity in either spelling order.");
            if (!rejects)
                Check.Require(DropboxEntries.Parse(projected.RootElement, pair) ==
                    DropboxEntries.Parse(baseline.RootElement, pair),
                    "Escaped known names project the same winning entry as JsonDocument.");
        }

        foreach (var json in new[] { "[]", "[{}]", "null", "true", "0", "\"value\"" })
        {
            using var projected = Project(json, SingleByteChunkSize);
            Check.Require(Rejects(projected.RootElement, pair),
                "A complete nonobject root stays invalid metadata after projection.");
        }

        var huge = new string('x', 200_000);
        var hugeRequired = FileJson((DropboxProtocol.Id, JsonSerializer.Serialize(huge)));
        using (var projected = Project(hugeRequired, huge.Length))
            Check.Require(DropboxEntries.Parse(projected.RootElement, pair).Entry?.Id == huge,
                "A huge required scalar is retained in full rather than capped.");
        var hugeDiscarded = Encoding.UTF8.GetBytes(FileJson(("unknown", JsonSerializer.Serialize(huge))));
        ReadTokens(hugeDiscarded, hugeDiscarded.Length);
        using (var unknown = Project(prefix + "\"size\":1,\"" + huge + "\":\"" + huge + "\"" + suffix, 4096))
        {
            Check.Require(!unknown.RootElement.TryGetProperty(huge, out _),
                "An unknown property name and value are validated and skipped, not retained.");
            Check.Require(DropboxEntries.Parse(unknown.RootElement, pair).Entry?.Size == 1,
                "Huge unknown tokens do not disturb the selected fields.");
        }

        var hugeNumber = "1" + new string('0', 200_000);
        using (var unknownNumber = Project(FileJson(("unknown", hugeNumber)), 4096))
            Check.Require(!unknownNumber.RootElement.TryGetProperty("unknown", out _) &&
                DropboxEntries.Parse(unknownNumber.RootElement, pair).Entry?.Size == 1,
                "A huge unknown number is validated and skipped, not retained.");

        var nestedValue = string.Concat(Enumerable.Repeat("[", 10)) + "\"" + new string('x', 100_000) + "\"" +
            string.Concat(Enumerable.Repeat("]", 10));
        using (var unknownNested = Project(FileJson(("unknown", nestedValue)), 4096))
            Check.Require(!unknownNested.RootElement.TryGetProperty("unknown", out _) &&
                DropboxEntries.Parse(unknownNested.RootElement, pair).Entry?.Size == 1,
                "A huge nested unknown value is validated and skipped, not retained.");

        var escapedName = string.Concat(Enumerable.Repeat("\\u0078", 50_000));
        using (var unknownEscaped = Project(FileJson((escapedName, "\"value\"")), 4096))
            Check.Require(!unknownEscaped.RootElement.TryGetProperty(new string('x', 50_000), out _) &&
                DropboxEntries.Parse(unknownEscaped.RootElement, pair).Entry?.Size == 1,
                "An escaped huge unknown property name is validated and skipped, not retained.");

        Check.Require(ProjectRejects(FileJson(("unknown", "tru"))),
            "A malformed discarded literal still fails the item.");
        Check.Require(ProjectRejects(FileJson(("unknown", "\"\\q\""))),
            "A malformed discarded string escape still fails the item.");
        Check.Require(ProjectRejects(FileJson(("unknown",
                string.Concat(Enumerable.Repeat("[", 100)) + string.Concat(Enumerable.Repeat("]", 100))))),
            "A nested value beyond the reader's default depth limit fails.");

        using (var folder = Project("{\".tag\":\"folder\",\"path_display\":\"/Parent/Child\"," +
                "\"id\":\"id:folder\",\"size\":\"not-a-size\",\"client_modified\":\"not-a-timestamp\"," +
                "\"rev\":null,\"content_hash\":null}", 1))
            Check.Require(DropboxEntries.Parse(folder.RootElement, pair) == new Listed("Child",
                    new Entry("Child", true, 0, default, "id:folder", "", "", null)),
                "A folder ignores the invalid file-only fields the projection retains.");
        using (var deleted = Project(
                "{\".tag\":\"deleted\",\"path_display\":\"/Parent/Gone\",\"size\":\"not-a-size\"}", 1))
        {
            Check.Require(DropboxEntries.Parse(deleted.RootElement, pair) == new Listed("Gone", null),
                "A tombstone ignores the file-only fields the projection retains.");
            Check.Require(Throws(() => DropboxEntries.RequireLive(deleted.RootElement, pair, null)),
                "A write reply cannot carry a projected tombstone.");
        }
        using (var skipped = Project(prefix + "\"size\":1,\"is_downloadable\":false" + suffix, 1))
            Check.Require(DropboxEntries.Parse(skipped.RootElement, pair).Entry?.Skip ==
                    nameof(TextKey.SkipNotDownloadable),
                "A non-downloadable file keeps its skip through the projection.");

        using (var nullHash = Project(prefix + "\"size\":1,\"content_hash\":null" + suffix, 1))
            Check.Require(Rejects(nullHash.RootElement, pair),
                "A null selected value stays null through the projection instead of becoming missing.");
        using (var missingHash = Project(prefix + "\"size\":1" + suffix, 1))
            Check.Require(DropboxEntries.Parse(missingHash.RootElement, pair).Entry?.ContentHash == "",
                "A missing optional field stays missing through the projection.");

        var partial = Encoding.UTF8.GetBytes("{\".tag\":\"file\",\"path_display\":\"/Parent/Ch");
        using (var incomplete = new DropboxEntries.Projection())
        {
            var (_, done) = incomplete.Read(partial, false, CancellationToken.None);
            Check.Require(!done,
                "A partial item reports no completion, so nothing is published before its closing brace.");
            Check.Require(ThrowsJson(() => incomplete.Read(ReadOnlySpan<byte>.Empty, true, CancellationToken.None)),
                "A zero-length final read of a partial item fails instead of publishing.");
        }

        using (var empty = new DropboxEntries.Projection())
            Check.Require(ThrowsJson(() => empty.Read(ReadOnlySpan<byte>.Empty, true, CancellationToken.None)),
                "A zero-length final read before any bytes is a syntax failure.");

        var completeBytes = Encoding.UTF8.GetBytes(file);
        using (var completed = new DropboxEntries.Projection())
        {
            var (_, finished) = completed.Read(completeBytes, true, CancellationToken.None);
            Check.Require(finished, "The item completes within its own bytes.");
            var (zero, stillFinished) = completed.Read(ReadOnlySpan<byte>.Empty, true, CancellationToken.None);
            Check.Require(zero == 0 && stillFinished, "A completed item accepts a zero-length final read.");
        }

        var enclosing = Encoding.UTF8.GetBytes(file + ",{\"cursor\":\"c\"}");
        using (var projection = new DropboxEntries.Projection())
        {
            var (consumed, complete) = projection.Read(enclosing, false, CancellationToken.None);
            Check.Require(complete && consumed == Encoding.UTF8.GetByteCount(file),
                "Completion stops at the item's closing brace so the reply reader keeps enclosing syntax.");
        }

        var disposedProjection = new DropboxEntries.Projection();
        disposedProjection.Dispose();
        Check.Require(ThrowsDisposed(() =>
                disposedProjection.Read(ReadOnlySpan<byte>.Empty, true, CancellationToken.None)),
            "A disposed projection refuses reads.");
        Check.Require(ThrowsDisposed(() => disposedProjection.Complete()),
            "A disposed projection refuses its document.");
        disposedProjection.Dispose();

        Check.Require(ThrowsCancellation(() => Project(file, 1, new CancellationToken(canceled: true))),
            "A cancelled projection read stops instead of publishing.");
    }

    private static void ReplyTokenValidation()
    {
        foreach (var json in new[]
        {
            FileJson(), FileJson(("unknown", "{\"size\":[true,false,null]}")),
            "{\"\\u0073ize\":0}", "{\"size\" : 0}", "{\"size\":\"É\\uD83D\\uDE00\"}", "0"
        })
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            for (var split = 0; split <= bytes.Length; split++)
                ReadTokens(bytes, split);
        }

        foreach (var json in new[]
        {
            "", "{", "{\"size\"", "{\"size\":", "{\"size\":0", "{\"size\"::0}",
            "{\"size\":0,}", "{}{}", "{}x", "{\"unknown\":\"\n\"}",
            "{\"unknown\":\"\\q\"}", "{\"unknown\":\"\\uD800\"}", "{\"unknown\":\"\\uDC00\"}",
            "{\"unknown\":01}", "{\"unknown\":1.}", "{\"unknown\":-}", "{\"unknown\":1e}",
            "{\"unknown\":tru}", "{\"size\":{\"unknown\":01},\"size\":0}"
        })
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            for (var split = 0; split <= bytes.Length; split++)
                Check.Require(ThrowsJson(() => ReadTokens(bytes, split)),
                    "Malformed original JSON fails even when every scalar is discarded.");
        }

        // Raw bytes avoid the encoder replacing invalid UTF-8 before the lexer sees it.
        foreach (var hex in new[] { "80", "C080", "E08080", "EDA080", "F4908080", "C2", "E282", "F09F98" })
        {
            var bytes = Encoding.UTF8.GetBytes("{\"unknown\":\"")
                .Concat(Convert.FromHexString(hex)).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
            for (var split = 0; split <= bytes.Length; split++)
                Check.Require(ThrowsJson(() => ReadTokens(bytes, split)),
                    "Malformed UTF-8 is rejected across every split without retaining the string.");
        }
    }

    private static void ReadTokens(byte[] bytes, int split)
    {
        using var tokens = new DropboxEntries.ReplyTokens();
        var offset = 0;
        foreach (var end in new[] { split, bytes.Length })
        {
            while (offset < end)
            {
                var (consumed, ready) = tokens.Read(bytes.AsSpan(offset, end - offset), false,
                    DropboxEntries.Projection.Fields, false, CancellationToken.None);
                Check.Require(consumed > 0 || ready, "The lexer consumes bytes or emits a token.");
                offset += consumed;
                if (ready)
                    Check.Require(tokens.TakeScalar() is null, "Discarded scalars are never returned.");
            }
        }
        // Final numbers may emit a token at EOF before the root can be checked.
        while (true)
        {
            var (consumed, ready) = tokens.Read(ReadOnlySpan<byte>.Empty, true,
                DropboxEntries.Projection.Fields, false, CancellationToken.None);
            Check.Require(consumed == 0, "EOF consumes no bytes.");
            if (!ready) break;
            Check.Require(tokens.TakeScalar() is null, "A discarded final scalar is not retained.");
        }
        tokens.RequireEnd();
        Check.Require(tokens.IsComplete, "The original root is complete at EOF.");
    }

    private static void UploadResults()
    {
        foreach (var item in ContentMatchCases.All())
        {
            var returned = new Entry(item.Id, false, item.AdvertisedSize, default, "id:file", "revision",
                item.ExpectedHash!, null);
            var uploadedHash = DropboxContentHash.Compute(item.Body);
            if (item.ExpectedGate == "accept")
            {
                DropboxEntries.ValidateUploadResult(returned, item.Id, item.Body.LongLength, uploadedHash);
                DropboxEntries.ValidateUploadResult(returned with { Skip = nameof(TextKey.SkipNotDownloadable) },
                    item.Id, item.Body.LongLength, uploadedHash);
            }
            else
                UploadRefused(returned, item.Id, item.Body.LongLength, uploadedHash, item.Id + ": " + item.ExpectedGate);

            if (item.LaterFailure == "upload-envelope")
            {
                foreach (var invalid in new[]
                {
                    returned with { IsFolder = true }, returned with { Path = "" },
                    returned with { Path = "Other" }, returned with { Id = "" }, returned with { Revision = "" }
                })
                    UploadRefused(invalid, item.Id, item.Body.LongLength, uploadedHash, item.Id);
            }
        }
    }

    private static void UploadRefused(Entry returned, string path, long size, string hash, string label)
    {
        try
        {
            DropboxEntries.ValidateUploadResult(returned, path, size, hash);
        }
        catch (ItemFailedException error)
        {
            Check.Require(error.Path == path && error.Cause == TextKey.DropboxRefused && error.InnerException is null,
                label + ": upload size, hash and envelope failures retain the named Dropbox refusal.");
            return;
        }
        Check.Require(false, label + ": invalid upload metadata must not be accepted.");
    }

    private static JsonDocument Project(string json, int chunkSize, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var projection = new DropboxEntries.Projection();
        var complete = false;
        var offset = 0;
        var finalized = false;
        while (!complete)
        {
            var take = Math.Min(chunkSize, bytes.Length - offset);
            if (take == 0)
            {
                // A decoder may finish an item only on a final zero-length read; allow exactly one.
                Check.Require(!finalized, "The projection completes within the item's bytes.");
                finalized = true;
                var (_, finished) = projection.Read(ReadOnlySpan<byte>.Empty, true, cancellationToken);
                complete = finished;
                continue;
            }

            var (consumed, done) = projection.Read(
                bytes.AsSpan(offset, take), offset + take == bytes.Length, cancellationToken);
            Check.Require(consumed > 0 || done, "A projection read consumes bytes or completes the item.");
            offset += consumed;
            Check.Require(offset <= bytes.Length, "A projection never consumes beyond its bytes.");
            complete = done;
        }

        Check.Require(offset == bytes.Length, "The projection completes exactly at the item's end.");
        return projection.Complete();
    }

    private static bool ProjectRejects(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var projection = new DropboxEntries.Projection();
        try
        {
            var complete = false;
            var offset = 0;
            while (!complete && offset < bytes.Length)
            {
                var take = Math.Min(64, bytes.Length - offset);
                var (consumed, done) = projection.Read(
                    bytes.AsSpan(offset, take), offset + take == bytes.Length, CancellationToken.None);
                offset += consumed;
                complete = done;
                if (consumed == 0 && !done) return false;
            }

            if (!complete) using (projection.Complete()) { }
            return false;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return true;
        }
        finally
        {
            projection.Dispose();
        }
    }

    private static bool ThrowsCancellation(Func<JsonDocument> project)
    {
        try
        {
            using var document = project();
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    // The recognised metadata fields with a value Parse accepts, used to build one-field duplicate fixtures.
    private static readonly (string Name, string Json)[] FileFields =
    [
        (DropboxProtocol.Tag, "\"file\""),
        (DropboxProtocol.PathDisplay, "\"/Parent/Child.txt\""),
        (DropboxProtocol.Id, "\"id:file\""),
        (DropboxProtocol.Size, "1"),
        (DropboxProtocol.ClientModified, "\"2026-01-02T03:04:05Z\""),
        (DropboxProtocol.Rev, "\"revision\""),
        (DropboxProtocol.ContentHash, "\"content-hash\""),
        (DropboxProtocol.IsDownloadable, "true")
    ];

    // One value Parse rejects for each recognised field, in the field's own JSON shape.
    private static readonly (string Field, string Invalid)[] InvalidValues =
    [
        (DropboxProtocol.Tag, "\"unknown\""),
        (DropboxProtocol.PathDisplay, "null"),
        (DropboxProtocol.Id, "null"),
        (DropboxProtocol.Size, "\"not-a-size\""),
        (DropboxProtocol.ClientModified, "\"not-a-timestamp\""),
        (DropboxProtocol.Rev, "null"),
        (DropboxProtocol.ContentHash, "null"),
        (DropboxProtocol.IsDownloadable, "\"false\"")
    ];

    private static string Valid(string field) => FileFields.First(entry => entry.Name == field).Json;

    private static string FileText(string pathJson) =>
        "{\".tag\":\"file\",\"path_display\":" + pathJson +
        ",\"id\":\"id:file\",\"size\":1,\"client_modified\":\"2026-01-02T03:04:05Z\",\"rev\":\"revision\"}";

    private static string FileJson(params (string Name, string Json)[] extra)
    {
        var builder = new StringBuilder("{");
        foreach (var (name, json) in FileFields) AppendField(builder, name, json);
        foreach (var (name, json) in extra) AppendField(builder, name, json);
        return builder.Append('}').ToString();
    }

    private static string Duplicated(string field, string first, string? last = null)
    {
        var builder = new StringBuilder("{");
        foreach (var (name, json) in FileFields)
        {
            if (name != field)
            {
                AppendField(builder, name, json);
                continue;
            }
            AppendField(builder, name, first);
            if (last is not null) AppendField(builder, name, last);
        }
        return builder.Append('}').ToString();
    }

    private static void AppendField(StringBuilder builder, string name, string json)
    {
        if (builder.Length > 1) builder.Append(',');
        builder.Append('"').Append(name).Append("\":").Append(json);
    }

    private static bool ThrowsJson(Action action)
    {
        try { action(); return false; }
        catch (JsonException) { return true; }
    }

    private static bool ThrowsDisposed(Action action)
    {
        try { action(); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static void Helpers(PairConfig pair)
    {
        Check.Require(DropboxEntries.IsShapeFailure(new KeyNotFoundException()) &&
            DropboxEntries.IsShapeFailure(new InvalidOperationException()) &&
            DropboxEntries.IsShapeFailure(new FormatException()) &&
            !DropboxEntries.IsShapeFailure(new JsonException()) &&
            !DropboxEntries.IsShapeFailure(new IOException()) &&
            !DropboxEntries.IsShapeFailure(new OperationCanceledException()),
            "Shape failures are exactly the three extraction exceptions.");

        var shape = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["present"] = "value", ["empty"] = "", ["number"] = 1, ["null"] = null
        });
        Check.Require(DropboxEntries.RequiredString(shape, "present") == "value" &&
            DropboxEntries.RequiredString(shape, "empty") == "",
            "Required strings accept non-empty and empty values.");
        Check.Require(Throws(() => DropboxEntries.RequiredString(shape, "missing")) &&
            Throws(() => DropboxEntries.RequiredString(shape, "null")) &&
            Throws(() => DropboxEntries.RequiredString(shape, "number")),
            "Missing, null and wrong-kind required strings keep their extraction exceptions.");

        var tombstone = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.TagDeleted,
            [DropboxProtocol.PathDisplay] = pair.DropboxRoot + "/Gone"
        });
        Check.Require(Throws(() => DropboxEntries.RequireLive(tombstone, pair, null)),
            "A write reply cannot carry a tombstone.");
        var live = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.TagFolder,
            [DropboxProtocol.PathDisplay] = pair.DropboxRoot + "/Child",
            [DropboxProtocol.Id] = "id:child"
        });
        Check.Require(DropboxEntries.RequireLive(live, pair, null).Path == "Child",
            "RequireLive returns the parsed live entry.");
    }

    private static void AllocationProbe(PairConfig pair)
    {
        const int calls = 1000;
        var element = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["present"] = "value" });
        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.TagFolder,
            [DropboxProtocol.PathDisplay] = pair.DropboxRoot + "/Child",
            [DropboxProtocol.Id] = "id:child"
        });
        var entry = new Entry("Docs/A/Child.txt", false, 1, default, "id:child", "rev", "", null);

        Action<int> baselineString = index => _ = element.GetProperty("present").GetString() ?? throw new JsonException();
        Action<int> candidateString = index => _ = DropboxEntries.RequiredString(element, "present");
        Action<int> baselineLive = index => _ = DropboxEntries.Parse(metadata, pair, null).Entry ?? throw new JsonException();
        Action<int> candidateLive = index => _ = DropboxEntries.RequireLive(metadata, pair, null);
        Action<int> baselineUnion = index => _ = new Dictionary<string, string>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.ModeUpdate,
            [DropboxProtocol.ModeUpdate] = "old-rev"
        };
        Action<int> candidateUnion = index => _ = DropboxProtocol.TaggedUnion(DropboxProtocol.ModeUpdate, "old-rev");
        Action<int> baselineRespelled = index => _ = entry with
        {
            Path = PathNames.Child(PathNames.Parent("Docs/A/Child.txt"),
                entry.Path[(entry.Path.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal) + 1)..])
        };
        Action<int> candidateRespelled = index => _ = DropboxListing.Respelled(entry, "Docs/A/Child.txt");
        const string dropboxPath = "Docs/A/Child.txt";
        Action<int> baselineTransferable = index => Sink(PathRules.IsWindowsTransferPath(PathNames.LocalSpelling(dropboxPath)));
        Action<int> candidateTransferable = index => Sink(PathNames.IsTransferable(dropboxPath));

        var baselineBytes = AllocProbe.Measure(baselineTransferable, calls);
        var candidateBytes = AllocProbe.Measure(candidateTransferable, calls);
        Check.Require(AllocProbe.Measure(baselineString, calls) == AllocProbe.Measure(candidateString, calls) &&
            AllocProbe.Measure(baselineLive, calls) == AllocProbe.Measure(candidateLive, calls) &&
            AllocProbe.Measure(baselineUnion, calls) == AllocProbe.Measure(candidateUnion, calls) &&
            AllocProbe.Measure(baselineRespelled, calls) == AllocProbe.Measure(candidateRespelled, calls) &&
            baselineBytes == candidateBytes,
            "RequiredString, RequireLive, TaggedUnion, Respelled and IsTransferable allocate the same bytes per call as the code they replaced.");
    }

    // A typed, non-inlined sink keeps the probed call's result live without boxing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value) => _boolean = value;

    private static bool _boolean;

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException
            or FormatException or JsonException)
        {
            return true;
        }
    }

    private static void ResponseShapes(PairConfig pair)
    {
        // https://docs.dropboxapi.com/dropbox-api/api-reference/user-endpoints/files/
        // upload and upload-session-finish return FileMetadata; create-folder-v-2 wraps FolderMetadata.
        var file = new Dictionary<string, object?>
        {
            ["name"] = "Child.txt", ["path_lower"] = "/parent/child.txt", ["path_display"] = "/Parent/Child.txt",
            ["id"] = "id:file", ["rev"] = "revision", ["size"] = 1L,
            ["client_modified"] = "2024-01-02T01:04:05Z", ["server_modified"] = "2024-01-02T01:04:06Z"
        };
        var folder = new Dictionary<string, object?>
        {
            ["name"] = "Child", ["path_lower"] = "/parent/child", ["path_display"] = "/Parent/Child",
            ["id"] = "id:folder"
        };
        foreach (var fields in new[] { file, folder })
        {
            var isFolder = ReferenceEquals(fields, folder);
            var kind = isFolder ? DropboxProtocol.TagFolder : DropboxProtocol.TagFile;
            var untagged = JsonSerializer.SerializeToElement(fields);
            Check.Require(Rejects(untagged, pair), "Metadata unions must reject a missing .tag.");
            var actual = DropboxEntries.Parse(untagged, pair, kind);
            var expected = isFolder
                ? new Entry("Child", true, 0, default, "id:folder", "", "", null)
                : new Entry("Child.txt", false, 1, DateTimeOffset.Parse("2024-01-02T01:04:05Z"),
                    "id:file", "revision", "", null);
            Check.Require(actual.Entry == expected, "Concrete metadata accepts only the caller-supplied missing kind.");
            if (isFolder)
            {
                var created = JsonSerializer.SerializeToElement(new { metadata = fields });
                Check.Require(DropboxEntries.Parse(created.GetProperty("metadata"), pair, kind) == actual,
                    "create_folder_v2 wraps untagged FolderMetadata.");
            }

            // get-metadata returns Metadata, move-v-2 wraps it, and list-folder contains a list of it.
            fields[".tag"] = kind;
            var tagged = JsonSerializer.SerializeToElement(fields);
            var moved = JsonSerializer.SerializeToElement(new { metadata = fields });
            var listed = JsonSerializer.SerializeToElement(new { entries = new[] { fields }, cursor = "cursor", has_more = false });
            foreach (var metadata in new[] { tagged, moved.GetProperty("metadata"), listed.GetProperty("entries")[0] })
                Check.Require(DropboxEntries.Parse(metadata, pair) == actual,
                    "get_metadata, move_v2 and list_folder use tagged file and folder Metadata.");
            Check.Require(DropboxEntries.Parse(tagged, pair,
                isFolder ? DropboxProtocol.TagFile : DropboxProtocol.TagFolder) == actual,
                "An explicit .tag takes precedence over the missing-tag fallback.");
            foreach (var invalidTag in new object?[] { null, "unknown", 1 })
            {
                fields[".tag"] = invalidTag;
                Check.Require(Rejects(JsonSerializer.SerializeToElement(fields), pair, kind),
                    "A missing-tag fallback must not hide a malformed explicit .tag.");
            }
            fields.Remove(".tag");
            fields.Remove("id");
            Check.Require(Rejects(JsonSerializer.SerializeToElement(fields), pair, kind),
                "Untagged metadata still requires its other metadata fields.");
        }
    }

    private static bool Rejects(JsonElement metadata, PairConfig pair, string? untaggedKind = null)
    {
        try
        {
            DropboxEntries.Parse(metadata, pair, untaggedKind);
            return false;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return true;
        }
    }
}
