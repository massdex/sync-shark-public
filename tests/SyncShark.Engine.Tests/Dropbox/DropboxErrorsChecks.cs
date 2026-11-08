using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for DropboxErrors (`--engine-checks dropbox`), written by DropboxErrors's writer.</summary>
public static class DropboxErrorsChecks
{
    // Standard 1.3 size prompt answered by record, not a by-job split: this type has one job — assert one owner's
    // mapping decisions — and its methods are long only because each holds provider case data and the loop that
    // asserts it. Splitting per method would make one type per case table, not separate jobs.
    public static void Run()
    {
        const string path = "/Parent/Child";
        var cases = new (string Route, string Summary, bool ExpectedIsNull, Type? ExpectedType,
            TextKey ExpectedCause)[]
        {
            (DropboxProtocol.GetMetadata, DropboxProtocol.PathNotFound, true, null, TextKey.DropboxRefused),
            (DropboxProtocol.Download, DropboxProtocol.PathNotFound, false,
                typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, DropboxProtocol.PathConflict, true,
                typeof(PathOccupiedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, DropboxProtocol.PathConflict, false,
                typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.UploadSessionFinish, DropboxProtocol.PathConflict, true,
                typeof(PathOccupiedException), TextKey.DropboxRefused),
            (DropboxProtocol.UploadSessionFinish, DropboxProtocol.PathConflict, false,
                typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.CreateFolder, DropboxProtocol.PathConflictFolder, true,
                null, TextKey.DropboxRefused),
            (DropboxProtocol.CreateFolder, "path/conflict/file", true,
                typeof(PathOccupiedException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, DropboxProtocol.PathLookupNotFound, false,
                typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, "path_lookup/not_file", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, "path_write/no_write_permission", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.Move, DropboxProtocol.FromLookupNotFound, false,
                typeof(ChangedUnderneathException), TextKey.DropboxRefused),
            (DropboxProtocol.Move, DropboxProtocol.ToConflict, true,
                typeof(PathOccupiedException), TextKey.DropboxRefused),
            (DropboxProtocol.GetMetadata, "path/restricted_content", true,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            ("files/unknown", DropboxProtocol.PathNotFound, true,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Download, DropboxProtocol.PathConflict, false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.GetMetadata, "PATH/not_found", true,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.GetMetadata, "other/path/not_found", true,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.GetMetadata, "", true,
                typeof(ItemFailedException), TextKey.DropboxRefused),

            // Documented write-refusal families, by route and its documented write location.
            (DropboxProtocol.Upload, "path/malformed_path", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Upload, "path/disallowed_name", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Upload, "path/no_write_permission", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.Upload, "path/insufficient_space", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoSpace),
            (DropboxProtocol.UploadSessionFinish, "path/malformed_path", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.UploadSessionFinish, "path/disallowed_name", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.UploadSessionFinish, "path/no_write_permission", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.UploadSessionFinish, "path/insufficient_space", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoSpace),
            (DropboxProtocol.CreateFolder, "path/malformed_path", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.CreateFolder, "path/disallowed_name", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.CreateFolder, "path/no_write_permission", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.CreateFolder, "path/insufficient_space", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoSpace),
            (DropboxProtocol.Delete, "path_write/malformed_path", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Delete, "path_write/insufficient_space", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoSpace),
            (DropboxProtocol.Delete, "path_write/disallowed_name", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Move, "to/malformed_path", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Move, "to/no_write_permission", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.Move, "to/disallowed_name", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Move, "to/insufficient_space", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoSpace),
            (DropboxProtocol.Move, "from_write/malformed_path", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Move, "from_write/no_write_permission", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoPermission),
            (DropboxProtocol.Move, "from_write/disallowed_name", false,
                typeof(ItemFailedException), TextKey.FailDropboxNameRejected),
            (DropboxProtocol.Move, "from_write/insufficient_space", false,
                typeof(ItemFailedException), TextKey.FailDropboxNoSpace),

            // A lookup, another route's write location or a read route is not a documented write refusal.
            (DropboxProtocol.Delete, "path/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, "path_lookup/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "path_write/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "path_lookup/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Move, "from_lookup/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.GetMetadata, "path/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),

            // Complete tag boundaries: a substring, another tag, a case mismatch or a location not at the
            // start of the summary is not the documented refusal.
            (DropboxProtocol.Upload, "path/no_write_permission_extra", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "path/not_no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "path/disallowed_name_extra", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "PATH/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Upload, "xpath/no_write_permission", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Delete, "path_write/no_write_permission_extra", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Move, "to/no_write_permission_extra", false,
                typeof(ItemFailedException), TextKey.DropboxRefused),
            (DropboxProtocol.Move, "from_write/insufficient_space_extra", false,
                typeof(ItemFailedException), TextKey.DropboxRefused)
        };

        foreach (var test in cases)
        foreach (var suffix in new[] { "", "/provider-detail" })
        foreach (var status in new[] { HttpStatusCode.Conflict, HttpStatusCode.Forbidden })
        {
            var error = new DropboxApiException(status, test.Summary + suffix);
            var result = DropboxErrors.Map(test.Route, error, test.ExpectedIsNull, path);
            var expectedType = status == HttpStatusCode.Conflict ? test.ExpectedType : typeof(ItemFailedException);
            // The documented write families are typed only for the provider's 409 refusal; another status is unclassified.
            var expectedCause = status == HttpStatusCode.Conflict ? test.ExpectedCause : TextKey.DropboxRefused;
            var context = $"{test.Route}: {status}, {error.SafeReason}, expectedIsNull={test.ExpectedIsNull}";
            if (expectedType is null)
            {
                Check.Require(result.IsAbsent, context);
                continue;
            }
            Check.Require(!result.IsAbsent, context);
            var mapped = result.Error;
            Check.Require(mapped.GetType() == expectedType, context);
            switch (mapped)
            {
                case ChangedUnderneathException changed:
                    Check.Require(changed.Path == path, context);
                    break;
                case PathOccupiedException occupied:
                    Check.Require(occupied.Path == path, context);
                    break;
                case ItemFailedException failed:
                    Check.Require(failed.Path == path && failed.Cause == expectedCause, context);
                    Check.Require(ReferenceEquals(failed.InnerException, error), context);
                    Check.Require(failed.Message == Text.Get(expectedCause,
                        (TextArg.Path, path), (TextArg.Message, error.SafeReason)), context);
                    break;
            }
        }

        ResultShapes(path);
        UploadFailureShapes(path);
        SummaryShapes();
        WriteThrottleShapes();
        FolderFailureShapes();
        ClassifierShapes();
    }

    // The closed result: absence is explicit, and a default or refusal-less value can never grant it.
    private static void ResultShapes(string path)
    {
        Check.Require(DropboxErrors.MapResult.Absent.IsAbsent,
            "The documented absence result reports absence.");
        var invalid = default(DropboxErrors.MapResult);
        Check.Require(!invalid.IsAbsent, "An uninitialised mapped result never proves absence.");
        var refused = false;
        try { _ = invalid.Error; }
        catch (InvalidOperationException) { refused = true; }
        Check.Require(refused, "An uninitialised mapped result carries no refusal.");
        var failure = DropboxErrors.MapResult.Failure(new ItemFailedException(path, TextKey.DropboxRefused, null));
        Check.Require(!failure.IsAbsent && failure.Error is ItemFailedException,
            "A refusal is distinct from absence and carries its exception.");
    }

    private static void SummaryShapes()
    {
        var present = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { [DropboxProtocol.ErrorSummary] = DropboxProtocol.PathNotFound });
        Check.Require(DropboxErrors.Summary(present) == DropboxProtocol.PathNotFound,
            "An object's string error_summary is the summary.");
        foreach (var body in new object?[]
        {
            new Dictionary<string, object>(), new Dictionary<string, object> { [DropboxProtocol.ErrorSummary] = 1 },
            new Dictionary<string, object?> { [DropboxProtocol.ErrorSummary] = null }, new[] { 1 }, "text", 42, null
        })
            Check.Require(DropboxErrors.Summary(JsonSerializer.SerializeToElement(body)) == "",
                "A missing, non-object or non-string error_summary is empty and never throws.");
    }

    private static void WriteThrottleShapes()
    {
        static JsonElement Root(object? value) => JsonSerializer.SerializeToElement(value);
        static Dictionary<string, object> WithSummary(string summary) => new()
            { [DropboxProtocol.ErrorSummary] = summary };

        // A structured throttle needs no summary, at the union tag, the WriteError under path or path.reason, or reason.
        var structured = new object[]
        {
            new Dictionary<string, object>
                { [DropboxProtocol.Tag] = DropboxProtocol.TooManyWriteOperations },
            new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [DropboxProtocol.Path] = new Dictionary<string, object>
                    { [DropboxProtocol.Tag] = DropboxProtocol.TooManyWriteOperations }
            },
            new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [DropboxProtocol.Path] = new Dictionary<string, object>
                {
                    [DropboxProtocol.Reason] = new Dictionary<string, object>
                        { [DropboxProtocol.Tag] = DropboxProtocol.TooManyWriteOperations }
                }
            },
            new Dictionary<string, object>
            {
                [DropboxProtocol.Reason] = new Dictionary<string, object>
                    { [DropboxProtocol.Tag] = DropboxProtocol.TooManyWriteOperations }
            }
        };
        foreach (var error in structured)
            Check.Require(DropboxErrors.IsWriteThrottle(Root(new Dictionary<string, object>
                { [DropboxProtocol.Error] = error })),
                "A no-summary structured union, path, path.reason or reason tag identifies write throttling.");
        foreach (var summary in new[]
        {
            DropboxProtocol.TooManyWriteOperations,
            DropboxProtocol.TooManyWriteOperations + "/",
            "/" + DropboxProtocol.TooManyWriteOperations,
            "path/" + DropboxProtocol.TooManyWriteOperations + "/detail",
            "//" + DropboxProtocol.TooManyWriteOperations + "//"
        })
            Check.Require(DropboxErrors.IsWriteThrottle(Root(WithSummary(summary))),
                "A complete summary segment identifies write throttling, including empty edge segments.");
        foreach (var summary in new[]
        {
            "", "path/not_too_many_write_operations", "path/too_many_write_operations_suffix",
            DropboxProtocol.TooManyWriteOperations.ToUpperInvariant(),
            "path/Too_Many_Write_Operations",
            "//not_too_many_write_operations//",
            DropboxProtocol.TooManyWriteOperations + "_extra//path",
            "/", "//", "///"
        })
            Check.Require(!DropboxErrors.IsWriteThrottle(Root(WithSummary(summary))),
                "A substring, case-mismatched, separator-only or empty segment is not write throttling.");
        foreach (var error in new object?[]
        {
            new Dictionary<string, object> { [DropboxProtocol.Reason] = 1 },
            new Dictionary<string, object> { [DropboxProtocol.Reason] = new Dictionary<string, object>() },
            new Dictionary<string, object>
                { [DropboxProtocol.Reason] = new Dictionary<string, object> { [DropboxProtocol.Tag] = 1 } },
            "not-an-object"
        })
            Check.Require(DropboxErrors.IsWriteThrottle(Root(new Dictionary<string, object?>
            {
                [DropboxProtocol.Error] = error,
                [DropboxProtocol.ErrorSummary] = DropboxProtocol.TooManyWriteOperations
            })), "A malformed structured branch falls through to a valid summary.");
        foreach (var root in new object?[] { new[] { 1 }, "text", 42, null })
            Check.Require(!DropboxErrors.IsWriteThrottle(Root(root)),
                "A non-object JSON root is not a write throttle and must not throw.");
    }

    private static void FolderFailureShapes()
    {
        static JsonElement Root(object? value) => JsonSerializer.SerializeToElement(value);
        static Dictionary<string, object> Lookup(string tag) => new()
        {
            [DropboxProtocol.Tag] = DropboxProtocol.PathLookup,
            [DropboxProtocol.PathLookup] = new Dictionary<string, object> { [DropboxProtocol.Tag] = tag }
        };

        var cases = new (object? Failure, TextKey? Cause)[]
        {
            (Lookup("not_found"), TextKey.FailChanged),
            (Lookup("not_file"), TextKey.DropboxRefused),
            (Lookup("not_folder"), TextKey.DropboxRefused),
            // A malformed lookup carries no cause; no DeleteError tag proves the effect was not applied.
            (new Dictionary<string, object> { [DropboxProtocol.Tag] = DropboxProtocol.PathLookup }, null),
            (new Dictionary<string, object>
                { [DropboxProtocol.Tag] = DropboxProtocol.PathLookup, [DropboxProtocol.PathLookup] = "text" }, null),
            (new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.PathLookup,
                [DropboxProtocol.PathLookup] = new Dictionary<string, object>()
            }, null),
            (new Dictionary<string, object>(), null),
            (null, null),
            ("text", null)
        };
        foreach (var test in cases)
            Check.Require(DropboxErrors.MapFolderFailure(Root(test.Failure), 0) ==
                new FolderMemberResult.Unknown(0, test.Cause),
                "A folder path_lookup not_found is FailChanged, another lookup tag DropboxRefused and a malformed shape no cause.");
    }

    private static void ClassifierShapes()
    {
        static DropboxApiException Refusal(HttpStatusCode status, object? failure)
        {
            var error = new DropboxApiException(status, "");
            if (failure is not null) error.Data[typeof(JsonElement)] = JsonSerializer.SerializeToElement(failure);
            return error;
        }
        static Dictionary<string, object> PathFailure(string reason) => new()
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Path,
            [DropboxProtocol.Path] = new Dictionary<string, object> { [DropboxProtocol.Tag] = reason }
        };

        var structured = new (HttpStatusCode Status, object? Failure, bool Expected)[]
        {
            (HttpStatusCode.Conflict, PathFailure("not_found"), true),
            (HttpStatusCode.Conflict, PathFailure("not_file"), false),
            (HttpStatusCode.Conflict, null, false),
            (HttpStatusCode.NotFound, PathFailure("not_found"), false),
            (HttpStatusCode.Forbidden, PathFailure("not_found"), false)
        };
        foreach (var test in structured)
            Check.Require(DropboxErrors.IsStructuredPathNotFound(Refusal(test.Status, test.Failure)) == test.Expected,
                $"{test.Status}: only the exact structured 409 path/not_found is the documented miss.");

        // History-only means an exhausted ordinary transient refusal on the one history route; auth, permission,
        // path-root, non-transient and non-history failures never downgrade to HistoryUnavailable.
        var history = new (string Route, HttpStatusCode Status, object? Failure, bool Expected)[]
        {
            (DropboxProtocol.ListRevisions, HttpStatusCode.RequestTimeout, null, true),
            (DropboxProtocol.ListRevisions, HttpStatusCode.TooManyRequests, null, true),
            (DropboxProtocol.ListRevisions, HttpStatusCode.ServiceUnavailable, null, true),
            (DropboxProtocol.ListRevisions, HttpStatusCode.Unauthorized, null, false),
            (DropboxProtocol.ListRevisions, HttpStatusCode.Forbidden, null, false),
            (DropboxProtocol.ListRevisions, HttpStatusCode.UnprocessableEntity, null, false),
            (DropboxProtocol.ListRevisions, HttpStatusCode.Conflict, PathFailure("not_found"), false),
            (DropboxProtocol.ListRevisions, HttpStatusCode.Conflict, null, false),
            (DropboxProtocol.GetMetadata, HttpStatusCode.ServiceUnavailable, null, false)
        };
        foreach (var test in history)
            Check.Require(DropboxErrors.IsHistoryOnlyFailure(test.Route, Refusal(test.Status, test.Failure)) == test.Expected,
                $"{test.Route}: {test.Status} is history-only only for an exhausted list_revisions transient failure.");
    }

    private static void UploadFailureShapes(string path)
    {
        static JsonElement Root(object? value) => JsonSerializer.SerializeToElement(value);
        static Dictionary<string, object> Tagged(string tag) => new() { [DropboxProtocol.Tag] = tag };

        // Session finish nests WriteError under path; direct upload inlines the UploadWriteFailed reason;
        // the documented wrapper nests that reason under path.
        var shapes = new (string Name, Func<string, object> Build)[]
        {
            ("session finish", tag => new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [DropboxProtocol.Path] = Tagged(tag)
            }),
            ("direct upload", tag => new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [DropboxProtocol.Reason] = Tagged(tag)
            }),
            ("nested path reason", tag => new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [DropboxProtocol.Path] = new Dictionary<string, object>
                    { [DropboxProtocol.Reason] = Tagged(tag) }
            })
        };
        var writeCases = new (string Tag, bool ExpectedIsNull, Type ExpectedType, TextKey Cause,
            bool Throttled)[]
        {
            (DropboxProtocol.Conflict, true, typeof(PathOccupiedException), TextKey.DropboxRefused, false),
            (DropboxProtocol.Conflict, false, typeof(ChangedUnderneathException), TextKey.DropboxRefused, false),
            (DropboxProtocol.MalformedPath, false, typeof(ItemFailedException), TextKey.FailDropboxNameRejected, false),
            (DropboxProtocol.DisallowedName, false, typeof(ItemFailedException), TextKey.FailDropboxNameRejected, false),
            (DropboxProtocol.NoWritePermission, false, typeof(ItemFailedException), TextKey.FailDropboxNoPermission, false),
            (DropboxProtocol.InsufficientSpace, false, typeof(ItemFailedException), TextKey.FailDropboxNoSpace, false),
            (DropboxProtocol.InsufficientSpace, true, typeof(ItemFailedException), TextKey.FailDropboxNoSpace, false),
            (DropboxProtocol.TooManyWriteOperations, false, typeof(ItemFailedException), TextKey.DropboxRefused, true),
            (DropboxProtocol.TooManyWriteOperations, true, typeof(ItemFailedException), TextKey.DropboxRefused, true),
            // Other documented WriteError members stay ordinary refusals, not the four existing causes.
            ("team_folder", false, typeof(ItemFailedException), TextKey.DropboxRefused, false),
            ("team_folder_insufficient_space", false, typeof(ItemFailedException), TextKey.DropboxRefused, false),
            ("member_folder_insufficient_space", false, typeof(ItemFailedException), TextKey.DropboxRefused, false),
            ("access_restricted", false, typeof(ItemFailedException), TextKey.DropboxRefused, false),
            ("operation_suppressed", false, typeof(ItemFailedException), TextKey.DropboxRefused, false),
            ("upload_traffic_limit_reached", false, typeof(ItemFailedException), TextKey.DropboxRefused, false),
            (DropboxProtocol.TagOther, false, typeof(ItemFailedException), TextKey.DropboxRefused, false),
            ("future_write_error", false, typeof(ItemFailedException), TextKey.DropboxRefused, false)
        };
        foreach (var shape in shapes)
        foreach (var test in writeCases)
            RequireUploadFailure(Root(shape.Build(test.Tag)), path, test.ExpectedIsNull, test.ExpectedType,
                test.Cause, test.Throttled, $"{shape.Name}: path/{test.Tag}");

        // A top-level (non-path) union tag is a valid refusal; only too_many_write_operations throttles.
        var topLevel = new (string Tag, bool Throttled)[]
        {
            (DropboxProtocol.TooManyWriteOperations, true),
            (DropboxProtocol.TagOther, false),
            ("lookup_failed", false),
            ("properties_error", false),
            ("payload_too_large", false),
            ("content_hash_mismatch", false),
            ("future_union_tag", false)
        };
        foreach (var test in topLevel)
            RequireUploadFailure(Root(Tagged(test.Tag)), path, true, typeof(ItemFailedException),
                TextKey.DropboxRefused, test.Throttled, $"top-level {test.Tag}");

        // Narrow shape handling: only a malformed required member is a decode failure.
        foreach (var failure in new object?[]
        {
            null, "text", 42, new[] { 1 },
            new Dictionary<string, object>(),
            new Dictionary<string, object> { [DropboxProtocol.Tag] = 1 },
            new Dictionary<string, object?> { [DropboxProtocol.Tag] = null },
            new Dictionary<string, object> { [DropboxProtocol.Tag] = new[] { 1 } },
            new Dictionary<string, object> { [DropboxProtocol.Tag] = DropboxProtocol.Path },
            new Dictionary<string, object>
                { [DropboxProtocol.Tag] = DropboxProtocol.Path, [DropboxProtocol.Path] = "text" },
            new Dictionary<string, object?>
                { [DropboxProtocol.Tag] = DropboxProtocol.Path, [DropboxProtocol.Path] = null },
            new Dictionary<string, object>
                { [DropboxProtocol.Tag] = DropboxProtocol.Path, [DropboxProtocol.Path] = new Dictionary<string, object>() },
            new Dictionary<string, object>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [DropboxProtocol.Path] = new Dictionary<string, object> { [DropboxProtocol.Tag] = 1 }
            }
        })
        {
            Exception? thrown = null;
            try
            {
                DropboxErrors.MapUploadFailure(Root(failure), true, path);
            }
            catch (Exception error)
            {
                thrown = error;
            }
            Check.Require(thrown?.GetType() == typeof(JsonException),
                "A malformed structured upload failure is a JsonException.");
        }
    }

    private static void RequireUploadFailure(JsonElement failure, string path, bool expectedIsNull,
        Type expectedType, TextKey expectedCause, bool expectedThrottled, string context)
    {
        var mapped = DropboxErrors.MapUploadFailure(failure, expectedIsNull, path);
        Check.Require(mapped.Error.GetType() == expectedType, context);
        Check.Require(mapped.WriteThrottled == expectedThrottled, context);
        switch (mapped.Error)
        {
            case PathOccupiedException occupied:
                Check.Require(occupied.Path == path, context);
                break;
            case ChangedUnderneathException changed:
                Check.Require(changed.Path == path, context);
                break;
            case ItemFailedException failed:
                Check.Require(failed.Path == path && failed.Cause == expectedCause, context);
                Check.Require(failed.InnerException is ProviderFailureException evidence &&
                    evidence.Route == DropboxProtocol.UploadSessionFinishBatch && evidence.Tag.Length != 0, context);
                Check.Require(failed.Message == Text.Get(expectedCause,
                    (TextArg.Path, path), (TextArg.Message, failed.InnerException!.Message)), context);
                break;
        }
    }
}
