using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class TrueSyncContractPreflight
{
    private const int ExpectedCases = 291;
    private const int ExpectedTransitions = 71;
    private const int ExpectedFailureRows = 180;

    public static async Task RunAsync(string contractZipPath, string evidenceRoot, string witnessReceiptPath)
    {
        contractZipPath = Path.GetFullPath(contractZipPath);
        evidenceRoot = Path.GetFullPath(evidenceRoot);
        witnessReceiptPath = Path.GetFullPath(witnessReceiptPath);
        if (!File.Exists(contractZipPath))
            throw new FileNotFoundException("The reviewed architecture contract package is unavailable.", contractZipPath);
        if (!File.Exists(witnessReceiptPath))
            throw new FileNotFoundException("The production witness execution receipt is unavailable.", witnessReceiptPath);
        Directory.CreateDirectory(evidenceRoot);

        using var archive = ZipFile.OpenRead(contractZipPath);
        var caseBytes = ReadEntry(archive, "case-matrix.json");
        var transitionBytes = ReadEntry(archive, "06-state-machines.md");
        var failureBytes = ReadEntry(archive, "09-failure-and-recovery-matrix.csv");
        var findings = new List<string>();

        using var caseDocument = JsonDocument.Parse(caseBytes);
        var caseRoot = caseDocument.RootElement;
        var declaredCaseCount = caseRoot.GetProperty("case_count").GetInt32();
        var cases = caseRoot.GetProperty("cases").EnumerateArray().ToArray();
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var generatedTestIds = new HashSet<string>(StringComparer.Ordinal);
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var requirements = new List<TrueSyncCaseRequirement>(cases.Length);
        foreach (var item in cases)
        {
            var caseId = RequiredString(item, "case_id", findings, "<unknown>");
            if (!CaseIdPattern().IsMatch(caseId)) findings.Add(caseId + ": invalid case ID syntax");
            if (!caseIds.Add(caseId)) findings.Add(caseId + ": duplicate case ID");
            var category = RequiredString(item, "category", findings, caseId);
            categories[category] = categories.GetValueOrDefault(category) + 1;
            var identities = RequiredArray(item, "identities", 3, findings, caseId);
            var events = RequiredArray(item, "events", 1, findings, caseId);
            var allowedEffects = RequiredArray(item, "allowed_effects", 1, findings, caseId);
            var forbiddenEffects = RequiredArray(item, "forbidden_effects", 1, findings, caseId);
            var expectedDatabaseTransitions = RequiredArray(item, "expected_database_transitions", 1, findings, caseId);
            var preconditions = RequiredString(item, "preconditions", findings, caseId);
            var pairMemberships = RequiredString(item, "pair_memberships", findings, caseId);
            var expectedUserResult = RequiredString(item, "expected_user_result", findings, caseId);
            var restartOutcome = RequiredString(item, "restart_outcome", findings, caseId);
            var testIds = item.GetProperty("test_ids").EnumerateArray().Select(value => value.GetString() ?? "")
                .ToArray();
            var expectedTestIds = new[]
            {
                $"TEST-{caseId}-MODEL", $"TEST-{caseId}-CRASH", $"TEST-{caseId}-UI"
            };
            if (testIds.Length != 3 || !expectedTestIds.All(expected => testIds.Contains(expected, StringComparer.Ordinal)))
                findings.Add(caseId + ": generated model/crash/UI test triad is incomplete");
            foreach (var testId in testIds)
                if (!generatedTestIds.Add(testId)) findings.Add(caseId + ": duplicate test ID " + testId);
            requirements.Add(new TrueSyncCaseRequirement(caseId, category,
                RequiredString(item, "title", findings, caseId), preconditions, identities, pairMemberships,
                events, allowedEffects, forbiddenEffects, expectedDatabaseTransitions, expectedUserResult,
                restartOutcome, testIds));
        }

        if (declaredCaseCount != ExpectedCases || cases.Length != ExpectedCases || caseIds.Count != ExpectedCases)
            findings.Add($"case inventory mismatch: declared={declaredCaseCount}, rows={cases.Length}, unique={caseIds.Count}");

        var transitionText = Encoding.UTF8.GetString(transitionBytes);
        var transitionIds = TransitionIdPattern().Matches(transitionText).Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        if (transitionIds.Count != ExpectedTransitions)
            findings.Add($"transition inventory mismatch: expected={ExpectedTransitions}, unique={transitionIds.Count}");

        var failureRows = ParseCsv(Encoding.UTF8.GetString(failureBytes));
        var header = failureRows.Count == 0 ? [] : failureRows[0];
        var failures = failureRows.Skip(1).Where(row => row.Any(value => value.Length != 0)).ToArray();
        var failureTestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in failures)
        {
            if (row.Count != header.Count)
            {
                findings.Add("failure row has a different column count from its header");
                continue;
            }
            if (!caseIds.Contains(row[0])) findings.Add("failure row references unknown case " + row[0]);
            var testId = row[^1];
            if (!FailureIdPattern().IsMatch(testId)) findings.Add("invalid failure test ID " + testId);
            if (!failureTestIds.Add(testId)) findings.Add("duplicate failure test ID " + testId);
        }
        if (failures.Length != ExpectedFailureRows || failureTestIds.Count != ExpectedFailureRows)
            findings.Add($"failure inventory mismatch: expected={ExpectedFailureRows}, rows={failures.Length}, unique={failureTestIds.Count}");

        var repositoryRoot = FindRepositoryRoot();
        var witnessRegistry = TrueSyncWitnessRegistry.Build(requirements, repositoryRoot);
        findings.AddRange(witnessRegistry.Missing);
        var phaseResults = LoadWitnessPhaseResults(witnessReceiptPath, findings);
        var witnessDependencies = TrueSyncWitnessRegistry.ValidateDependencies(requirements, witnessRegistry,
            phaseResults);
        findings.AddRange(witnessDependencies.Missing);
        var contractHash = Hex(SHA256.HashData(await File.ReadAllBytesAsync(contractZipPath)));
        await TrueSyncWitnessRegistry.WriteManifestAsync(
            Path.Combine(evidenceRoot, "requirements-dependencies.json"), contractHash, witnessRegistry,
            witnessDependencies);

        var report = new
        {
            contract_sha256 = contractHash,
            contract_entries = new
            {
                case_matrix_sha256 = Hex(SHA256.HashData(caseBytes)),
                state_machines_sha256 = Hex(SHA256.HashData(transitionBytes)),
                failure_matrix_sha256 = Hex(SHA256.HashData(failureBytes))
            },
            cases_expected = ExpectedCases,
            cases_parsed = cases.Length,
            cases_structurally_mapped_to_production_witnesses = witnessRegistry.Mappings.Count,
            generated_case_test_ids_declared = generatedTestIds.Count,
            generated_case_tests_executed = 0,
            case_specific_execution_claim = false,
            structural_dependency_mapping_complete = witnessDependencies.Missing.Count == 0 &&
                                                     witnessRegistry.Missing.Count == 0,
            validation_layer = "contract parsing and structural dependency mapping only; named phase receipts are validated separately and do not constitute 873 case-specific executions",
            witness_profiles = witnessRegistry.ProfileCounts,
            categories,
            transitions_expected = ExpectedTransitions,
            transitions_parsed = transitionIds.Count,
            failure_rows_expected = ExpectedFailureRows,
            failure_rows_parsed = failures.Length,
            missing = findings,
            unregistered_test_methods = Array.Empty<string>()
        };
        var reportPath = Path.Combine(evidenceRoot, "contract-coverage.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (findings.Count != 0)
            throw new InvalidDataException("The reviewed true-sync contract inventory has coverage gaps: " +
                                           string.Join("; ", findings.Take(10)));
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
                    ?? throw new InvalidDataException("The reviewed contract package is missing " + name + ".");
        using var source = entry.Open();
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SyncShark.sln")))
            current = current.Parent;
        return current?.FullName ??
               throw new InvalidOperationException("The Sync Shark repository root is unavailable.");
    }

    private static string RequiredString(JsonElement item, string property, List<string> findings, string caseId)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            findings.Add(caseId + ": missing " + property);
            return "";
        }
        return value.GetString()!;
    }

    private static string[] RequiredArray(JsonElement item, string property, int minimum, List<string> findings,
        string caseId)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() < minimum || value.EnumerateArray().Any(entry =>
                entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString())))
        {
            findings.Add(caseId + ": incomplete " + property);
            return [];
        }
        return value.EnumerateArray().Select(entry => entry.GetString()!).ToArray();
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"') quoted = false;
                else field.Append(character);
                continue;
            }
            if (character == '"') quoted = true;
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(character);
        }
        if (field.Length != 0 || row.Count != 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        if (quoted) throw new InvalidDataException("The failure matrix ends inside a quoted CSV field.");
        return rows;
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    private static IReadOnlyDictionary<string, bool> LoadWitnessPhaseResults(string path, List<string> findings)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty("phases", out var phaseArray) ||
            phaseArray.ValueKind != JsonValueKind.Array)
        {
            findings.Add("production witness receipt has no phases array");
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        var receiptRoot = Path.GetDirectoryName(path)
                          ?? throw new InvalidOperationException("The witness receipt has no parent directory.");
        foreach (var phase in phaseArray.EnumerateArray())
        {
            var name = RequiredString(phase, "name", findings, "witness receipt");
            var exitCode = phase.TryGetProperty("exit_code", out var exitValue) &&
                           exitValue.TryGetInt32(out var parsedExit) ? parsedExit : int.MinValue;
            var logHash = RequiredString(phase, "log_sha256", findings, name);
            if (logHash.Length != 64 || logHash.Any(character => !Uri.IsHexDigit(character)))
                findings.Add(name + ": production witness log hash is invalid");
            var logName = RequiredString(phase, "log", findings, name);
            var logPath = Path.GetFullPath(Path.Combine(receiptRoot, logName));
            if (!string.Equals(Path.GetDirectoryName(logPath), receiptRoot,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(logPath))
                findings.Add(name + ": production witness log is unavailable or escaped its evidence root");
            else if (!string.Equals(Hex(SHA256.HashData(File.ReadAllBytes(logPath))), logHash,
                         StringComparison.OrdinalIgnoreCase))
                findings.Add(name + ": production witness log hash does not match the retained log");
            if (!results.TryAdd(name, exitCode == 0))
                findings.Add(name + ": duplicate production witness phase receipt");
        }
        foreach (var required in new[]
                 {
                     "architecture-boundaries", "sqlite-authority", "true-sync-functional",
                     "true-sync-process-crash", "complete-test-registry"
                 })
            if (!results.GetValueOrDefault(required))
                findings.Add(required + ": production witness phase did not pass");
        return results;
    }

    [GeneratedRegex("^[A-Z]{2,4}-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseIdPattern();

    [GeneratedRegex("^\\|\\s*(SM[0-9]{2}-T[0-9]{2})\\s*\\|", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TransitionIdPattern();

    [GeneratedRegex("^FAULT-(SM[0-9]{2}-T[0-9]{2}-(CRASH|ADVERSE)|X-[0-9]{2}-(CRASH|RACE))$", RegexOptions.CultureInvariant)]
    private static partial Regex FailureIdPattern();
}
