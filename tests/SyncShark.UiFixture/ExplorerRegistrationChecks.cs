using System.IO;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class ExplorerRegistrationChecks
{
    internal static int Run()
    {
        var count = 0;
        var version = new Version(0, 3, 34, 0);
        const string root = @"C:\Program Files\Massdex\SyncShark\versions\0.3.34";
        var current = new ExplorerPackage.Registration("exact-current-package", ExplorerPackage.Publisher, version, root);
        var effects = new List<string>();
        void Check(bool pass, string label)
        { if (!pass) throw new IOException(label); count++; Console.WriteLine("PASS " + label); }
        void Apply(bool register, bool active, ExplorerPackage.Registration[] existing, bool payloadValid = true)
        {
            effects.Clear();
            ExplorerPackage.ApplyObservedRegistrationAsync(register, active, root, version, existing,
                () => { effects.Add("verify"); if (!payloadValid) throw new IOException("Fixture payload mismatch"); },
                () => { effects.Add("add"); return Task.CompletedTask; },
                name => { Check(name == current.FullName, "Remove uses the admitted exact package identity"); effects.Add("remove"); return Task.CompletedTask; },
                () => effects.Add("absent")).GetAwaiter().GetResult();
        }
        Apply(true, true, []);
        Check(effects.SequenceEqual(["add", "verify"]), "Absent registration adds then verifies through the production decision");
        Apply(true, true, [current]);
        Check(effects.SequenceEqual(["verify"]), "Exact replay verifies without reinstalling the running package");
        Apply(false, true, [current]);
        Check(effects.SequenceEqual(["verify", "remove", "absent"]), "Removal verifies the current protected payload before the exact OS effect");
        Apply(false, true, []);
        Check(effects.SequenceEqual(["absent"]), "Absent removal is a verified no-effect replay");
        Apply(true, true, [current with { Directory = root.ToUpperInvariant() + "\\" }]);
        Check(effects.SequenceEqual(["verify"]), "Equivalent directory case and separator remain an exact replay");

        var invalid = new[]
        {
            new[] { current with { Directory = root + "-other" } },
            new[] { current with { Directory = "" } },
            new[] { current with { Directory = @"C:\Users\fixture\writable" } },
            new[] { current with { Publisher = "CN=Other" } },
            new[] { current with { Version = new Version(0, 3, 33, 0) } },
            new[] { current, current }
        };
        foreach (var register in new[] { true, false })
        {
            foreach (var observed in invalid)
            {
                var rejected = false;
                try { Apply(register, true, observed); } catch (IOException) { rejected = true; }
                Check(rejected && effects.Count == 0, (register ? "Add" : "Remove") + " rejects unexpected publisher/version/root/duplicate before any package callback");
            }
            var inactiveRejected = false;
            try { Apply(register, false, [current]); } catch (IOException) { inactiveRejected = true; }
            Check(inactiveRejected && effects.Count == 0, "Changed active-pointer observation rejects before package callbacks");
        }
        var badPayloadRejected = false;
        try { Apply(false, true, [current], false); } catch (IOException) { badPayloadRejected = true; }
        Check(badPayloadRejected && effects.SequenceEqual(["verify"]), "Invalid current payload cannot reach Remove");
        Console.WriteLine($"{count} Explorer registration decisions passed; harmless observations, no package registration or removal.");
        return 0;
    }
}
