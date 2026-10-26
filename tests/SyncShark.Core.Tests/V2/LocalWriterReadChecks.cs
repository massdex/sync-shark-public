using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark.V2;

/// <summary>The local writer observation and read checks: observe, open-read and the write-error mapping.</summary>
internal static class LocalWriterReadChecks
{
    public static async Task RunAsync()
    {
        await HardLinkAbsenceAsync().ConfigureAwait(false);
        await ObserveAsync().ConfigureAwait(false);
        await OpenReadAsync().ConfigureAwait(false);
        await OpenReadChangedAsync().ConfigureAwait(false);
        ErrorMapping();
    }

    private static async Task HardLinkAbsenceAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("hard-link-absence").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "original.txt", "retained");
        var historical = (await setup.Writer.ObserveAsync("original.txt", default))!;
        var outside = Path.Combine(Path.GetDirectoryName(setup.Root)!, "outside.txt");
        File.Move(Path.Combine(setup.Root, "original.txt"), outside);
        var alias = Path.Combine(setup.Root, "alias.txt");
        LocalChecks.CreateHardLink(alias, outside);
        LocalChecks.Require(await setup.Writer.IsSourceAbsentAsync(historical, historical.Path, default) != true,
            "outside plus inside alias cannot authorize deletion at the absent historical path");
        File.Move(alias, Path.Combine(Path.GetDirectoryName(setup.Root)!, "second-outside.txt"));
        LocalChecks.Require(await setup.Writer.IsSourceAbsentAsync(historical, historical.Path, default) is null,
            "one outside name cannot establish membership of a multi-link identity");
        var batch = await setup.Writer.IsAbsentBatchAsync([(historical, historical.Path)], default);
        LocalChecks.Require(batch.Single().Absent is null,
            "batched absence uses the same conservative hard-link rule");
        File.Delete(Path.Combine(Path.GetDirectoryName(setup.Root)!, "second-outside.txt"));
        LocalChecks.Require(await setup.Writer.IsSourceAbsentAsync(historical, historical.Path, default) == true,
            "single-name move outside retains the approved absent policy");
    }

    private static async Task ObserveAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("observe").ConfigureAwait(false);
        LocalChecks.Require(await setup.Writer.ObserveAsync("nope.txt", CancellationToken.None) is null,
            "observing an absent item returns null");
        LocalChecks.Seed(setup.Root, "o.txt", "observe");
        var state = await setup.Writer.ObserveAsync("o.txt", CancellationToken.None);
        LocalChecks.Require(state is { Kind: ItemKind.File, Size: 7 } &&
            state.Hash == LocalChecks.BlockHash(LocalChecks.Bytes("observe")),
            "observing a present item returns its exact identity, size and hash");
    }

    private static async Task OpenReadAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("open-read").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "r.txt", "read-me");
        var state = (await setup.Writer.ObserveAsync("r.txt", CancellationToken.None))!;
        await using var stream = await setup.Writer.OpenReadAsync(state, CancellationToken.None);
        using var reader = new StreamReader(stream);
        LocalChecks.Require(await reader.ReadToEndAsync() == "read-me",
            "open-read returns exactly the planned file bytes");
    }

    // A source that changed under the held handle — appended, equal-size edit, or a swap — is a stale read.
    private static async Task OpenReadChangedAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("open-read-changed").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "c.txt", "content");
        var appended = (await setup.Writer.ObserveAsync("c.txt", CancellationToken.None))!;
        File.AppendAllText(Path.Combine(setup.Root, "c.txt"), "extra");
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.OpenReadAsync(appended, CancellationToken.None));

        LocalChecks.Seed(setup.Root, "same.txt", "aaaa");
        var equal = (await setup.Writer.ObserveAsync("same.txt", CancellationToken.None))!;
        var samePath = Path.Combine(setup.Root, "same.txt");
        var sameMtime = File.GetLastWriteTimeUtc(samePath);
        File.WriteAllBytes(samePath, LocalChecks.Bytes("bbbb"));
        File.SetLastWriteTimeUtc(samePath, sameMtime);
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.OpenReadAsync(equal, CancellationToken.None));

        LocalChecks.Seed(setup.Root, "swap.txt", "swap");
        var original = (await setup.Writer.ObserveAsync("swap.txt", CancellationToken.None))!;
        var swapPath = Path.Combine(setup.Root, "swap.txt");
        var swapMtime = File.GetLastWriteTimeUtc(swapPath);
        File.Delete(swapPath);
        LocalChecks.Seed(setup.Root, "swap.txt", "swap");
        File.SetLastWriteTimeUtc(swapPath, swapMtime);
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.OpenReadAsync(original, CancellationToken.None));
    }

    private static void ErrorMapping()
    {
        var unrelated = new IOException("x", new COMException("y", unchecked((int)0x80040091)));
        LocalChecks.Require(ReferenceEquals(LocalItems.MapWriteError(unrelated), unrelated),
            "an unrelated facility HRESULT ending in 0x91 is not a target mismatch");
        var notEmpty = new IOException("x", new COMException("y", unchecked((int)0x80070091)));
        LocalChecks.Require(LocalItems.MapWriteError(notEmpty) is ExpectedTargetMismatchException,
            "the exact directory-not-empty HRESULT maps to a target mismatch");
        var native = new IOException("x", new Win32Exception(145));
        LocalChecks.Require(LocalItems.MapWriteError(native) is ExpectedTargetMismatchException,
            "a Win32Exception carrying error 145 maps to a target mismatch");
    }
}
