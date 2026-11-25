"""Small isolated regression tests; python -I tools/ci/test-spec-ratchet.py."""
import contextlib
import importlib.util
import io
import hashlib
import os
import json
from pathlib import Path
import re
import tempfile
import unittest
import sys
from unittest import mock

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('ratchet', HERE / 'check-spec-ratchet.py')
ratchet = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ratchet)


def long_method(name='Run', extra=0):
    return '    public void ' + name + '()\n    {\n' + '        Work();\n' * (41 + extra) + '    }\n'


def artifact_rows(tree, root):
    for path in ratchet.identity.source_files(root):
        source = path.read_text(encoding='utf-8-sig')
        spans = []
        ratchet.lex.mask(source, spans)
        sites, counts = [], {}
        for start, end in sorted(spans):
            line, text = source.count('\n', 0, start) + 1, source[start:end]
            key = (line, text)
            ordinal = counts.get(key, 0)
            counts[key] = ordinal + 1
            sites.append(dict(line=line, text=text, ordinal=ordinal, permitted=False))
        yield dict(tree=tree, path=path.relative_to(root).as_posix(),
                   hash=hashlib.sha256(source.encode()).hexdigest(), sites=sites)


class RatchetChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='spec-ratchet-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'src').mkdir()
        self.source = self.root / 'src' / 'Example.cs'
        self.baseline = self.root / 'baseline.jsonl'
        self.put(long_method())
        with self.baseline.open('w', encoding='utf-8', newline='\n') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)

        self.seed_identity()

    def put(self, members):
        self.source.write_text('namespace Example;\npublic class Example\n{\n' + members + '}\n', encoding='utf-8')

    def check(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = ratchet.compare(self.root, self.baseline)
        return code, output.getvalue()

    def test_tunable_identifier_words(self):
        for name, count in (('examined', 0), ('terminal', 0), ('minimum', 1), ('MinDelay', 1), ('max_min', 1), ('RetryMin', 1),
                            ('RootVerifyAttempts', 1), ('RestoredEvidenceReleaseAttempts', 1),
                            ('VerificationPublicationPages', 1), ('_ordinaryHistoryMaintenanceBatches', 1), ('pages', 1)):
            with self.subTest(name=name):
                self.assertEqual(count, ratchet.tunable_count('int ' + name + ' = 0;', Path('Example.cs')))

    def test_protocol_fact_defaults(self):
        path = Path('src/SyncShark.Engine/Dropbox/ConnectionListFormat.cs')
        source = 'class ConnectionListFormat { private const int MaximumAppendRecords = 100; }'
        self.assertEqual(0, ratchet.tunable_count(source, path))
        self.assertEqual(1, ratchet.tunable_count(source, Path('Example.cs')))
        self.assertEqual(1, ratchet.tunable_count(source.replace('MaximumAppendRecords', 'RetryLimit'), path))
        for value in ('22', '23'):
            with self.subTest(value=value):
                self.assertEqual(1, ratchet.tunable_count('class StateSchema { private const int PlanDepthColumn = ' + value + '; }',
                                                          Path('src/SyncShark.Engine/Storage/StateSchema.cs')))

    def test_listed_protocol_fact_is_bound_to_its_declaration(self):
        # A fact names its file, owner type chain, member, attribute argument or method local, and exact tokens (B3).
        journal = Path('src/SyncShark.Engine/Local/JournalDevice.cs')
        source = 'class JournalDevice { private const int JournalStateBytes = 56; }'
        self.assertEqual(0, ratchet.tunable_count(source, journal))
        for path, text in ((Path('src/SyncShark.Engine/Other/JournalDevice.cs'), source),  # same basename, other folder
                           (Path('Example.cs'), source),
                           (journal, source.replace('56', '8192')),  # same declaration, changed value
                           (journal, source.replace('56', '(56)')),  # same value, other tokens
                           (journal, source.replace('JournalStateBytes', 'JournalPageBytes')),  # not the member
                           (journal, source.replace('class JournalDevice', 'class Journal')),  # not the owner
                           # a second same-named local in another method, and a same-named member of a nested type
                           (journal, source.replace('}', 'void Apply() { int JournalStateBytes = 56; ApplyLimit(JournalStateBytes); } }')),
                           (journal, source.replace('}', 'class Probe { const int JournalStateBytes = 56; } }'))):
            with self.subTest(path=path, text=text):
                self.assertEqual(1, ratchet.tunable_count(text, path))
        unsourced = {(journal.as_posix(), 'JournalDevice.JournalStateBytes'): ('56', ' ')}
        self.assertEqual(1, ratchet.tunable_count(source, journal, facts=unsourced))
        process = Path('src/SyncShark.Engine/Local/InUse.cs')
        field = ('class InUse { private struct ProcessInfo {\n    [MarshalAs(UnmanagedType.ByValTStr,\n'
                 '        SizeConst = 256)]\n    public string Name; } }')  # a multiline attribute
        self.assertEqual(0, ratchet.tunable_count(field, process))
        for text in (field.replace('Name;', 'Title;'), field.replace('256', '64'), field.replace('ProcessInfo', 'Other'),
                     field.replace('MarshalAs(', 'Marshal('), field.replace('public string Name;', 'void Run() { }')):
            with self.subTest(text=text):
                self.assertEqual(1, ratchet.tunable_count(text, process))
        layout = Path('src/SyncShark.Engine/Local/IdentityPaths.cs')
        listing = 'class DirectoryListing { private const int MaxNameCharacters = 255; }'
        self.assertEqual(0, ratchet.tunable_count(listing, Path('src/SyncShark.Engine/Local/DirectoryListing.cs')))
        self.assertEqual(1, ratchet.tunable_count('class IdentityPaths { private const int MaximumNameCharacters = 255; }', layout))
        descriptor = 'class IdentityPaths { [StructLayout(LayoutKind.Explicit, Size = 24)] private struct FileIdDescriptor { } }'
        self.assertEqual(0, ratchet.tunable_count(descriptor, layout))
        self.assertEqual(1, ratchet.tunable_count(descriptor.replace('FileIdDescriptor', 'IoStatusBlock'), layout))
        side = Path('src/SyncShark.Engine/Local/LocalSide.cs')
        digest = ('class LocalSide { internal static async Task<string> Digest(Stream input,Stream? output,CancellationToken ct)'
                  ' { const int readBufferBytes=128*1024; Use(readBufferBytes); } }')
        # R123: a method local counts unless independently approved by an exact-site receipt.
        nested = digest.replace('const int readBufferBytes=128*1024; Use(readBufferBytes);',
                                'static (int Bytes, bool Allowed) RecoveryCap() { const int readBufferBytes = 128*1024; '
                                'return (readBufferBytes, true); } Use(RecoveryCap());')
        for text in (digest, nested, digest.replace('Digest(', 'Hash(')):
            with self.subTest(text=text):
                self.assertEqual(1, ratchet.tunable_count(text, side))
        self.assertTrue(all(isinstance(path, str) and path.startswith('src/') and '.' in name and value.strip() and source.strip()
                            for (path, name), (value, source) in ratchet.PROTOCOL_FACTS.items()))

    def test_numbers_count_without_inferred_roles(self):
        # R124/R127: no index, bracket, accumulator-seed or one-holder role is inferred; a real mutex needs a receipt.
        native = 'InitializeProcThread' 'AttributeList'  # the guard refuses the whole 33-character name in source
        probe = 'static extern bool ' + native + '(nint list, int count, int flags, ref nint size); '
        for source, count in (('_ = new SemaphoreSlim(1);', 1), ('_ = new SemaphoreSlim(1, 1);', 1),
                              # R122: +/- adjacency proves nothing, so a terminator and a policy's extra slot both count.
                              ('var bufferBytes = length + 1;', 1), ('var batchSize = available + 1; return batchSize;', 1),
                              ('items.Take(available - 1);', 1),
                              # B2 round 4: a call's spelling or a slice bound proves no role here, so these count, a
                              # typed reader included (no receiver type in this checker), and so do same-spelled policies.
                              ('items.Skip(1);', 1), ('var size = header.AsSpan(1);', 1), ('var payload = message[1..];', 1),
                              ('var bufferBytes = buffer.AsMemory(0, chunk);', 1), ('var size = reader.GetInt64(1);', 1),
                              ('SqliteDataReader row = cmd.ExecuteReader(); var size = row.GetInt64(0);', 1),
                              ('var batchSize = policy.GetInt64(1);', 1), ('var size = rows.Skip(1);', 2),
                              ('var entries = Read(path.Length == 0 ? a : b);', 1),
                              # R123: a comparison proves no role, arithmetic beside it included.
                              ('var batchSize = available - 1 >= requested ? requested : unavailable;', 1),
                              ('items.Take(available - 1 >= requested ? requested : unavailable);', 1),
                              ('items.Take(count - 1);', 1),
                              # R124 (Sol r6 R6-3): the whole-index and zero-seed allowances are deleted, so an index, a
                              # bracketed allocation size and an accumulator seed that is a real fallback all count.
                              ('var chunkBytes = buffer[0];', 1),
                              ('var batchSize = (new Dictionary<int,string>[1]).Length;', 1),
                              ('int batchSize = 0; if (enabled) batchSize += configuredSize; return batchSize;', 1),
                              ('long copyBytes = 0, removeBytes = 0; copyBytes += a; removeBytes = checked(removeBytes + b);', 1),
                              ('items.Skip(2);', 1), ('Skip(1);', 1), ('var bufferBytes = length + 2;', 1),
                              ('int minimum = 0;', 1), ('long copyBytes = 0;', 1), ('long copyBytes = 0; Use(copyBytes);', 1),
                              ('nint size = 0; Use(size);', 1), ('bytesReturned = 0;', 1),
                              ('const int RetryLimit = (1);', 1), ('var batchSize = 1 + 1;', 1),
                              ('items.Limit(1);', 1), ('items.Retry(-1);', 1), ('var batchSize = new byte[1];', 1),
                              ('var limit = group.Count == 1 ? 1 : max;', 1), ('var batchSize = Math.Min(max, 1);', 2),
                              ('var batchSize = Policy.CapAt(1, available);', 1),  # an unlisted call's first argument
                              ('var batchSize = candidates.Take(1);', 2), ('items.Take(1);', 1),  # Take is a cap
                              ('var size = rows.Pick(1);', 1), ('var gate = Gates.SemaphoreSlim(1);', 1),
                              ('int retryLimit = 0; Adjust(ref retryLimit);', 1), ('nint size = 0; Probe(0, ref size);', 1),
                              ('nint size = 0; ' + native + '(0, 1, 0, ref size);', 1),
                              ('Task.Delay(1);', 1), ('TimeSpan.FromSeconds(1);', 1), ('items.WaitAsync(1);', 1),
                              ('DelaySeconds = 1;', 1), ('var DelaySeconds = x + 1;', 1), ('var pollInterval = x - 1;', 1)):
            with self.subTest(source=source):
                self.assertEqual(count, ratchet.tunable_count('class Example { void Run() { ' + source + ' } }', Path('Example.cs')))
        for source, count in (('void Read(out int bytesReturned) { bytesReturned = 0; }', 1),  # an out result's zero
                              ('void A() { long copyBytes = 0; } void B() { copyBytes += a; }', 1),
                              (probe + 'void Launch() { nint size = 0; ' + native + '(0, 1, 0, ref size); }', 1),
                              # Sol r7 R7-6: a project type spelled SemaphoreSlim whose argument is a batch count.
                              ('class SemaphoreSlim { public SemaphoreSlim(int batch) { } } '
                               'void Run() { var limiter = new SemaphoreSlim(1); }', 1)):
            with self.subTest(source=source):
                self.assertEqual(count, ratchet.tunable_count('class Example { ' + source + ' }', Path('Example.cs')))

    def test_reviewed_finding_receipt_is_bound_to_its_exact_site(self):
        # R124: a FALSE-POSITIVE receipt keyed [path, category, owner, Member.key()] with the site hash, its occurrence
        # among the same text in that member and the hash of the whole enclosing member clears exactly one site, in the
        # scan and in the trusted findings; a copy, any edit to that member, a renamed member or a blank reason counts.
        line = 'Console.WriteLine("Please retry this operation.");'

        def body(*statements, name='Run'):
            return '    void ' + name + '() { ' + ' '.join(statements) + ' }\n'

        def scanned():
            return {r[0][1]: r[1] for r in ratchet.scan(self.root)}.get('user-strings', 0)

        self.put(body(line))
        self.assertEqual((1, 1), (scanned(), self.trusted_strings()))
        self.receipt('Reviewed text.')
        self.assertEqual((0, 0), (scanned(), self.trusted_strings()))
        copy = 'Console.Error.WriteLine("Please retry this operation.");'
        for members, count in ((body(line, copy), 2),  # a copy edits the member: both count
                               (body(copy, line), 2),
                               (body(copy), 1),  # moved to another use
                               (body(line.replace(');', ', 1);')), 1),  # changed use
                               (body(line, 'Work();'), 1),  # any other edit to the member
                               (body(line, name='Load'), 1),  # renamed member
                               (body(line) + body('Work();', name='Load'), 0)):  # another member's edit leaves it bound
            with self.subTest(members=members):
                self.put(members)
                self.assertEqual((count, count), (scanned(), self.trusted_strings()))
        self.put(body(line))
        self.receipt(' ')
        self.assertEqual((1, 1), (scanned(), self.trusted_strings()))
        # Sol r6 R6-1: the literal's statement is unchanged and only its use changes.
        declared, shown = 'var note = "Please retry this operation.";', 'Console.WriteLine(note);'
        self.put(body(declared, shown))
        self.receipt('Reviewed text.')
        self.assertEqual((0, 0), (scanned(), self.trusted_strings()))
        self.put(body(declared, shown.replace('Console.', 'Console.Error.')))
        self.assertEqual((1, 1), (scanned(), self.trusted_strings()))

    def test_numeric_receipt_is_bound_to_its_exact_site(self):
        # R138 (Sol r10 R10-2): the counted readBufferBytes assignment of LocalSide.Digest. A magic-tunables receipt
        # clears exactly that one site in the scan and in the trusted findings; the same assignment in another member
        # still counts, and a different use in Digest, its declaration unchanged, counts again.
        digest = ('    internal static async Task<string> Digest(Stream input, CancellationToken ct)'
                  ' {{ const int readBufferBytes = 128 * 1024; {}(readBufferBytes); }}\n')
        load = '    void Load() { const int readBufferBytes = 128 * 1024; Use(readBufferBytes); }\n'

        def counts():
            scanned = {r[0][1]: r[1] for r in ratchet.scan(self.root)}.get('magic-tunables', 0)
            return scanned, self.trusted_strings(category='magic-tunables')

        self.put(digest.format('Use'))
        self.assertEqual((1, 1), counts())
        self.receipt('Reviewed buffer size.', 'magic-tunables')
        self.assertEqual((0, 0), counts())
        self.put(digest.format('Use') + load)  # one receipt, two identical sites: only Digest's is cleared
        self.assertEqual((1, 1), counts())
        self.put(digest.format('Other'))  # the use changes while the declaration is unchanged
        self.assertEqual((1, 1), counts())

    def trusted_strings(self, sink=None, category='user-strings'):
        """Trusted findings of category (user-strings by default); sink, when given, is reported for every literal site."""
        artifact = self.root / 'literal-sites.jsonl'
        rows = list(artifact_rows('candidate', self.root))
        for row in rows:
            for site in row['sites']:
                site['sink'] = sink
        artifact.write_text(''.join(json.dumps(row) + '\n' for row in rows), encoding='utf-8')
        with contextlib.closing(ratchet.trust.sqlite3.connect(':memory:')) as connection:
            ratchet.trust.literal_sites.load(connection, artifact)
            found = [row for row in ratchet.trust.findings(self.root, ratchet, connection, 'candidate')
                     if row[0] == category]
            permitted = [ratchet.trust.literal_sites.permitted(connection, 'candidate', row['path']) for row in rows]
        artifact.unlink()
        if sink is not None:
            self.assertEqual(len(found) == 0, any(permitted))  # the scan's permissions decide alike
        return len(found)

    def ledger(self, *rows):
        ledger = self.root / ratchet.reviews.LEDGER
        ledger.parent.mkdir(parents=True, exist_ok=True)
        ledger.write_text(json.dumps({'version': 1, 'verdicts': list(rows), 'register_reviews': []}), encoding='utf-8')

    def receipt(self, reason, category='user-strings'):
        """One receipt for the only counted site of category in src/Example.cs as it is now."""
        source = self.source.read_text(encoding='utf-8')
        spans = []
        masked = ratchet.lex.mask(source, spans)
        sites = []
        if category == 'user-strings':
            ratchet.text_count(source, masked, spans, Path('src/Example.cs'), lambda a, b: sites.append((a, b)))
        else:
            ratchet.tunable_count(masked, Path('src/Example.cs'), lambda a, b: sites.append((a, b)))
        (_, identity), = ratchet.site_identities(source, masked, 'src/Example.cs', category, sites,
                                                 ratchet.const_readers(self.root))
        self.bound({'key': list(identity[:4]), 'hash': identity[4], 'occurrence': identity[5], 'use': identity[6],
                    'size': 1, 'members': [], 'verdict': 'FALSE-POSITIVE', 'family': 'openai', 'reason': reason})

    def bound(self, *rows):
        """The ledger with every user-strings receipt and sink row bound to the rows' sink set (R127, R7-2)."""
        current = ratchet.sink_set(self.root, rows)
        self.ledger(*(dict(row, sinks=current) if row['key'][1] in ('user-strings', ratchet.reviews.SINK) else row
                      for row in rows))
        return current

    def storage_example(self):
        storage = self.root / 'src' / 'SyncShark.Engine' / 'Storage'
        storage.mkdir(parents=True)
        self.source.unlink()
        self.source = storage / 'Example.cs'
        return 'src/SyncShark.Engine/Storage/Example.cs'

    def sink_row(self, relative, name, parameter, symbol,
                 reason='parameter {} is used only as SQL text: its one use is the CommandText assignment.'):
        source = self.source.read_text(encoding='utf-8')
        member, = [m for m in ratchet.member_index(ratchet.lex.mask(source, []), Path(relative))[0]
                   if m.name == name and m.params == symbol[1]]
        return {'key': [relative, ratchet.reviews.SINK, member.owner, member.key()],
                'hash': ratchet.reviews.content_hash(source[member.start:member.end], ratchet.lex),
                'parameter': name + '.' + parameter, 'symbol': symbol[0], 'size': 1, 'members': [],
                'verdict': 'FALSE-POSITIVE', 'family': 'anthropic+openai', 'reason': reason.format(parameter)}

    def test_sql_sink_receipt_is_bound_to_the_whole_helper(self):
        # R124/R127: an argument the literals owner reports as bound to a method parameter, [path, identifier offset,
        # documentation ID, parameter], is SQL only through a sink receipt naming that symbol and bound to that
        # method's whole member hash with the reason 'parameter <p> is used only as SQL text'.
        relative = self.storage_example()
        helper = '    static void Run(SqliteCommand cmd, string text)\n    {\n        cmd.CommandText = text;\n    }\n'
        # Sol r6 R6-2: one parameter that is display text or SQL by mode.
        dual = ('    static void Run(SqliteCommand cmd, string text)\n    {\n'
                '        if (inspect) present(text); else cmd.CommandText = text;\n    }\n')
        call = '    void M(SqliteCommand cmd) => Run(cmd, "Please inspect the saved item.");\n'
        symbol = 'M:Example.Example.Run(Microsoft.Data.Sqlite.SqliteCommand,System.String)'

        def reported(parameter='text', name=symbol, path=relative):
            source = self.source.read_text(encoding='utf-8')
            return [path, source.index('Run('), name, parameter]

        self.put(helper + call)
        self.assertEqual(1, self.trusted_strings(reported()))  # no sink receipt
        reviewed = self.sink_row(relative, 'Run', 'text', (symbol, ['SqliteCommand', 'string']))
        self.bound(reviewed)
        self.assertEqual(0, self.trusted_strings(reported()))
        self.assertEqual(1, self.trusted_strings())  # not reported as bound to the helper
        for site in (reported('cmd'), reported(name=symbol.replace('Run', 'Other')),
                     reported(path='src/SyncShark.Engine/Storage/Other.cs'),
                     [relative, len(self.source.read_text(encoding='utf-8')) - 3, symbol, 'text']):  # outside Run
            with self.subTest(site=site):
                self.assertEqual(1, self.trusted_strings(site))
        for row in (dict(reviewed, reason='Reviewed helper.'), dict(reviewed, parameter='Run.cmd'),
                    dict(reviewed, parameter='Other.text'), dict(reviewed, verdict='REASON-RECORDED'),
                    dict(reviewed, symbol=symbol.replace('Run', 'Other'))):
            with self.subTest(row=row):
                self.bound(row)
                self.assertEqual(1, self.trusted_strings(reported()))
        self.bound(reviewed)
        self.ledger(dict(reviewed, sinks='0' * 12 + ('~' + '0' * 12) * 4 + '~0000'))  # another sink set
        self.assertEqual(1, self.trusted_strings(reported()))
        self.bound(reviewed)
        self.put(dual + call)  # the helper body changed to the dual-use form: the sink is void
        self.assertEqual(1, self.trusted_strings(reported()))
        self.put(helper + call.replace('M(', 'N('))  # R134 (B2): the sink binds its whole file, a caller there included
        self.assertEqual(1, self.trusted_strings(reported()))
        self.put(helper.replace('    {', '#if DEBUG\n    {\n#endif', 1) + call)  # R7-4: a directive in the sink
        self.assertEqual(1, self.trusted_strings(reported()))

    def test_same_line_overload_does_not_share_the_reviewed_sink(self):
        # Sol r7 R7-3: an unreviewed overload declared after the reviewed method's closing brace, on the same line, is
        # another symbol at another offset; neither its symbol nor its identifier offset matches the reviewed row.
        relative = self.storage_example()
        reviewed_text = '    static void Run(SqliteCommand cmd, string text) { cmd.CommandText = text; }'
        overload = ' static void Run(SqliteCommand cmd, string text, int n) { present(text); }\n'
        call = '    void M(SqliteCommand cmd) => Run(cmd, "Please inspect the saved item.", 1);\n'
        symbol = 'M:Example.Example.Run(Microsoft.Data.Sqlite.SqliteCommand,System.String)'
        other = 'M:Example.Example.Run(Microsoft.Data.Sqlite.SqliteCommand,System.String,System.Int32)'
        self.put(reviewed_text + overload + call)
        self.bound(self.sink_row(relative, 'Run', 'text', (symbol, ['SqliteCommand', 'string'])))
        source = self.source.read_text(encoding='utf-8')
        second = source.index('Run(', source.index('Run(') + 1)
        for site in ([relative, second, other, 'text'], [relative, second, symbol, 'text']):
            with self.subTest(site=site):
                self.assertEqual(1, self.trusted_strings(site))
        self.assertEqual(0, self.trusted_strings([relative, source.index('Run('), symbol, 'text']))

    def test_any_sink_edit_voids_every_sink_and_string_receipt(self):
        # R127 (Sol r7 R7-2): Write delegates to Create; Create's body changes while Write, the direct Create call and
        # the receipted fragment stay unchanged. The sink set changes, so all of them count again.
        relative = self.storage_example()
        create = '    static void Create(SqliteCommand cmd, string sql) { cmd.CommandText = sql; }\n'
        write = '    static void Write(SqliteCommand cmd, string sql) { Create(cmd, sql); cmd.ExecuteNonQuery(); }\n'
        calls = ('    void M(SqliteCommand cmd) => Write(cmd, "Please inspect the saved item.");\n'
                 '    void N(SqliteCommand cmd) { var sql = "Please inspect the saved item now."; Create(cmd, sql); }\n')
        types = ['SqliteCommand', 'string']
        created = ('M:Example.Example.Create(Microsoft.Data.Sqlite.SqliteCommand,System.String)', types)
        written = ('M:Example.Example.Write(Microsoft.Data.Sqlite.SqliteCommand,System.String)', types)
        self.put(create + write + calls)
        source = self.source.read_text(encoding='utf-8')
        sinks = [self.sink_row(relative, 'Create', 'sql', created), self.sink_row(relative, 'Write', 'sql', written)]
        spans = []
        masked = ratchet.lex.mask(source, spans)
        sites = []
        ratchet.text_count(source, masked, spans, Path(relative), lambda a, b: sites.append((a, b)))
        found, = [i for _, i in ratchet.site_identities(source, masked, relative, 'user-strings', sites)
                  if i[3].startswith('N(')]
        fragment = {'key': list(found[:4]), 'hash': found[4], 'occurrence': found[5], 'use': found[6], 'size': 1,
                    'members': [], 'verdict': 'FALSE-POSITIVE', 'family': 'openai', 'reason': 'Reviewed SQL.'}
        self.bound(*sinks, fragment)

        def at_write():
            return [relative, self.source.read_text(encoding='utf-8').index('Write('), written[0], 'sql']
        self.assertEqual(1, self.trusted_strings())  # N's fragment is cleared by its receipt; M's reports no sink here
        self.assertEqual(0, self.trusted_strings(at_write()))  # and M's literal through the Write sink
        # R138 (Sol r10 R10-1): only the downstream Create row becomes ineligible, its source and the upstream Write row
        # unchanged: a reason that does not assert SQL-only use (the set digest is unchanged), or a valid but stale
        # sink-set digest. The whole set is rejected, so the fragment and the literal through Write both count.
        for ineligible in (dict(sinks[0], reason='Reviewed helper.'),
                           dict(sinks[0], sinks='0' * 12 + ('~' + '0' * 12) * 4 + '~0000')):
            with self.subTest(create=ineligible):
                current = self.bound(*sinks, fragment)
                rows = [r for r in ratchet.ledger_rows(self.root) if r['key'][3] != sinks[0]['key'][3]]
                self.ledger(*rows, ineligible if 'sinks' in ineligible else dict(ineligible, sinks=current))
                self.assertEqual(current, ratchet.sink_set(self.root, ratchet.ledger_rows(self.root)))
                self.assertEqual((2, 2), (self.trusted_strings(), self.trusted_strings(at_write())))
        wrong = self.sink_row(relative, 'Create', 'sq1', created)
        self.bound(wrong, sinks[1], fragment)
        self.assertEqual((2, 2), (self.trusted_strings(), self.trusted_strings(at_write())))
        self.bound(*sinks, fragment)
        self.put(create.replace('cmd.CommandText = sql;', 'present(sql);') + write + calls)  # only Create changes
        self.assertIsNone(ratchet.sink_set(self.root, ratchet.ledger_rows(self.root)))
        self.assertEqual((2, 2), (self.trusted_strings(), self.trusted_strings(at_write())))

    def test_overload_added_to_a_sink_file_voids_every_sink_and_string_receipt(self):
        # R134 (Sol r8 B2), R135 (Sol r9 R9-3): the reviewed Create takes object; a more specific string overload is
        # added to the sink's file. The callers and both reviewed bodies are unchanged, but both existing string calls
        # (Write's and N's) now bind the new overload, so the sink set changes and the receipted fragment and the
        # literal through Write count again.
        relative = self.storage_example()
        create = '    static void Create(SqliteCommand cmd, object sql) { cmd.CommandText = (string)sql; }\n'
        overload = '    static void Create(SqliteCommand cmd, string sql) { present(sql); }\n'
        write = '    static void Write(SqliteCommand cmd, string sql) { Create(cmd, sql); cmd.ExecuteNonQuery(); }\n'
        calls = ('    void M(SqliteCommand cmd) => Write(cmd, "Please inspect the saved item.");\n'
                 '    void N(SqliteCommand cmd) { var sql = "Please inspect the saved item now."; Create(cmd, sql); }\n')
        types = ['SqliteCommand', 'string']
        created = ('M:Example.Example.Create(Microsoft.Data.Sqlite.SqliteCommand,System.Object)', ['SqliteCommand', 'object'])
        written = ('M:Example.Example.Write(Microsoft.Data.Sqlite.SqliteCommand,System.String)', types)
        self.put(create + write + calls)
        source = self.source.read_text(encoding='utf-8')
        sinks = [self.sink_row(relative, 'Create', 'sql', created), self.sink_row(relative, 'Write', 'sql', written)]
        spans = []
        masked = ratchet.lex.mask(source, spans)
        sites = []
        ratchet.text_count(source, masked, spans, Path(relative), lambda a, b: sites.append((a, b)))
        found, = [i for _, i in ratchet.site_identities(source, masked, relative, 'user-strings', sites)
                  if i[3].startswith('N(')]
        fragment = {'key': list(found[:4]), 'hash': found[4], 'occurrence': found[5], 'use': found[6], 'size': 1,
                    'members': [], 'verdict': 'FALSE-POSITIVE', 'family': 'openai', 'reason': 'Reviewed SQL.'}
        bound = self.bound(*sinks, fragment)

        def at_write():
            return [relative, self.source.read_text(encoding='utf-8').index('Write('), written[0], 'sql']
        self.assertEqual((1, 0), (self.trusted_strings(), self.trusted_strings(at_write())))
        self.put(create + overload + write + calls)  # only the overload is added; callers and old bodies unchanged
        self.assertNotIn(ratchet.sink_set(self.root, ratchet.ledger_rows(self.root)), (None, bound))
        self.assertEqual((2, 2), (self.trusted_strings(), self.trusted_strings(at_write())))

    def test_const_receipt_is_private_and_bound_to_the_whole_file(self):
        # R127 (Sol r7 R7-1): a declaration-only receipt is allowed only for a private const and binds the whole
        # declaring file, so a new consumer in another member counts while the declaration is unchanged; a non-private
        # const, a field, a partial type and a file with a directive hold no receipt.
        const = '    private const string Note = "Please retry this operation.";\n'
        use = '    void Run() { Execute(Note); }\n'

        def scanned():
            return {r[0][1]: r[1] for r in ratchet.scan(self.root)}.get('user-strings', 0)

        self.put(const + use)
        self.receipt('Reviewed text.')
        self.assertEqual((0, 0), (scanned(), self.trusted_strings()))
        self.put(const + use + '    void Show() { Console.WriteLine(Note); }\n')  # Sol's new consumer
        self.assertEqual((1, 1), (scanned(), self.trusted_strings()))
        for members in (const.replace('private', 'internal') + use, const.replace('const', 'static readonly') + use,
                        const + use + '#if DEBUG\n    void Show() { }\n#endif\n'):
            with self.subTest(members=members):
                self.put(members)
                self.assertEqual([], self.identities())
        self.source.write_text('namespace Example;\npublic partial class Example\n{\n' + const + use + '}\n', encoding='utf-8')
        self.assertEqual([], self.identities())

    def test_const_receipt_binds_every_file_that_names_the_const(self):
        # R134 (Sol r8 B1): a test reads the private const by name (GetField("Note")) and passes the value on. Adding
        # that reader, or editing it after a receipt that binds it, re-counts the const while its own file is unchanged.
        const = '    private const string Note = "Please retry this operation.";\n'
        use = '    void Run() { Execute(Note); }\n'
        tests = self.root / 'tests'
        tests.mkdir()
        reader = tests / 'ExampleChecks.cs'
        read = ('class ExampleChecks {{ static string Note() => (string)typeof(Example).GetField("Note", Flags)!'
                '.GetRawConstantValue()!; void Run() {{ {} }} }}\n')

        def scanned():
            return {r[0][1]: r[1] for r in ratchet.scan(self.root)}.get('user-strings', 0)

        self.put(const + use)
        self.receipt('Reviewed text.')
        self.assertEqual((0, 0), (scanned(), self.trusted_strings()))
        for text in (read.format('Execute(Note());'), 'class Other { string N => nameof(Note); }\n'):
            with self.subTest(reader=text):
                reader.write_text(text, encoding='utf-8')  # a new reader
                self.assertEqual((1, 1), (scanned(), self.trusted_strings()))
        reader.write_text(read.format('Execute(Note());'), encoding='utf-8')
        self.receipt('Reviewed text; ExampleChecks reads it into Execute.')  # the receipt now binds the reader
        self.assertEqual((0, 0), (scanned(), self.trusted_strings()))
        reader.write_text(read.format('Console.WriteLine(Note());'), encoding='utf-8')  # the reader's use changes
        self.assertEqual((1, 1), (scanned(), self.trusted_strings()))

    def test_member_with_a_directive_holds_no_receipt(self):
        # R127 (Sol r7 R7-4): flipping #if false to #if true swaps the use without changing the member's hash, so a
        # member holding any directive has no receipt identity and its literal counts.
        guarded = ('    void Run() { var note = "Please retry this operation.";\n#if {}\n        cmd.CommandText = note;\n'
                   '#else\n        present(note);\n#endif\n    }\n')
        self.put(guarded.replace('{}', 'false'))
        self.assertEqual([], self.identities())
        self.put(guarded.replace('{}', 'false').replace('#if false\n', '').replace('#else\n', '').replace('#endif\n', ''))
        self.receipt('Reviewed text.')
        self.assertEqual(0, self.trusted_strings())
        for flag in ('false', 'true'):
            with self.subTest(flag=flag):
                self.put(guarded.replace('{}', flag))
                self.assertEqual(1, self.trusted_strings())

    def identities(self):
        source = self.source.read_text(encoding='utf-8')
        spans = []
        masked = ratchet.lex.mask(source, spans)
        sites = []
        ratchet.text_count(source, masked, spans, Path('src/Example.cs'), lambda a, b: sites.append((a, b)))
        self.assertTrue(sites)
        return list(ratchet.site_identities(source, masked, 'src/Example.cs', 'user-strings', sites,
                                            ratchet.const_readers(self.root)))

    def test_receipt_identity_is_extended_only_for_literal_and_sink_rows(self):
        # R124 (Sol r6 R6-4): literal receipts and sinks are unique by their full identity; every other row keeps the
        # base key/hash uniqueness, so a contradictory size verdict with another occurrence or use is a duplicate.
        digest = 'a' * 12 + ('~' + 'b' * 12) * 4 + '~cccc'
        other = 'd' * 12 + ('~' + 'e' * 12) * 4 + '~ffff'
        literal = {'key': ['src/Example.cs', 'user-strings', 'Example', 'Run()'], 'hash': digest, 'occurrence': 0,
                   'use': other, 'sinks': other, 'size': 1, 'members': [], 'verdict': 'FALSE-POSITIVE',
                   'family': 'openai', 'reason': 'Reviewed text.'}
        size = {'key': ['src/Example.cs', 'long-member', 'Example', 'Run()'], 'hash': digest, 'size': 45, 'members': [],
                'verdict': 'SPLIT-PENDING', 'family': 'anthropic', 'register': ratchet.reviews.REGISTER.as_posix()}
        sinkrow = dict(literal, key=['src/Example.cs', ratchet.reviews.SINK, 'Example', 'Run()'], parameter='Run.text',
                       symbol='M:Example.Example.Run')
        del sinkrow['occurrence'], sinkrow['use']
        tunable = dict(literal, key=['src/Example.cs', 'magic-tunables', 'Example', 'Run()'])
        del tunable['sinks']
        no_set = dict(literal)
        del no_set['sinks']
        no_symbol = dict(sinkrow)
        del no_symbol['symbol']
        for rows, valid in (([literal, dict(literal, occurrence=1)], True),  # two legitimate occurrences
                            ([literal, dict(literal)], False),  # one true duplicate
                            ([size, dict(size)], False),  # the unchanged structural duplicate
                            ([size, dict(size, occurrence=1, use=other, verdict='REASON-RECORDED')], False),  # contradictory
                            ([sinkrow, dict(sinkrow, parameter='Run.sql')], True), ([sinkrow, dict(sinkrow)], False),
                            ([dict(literal, occurrence=-1)], False), ([dict(literal, use='x')], False),
                            ([dict(literal, reason=' ')], False), ([dict(literal, occurrence='0')], False),
                            ([dict(sinkrow, parameter='text')], False),
                            # R127 (R7-2/R7-3): user-strings receipts and sinks bind the sink set; a sink names a symbol.
                            ([tunable], True), ([no_set], False), ([dict(sinkrow, sinks='x')], False),
                            ([no_symbol], False), ([dict(sinkrow, symbol='Run')], False)):
            with self.subTest(rows=rows):
                self.ledger(*rows)
                if valid:
                    ratchet.reviews.read_ledger(self.root)
                else:
                    with self.assertRaises(ValueError):
                        ratchet.reviews.read_ledger(self.root)

    def test_storage_text_counts_unless_the_literals_owner_permits_it(self):
        # The literals owner's permission is the only way a counted Storage literal leaves the ratchet (B1).
        source = 'class A { string Sql() => "SELECT id FROM item WHERE id=$id"; string Reason() => "Please inspect the saved item."; }'
        spans = []
        masked = ratchet.lex.mask(source, spans)
        path = Path('src/SyncShark.Engine/Storage/A.cs')
        self.assertEqual(2, ratchet.text_count(source, masked, spans, path))
        sql = next(start for start, _ in spans if source[start:].startswith('"SELECT'))
        self.assertEqual(1, ratchet.text_count(source, masked, spans, path, permitted={sql}))

    def test_utf8_suffix_is_token_text_not_display_text(self):
        for suffix in ('u8', 'U8'):
            with self.subTest(suffix=suffix):
                source = 'class Example { object Message = "null"' + suffix + '; }'
                spans = []
                masked = ratchet.lex.mask(source, spans)
                self.assertEqual(['"null"' + suffix], [source[a:b] for a, b in spans])
                self.assertEqual(0, ratchet.text_count(source, masked, spans, Path('Example.cs')))

    def test_new_long_method_fails(self):
        self.put(long_method() + long_method('Added'))
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('NEW', output)
        self.assertIn('Added()', output)

    def test_removed_method_passes_and_reports_improved(self):
        self.put('')
        code, output = self.check()
        self.assertEqual(0, code)
        self.assertIn('improved', output)
        self.assertIn('Run()', output)

    def test_existing_method_cannot_grow(self):
        before = self.baseline.read_bytes()
        self.put(long_method(extra=1))
        self.assertEqual(1, self.check()[0])
        self.assertEqual(before, self.baseline.read_bytes())

    def test_line_shifts_do_not_change_member_identity(self):
        self.source.write_text('\n// Harmless preamble\n' + self.source.read_text(), encoding='utf-8')
        self.assertEqual((0, False), (self.check()[0], 'improved' in self.check()[1].splitlines()[0]))

    def test_overloads_nested_records_and_delegates(self):
        self.put(long_method() + long_method().replace('Run()', 'Run(int value)') +
                 '    class Nested\n    {\n' + long_method() + '    }\n')
        rows = list(ratchet.scan(self.root))
        methods = [r for r in rows if r[0][1] == 'long-member']
        self.assertEqual(3, len(methods))
        self.assertEqual(3, len({r[0] for r in methods}))
        self.source.write_text('namespace Example { record One(int Value); delegate void Two(); }', encoding='utf-8')
        self.assertEqual(2, dict((r[0][1], r[1]) for r in ratchet.scan(self.root))['top-level-types'])

    def test_literals_and_storage_edges_ignore_comments(self):
        self.put('// Microsoft.Data.Sqlite; TimeSpan.FromSeconds(20); "hello there people"\n'
                 '    void M() { var x = "Microsoft.Data.Sqlite"; }\n')
        rows = list(ratchet.scan(self.root))
        self.assertFalse(any(r[0][1] in ('data-outside-storage', 'magic-tunables') for r in rows))
        self.put('    void M() { Console.WriteLine("Please retry this operation."); Task.Delay(20); }\n')
        counts = {r[0][1]: r[1] for r in ratchet.scan(self.root)}
        self.assertEqual(1, counts['user-strings'])
        self.assertEqual(1, counts['magic-tunables'])
        self.assertEqual(1, self.check()[0])

    def test_long_local_function_cannot_hide_inside_a_shrinking_outer(self):
        self.put(long_method(extra=20))
        with self.baseline.open('w', encoding='utf-8', newline='\n') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)
        self.put('    public void Run() {\n' + long_method('Local').replace('public ', '') + '    }\n')
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('Run()::Local()', output)

    def test_record_variable_is_not_a_type(self):
        self.put('    void Read() { foreach (var record in records) { if (record is null) {\n' +
                 '        Work();\n' * 45 + '    } } }\n')
        keys = [row[0] for row in ratchet.scan(self.root)]
        self.assertFalse(any('.in' in key[2] or '.is' in key[2] for key in keys))

    def seed_identity(self):
        ratchet.identity.write_manifest(self.root, ratchet.identity_manifest(self.baseline),
                                        ratchet.lex, ratchet.type_declarations, ratchet.safe_key)

    def test_pure_rename_passes(self):
        self.seed_identity()
        self.source.rename(self.root / 'src' / 'Example.Moved.cs')
        self.assertEqual(0, self.check()[0])
        before = self.baseline.read_bytes()
        self.assertEqual(0, self.check()[0])
        self.assertEqual(before, self.baseline.read_bytes())

    def test_rename_with_growth_fails(self):
        self.seed_identity()
        self.source.rename(self.root / 'src' / 'Example.Moved.cs')
        self.source = self.root / 'src' / 'Example.Moved.cs'
        self.put(long_method(extra=1))
        self.assertEqual(1, self.check()[0])

    def test_member_move_to_partial_file_passes(self):
        self.put('')
        (self.root / 'src' / 'Example.Part.cs').write_text(
            'namespace Example;\npublic partial class Example\n{\n' + long_method() + '}\n', encoding='utf-8')
        self.assertEqual(0, self.check()[0])

    def test_counted_file_rename_preserves_ceiling(self):
        self.put('void Text() { Console.WriteLine("Please retry this operation."); }\n')
        with self.baseline.open('w', encoding='utf-8', newline='\n') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)
        self.seed_identity()
        self.source.rename(self.root / 'src' / 'Example.Moved.cs')
        self.assertEqual(0, self.check()[0])
        self.source = self.root / 'src' / 'Example.Moved.cs'
        self.put('void Text() { Console.WriteLine("Please retry this operation."); '
                 'Console.WriteLine("Please inspect the next operation."); }\n')
        self.assertEqual(1, self.check()[0])

    def seed_current(self):
        self.seed_identity()
        with self.baseline.open('w', encoding='utf-8', newline='\n') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)

    def test_constant_moved_between_files_passes(self):
        constant = '    const int RetryLimit = 20;\n'
        self.put(constant)
        self.seed_current()
        before = self.baseline.read_bytes()
        self.put('')
        (self.root / 'src' / 'SettingsKeys.cs').write_text(
            'class SettingsKeys {\n' + constant + '}\n', encoding='utf-8')
        code, output = self.check()
        self.assertEqual(0, code)
        self.assertIn('INFO file delta', output)
        self.assertEqual(before, self.baseline.read_bytes())

    def test_added_constant_anywhere_fails(self):
        self.put('    const int RetryLimit = 20;\n')
        self.seed_current()
        (self.root / 'src' / 'SettingsKeys.cs').write_text(
            'class SettingsKeys { const int BatchLimit = 50; }', encoding='utf-8')
        self.assertEqual(1, self.check()[0])

    def test_string_moved_to_catalogue_improves_total(self):
        self.put('void Text() { Console.WriteLine("Please retry this operation."); }\n')
        self.seed_current()
        before = dict(ratchet.baseline_rows(self.baseline))
        self.put('void Text() { Console.WriteLine(EdgeMessages.Text("retry")); }\n')
        (self.root / 'src' / 'messages.json').write_text(
            '{"retry": "Please retry this operation."}', encoding='utf-8')
        code, output = self.check()
        self.assertEqual(0, code)
        self.assertIn('improved @total | user-strings', output)
        self.assertEqual(before, dict(ratchet.baseline_rows(self.baseline)))

    def test_snapshot_without_git_or_history(self):
        self.assertFalse((self.root / '.git').exists())
        self.source.rename(self.root / 'src' / 'Example.Snapshot.cs')
        with mock.patch('subprocess.run', side_effect=AssertionError('Git must not run')):
            self.assertEqual(0, self.check()[0])

    def test_identity_ties_do_not_transfer_file_ceiling(self):
        # Two equally plausible oversized destinations cannot share one allowance.
        self.put('    int Value;\n' * 310)
        self.seed_current()
        source = self.source.read_text()
        self.source.unlink()
        for name in ('Example.One.cs', 'Example.Two.cs'):
            (self.root / 'src' / name).write_text(source, encoding='utf-8')
        self.assertEqual(1, self.check()[0])

    def test_long_names_are_privacy_safe_and_reversible(self):
        name = 'Long' * 15
        key = ratchet.safe_key(name)
        self.assertNotIn(name, key)
        self.assertEqual(name, ratchet.order_key(((key,), 1))[0])

    def write_reviews(self, verdict='FALSE-POSITIVE'):
        units = []
        def collect(key, unit):
            units.append(dict(key=list(key), hash=unit['hash'], size=unit['size'],
                              members=unit['members'], verdict=verdict, family='openai',
                              register=ratchet.reviews.REGISTER.as_posix()))
        list(ratchet.scan(self.root, collect))
        self.ledger = self.root / ratchet.reviews.LEDGER
        self.ledger.parent.mkdir(parents=True, exist_ok=True)
        self.ledger.write_text(json.dumps({'version': 1, 'verdicts': units, 'register_reviews': []}))
        self.register = self.root / ratchet.reviews.REGISTER
        self.register.parent.mkdir(parents=True, exist_ok=True)
        if verdict == 'SPLIT-PENDING':
            self.register.write_text('# Structure debt\n\n## src/Example.cs\n\nPending review.\n')
            ratchet.reviews.refresh_register(self.root, ['src/Example.cs'], ratchet.lex)
        elif not self.register.exists():
            self.register.write_text('# Structure debt\n')

    def test_all_review_dispositions_admit_new_sizes_without_raising_baseline(self):
        before = self.baseline.read_bytes()
        for verdict in ratchet.reviews.VERDICTS:
            with self.subTest(verdict=verdict):
                self.put('// One acquisition lifetime.\n' + long_method() +
                         '// One acquisition lifetime.\n' + long_method('Added'))
                self.write_reviews(verdict)
                self.assertEqual(0, self.check()[0])
                self.assertEqual(before, self.baseline.read_bytes())

    def test_review_allows_small_growth_but_not_cumulative_material_growth(self):
        self.write_reviews()
        self.put(long_method(extra=4))
        self.assertEqual(0, self.check()[0])
        self.put(long_method(extra=5))
        self.assertEqual(1, self.check()[0])
        self.write_reviews()
        self.assertEqual(0, self.check()[0])

    def test_successive_small_edits_do_not_roll_the_review_anchor(self):
        self.put('    void Run() {\n' + '        Work();\n' * 98 + '    }\n')
        self.seed_current()
        self.write_reviews()
        original = self.ledger.read_bytes()
        for added, expected in [(8, 0), (16, 1), (24, 1)]:
            with self.subTest(added=added):
                self.put('    void Run() {\n' + '        Work();\n' * (98 + added) + '    }\n')
                self.assertEqual(expected, self.check()[0])
                self.assertEqual(original, self.ledger.read_bytes())

    def test_review_does_not_raise_existing_size_ceiling(self):
        before = self.baseline.read_bytes()
        self.write_reviews()
        self.put(long_method(extra=1))
        self.assertEqual(0, self.check()[0])
        self.assertEqual(before, self.baseline.read_bytes())

    def test_exact_ten_percent_is_not_material(self):
        self.put('    void Run() {\n' + '        Work();\n' * 398 + '    }\n')
        self.write_reviews()
        self.put('    void Run() {\n' + '        Work();\n' * 438 + '    }\n')
        self.assertEqual(0, self.check()[0])
        self.put('    void Run() {\n' + '        Work();\n' * 439 + '    }\n')
        self.assertEqual(1, self.check()[0])

    def test_member_added_while_class_shrinks_invalidates_review(self):
        self.put(long_method(extra=300))
        self.seed_current()
        self.write_reviews()
        self.put(long_method(extra=290) + '    int Added;\n')
        self.assertEqual(1, self.check()[0])

    def test_replaced_member_with_equal_count_invalidates_review(self):
        self.put(long_method(extra=300) + '    int Before;\n')
        self.seed_current()
        self.write_reviews()
        self.put(long_method(extra=300) + '    int After;\n')
        self.assertEqual(1, self.check()[0])

    def test_partial_review_excludes_intervening_sibling(self):
        source = ('namespace Example;\npartial class Example {\n' + long_method(extra=300) +
                  '}\nclass Other { int Before; }\npartial class Example { int Last; }\n')
        self.source.write_text(source)
        self.seed_current()
        self.write_reviews()
        old = next(row for row in json.loads(self.ledger.read_text())['verdicts']
                   if row['key'][1] == 'class-size')
        self.source.write_text(source.replace('int Before;', 'int Before, Added;'))
        self.write_reviews()
        current = json.loads(self.ledger.read_text())
        for index, row in enumerate(current['verdicts']):
            if row['key'] == old['key']:
                current['verdicts'][index] = old
        self.ledger.write_text(json.dumps(current))
        self.assertEqual(0, self.check()[0])

    def test_enum_gained_value_invalidates_review(self):
        self.source.write_text('enum Example {\n' + '    // existing layout\n' * 301 + '    Before\n}\n')
        self.seed_current()
        self.write_reviews()
        self.source.write_text(self.source.read_text().replace('Before', 'Before, After'))
        self.assertEqual(1, self.check()[0])

    def test_comma_declaration_gained_field_invalidates_review(self):
        self.put(long_method(extra=300) + '    int Before, Last;\n')
        self.seed_current()
        self.write_reviews()
        self.put(long_method(extra=300) + '    int Before, Added, Last;\n')
        self.assertEqual(1, self.check()[0])

    def test_new_local_function_invalidates_same_size_member(self):
        self.write_reviews()
        self.put(long_method().replace('        Work();', '        void Local() { Work(); }', 1))
        self.assertEqual(1, self.check()[0])

    def test_nested_local_requires_its_own_current_review(self):
        body = '    void Run() {\n        void Local() {\n' + '            Work();\n' * 41 + '        }\n    }\n'
        self.put(body)
        self.seed_current()
        self.write_reviews()
        original = json.loads(self.ledger.read_text())
        self.put(body.replace('            Work();', '            void Nested() { Work(); }', 1))
        self.write_reviews()
        current = json.loads(self.ledger.read_text())
        for index, row in enumerate(current['verdicts']):
            if row['key'][3].endswith('::Local()'):
                current['verdicts'][index] = next(old for old in original['verdicts'] if old['key'] == row['key'])
        self.ledger.write_text(json.dumps(current))
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('REVIEW REQUIRED', output)

    def test_removed_reason_invalidates_exact_content_verdict(self):
        self.put('// One held transaction.\n' + long_method())
        self.write_reviews('REASON-RECORDED')
        self.assertEqual(0, self.check()[0])
        self.put(long_method())
        self.assertEqual(1, self.check()[0])
        self.put('//\n' + long_method())
        self.assertEqual(1, self.check()[0])

    def test_review_cannot_exempt_literal_or_type_layout_growth(self):
        self.put(long_method() + '    void Text() { Console.WriteLine("Please retry this operation."); Task.Delay(20); }\n')
        self.write_reviews()
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('magic-tunables', output)
        self.assertIn('user-strings', output)
        self.put(long_method())
        self.source.write_text(self.source.read_text() + 'class Added {}\n')
        self.write_reviews()
        self.assertEqual(1, self.check()[0])

    def test_invalid_review_metadata_is_rejected(self):
        self.write_reviews()
        original = self.ledger.read_text()
        for field, value in [('family', ''), ('verdict', 'APPROVED'), ('hash', 'not-a-hash')]:
            with self.subTest(field=field):
                data = json.loads(original)
                data['verdicts'][0][field] = value
                self.ledger.write_text(json.dumps(data))
                with self.assertRaises(ValueError):
                    self.check()

    def public_ledger(self):
        self.write_reviews()
        data = json.loads(self.ledger.read_text())
        data['version'] = 2
        for row in data['verdicts']:
            del row['family']
        self.ledger.write_text(json.dumps(data))
        return data

    def test_public_ledger_without_provenance_passes(self):
        self.public_ledger()
        self.assertEqual(0, self.check()[0])

    def test_public_ledger_rejects_provenance(self):
        for field in ('family', 'reason'):
            data = self.public_ledger()
            data['verdicts'][0][field] = 'fixture'
            self.ledger.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, 'private provenance'):
                self.check()

    def test_public_register_rejects_provenance(self):
        data = self.public_ledger()
        data['register_reviews'].append({'path': 'src/Example.cs', 'hash': data['verdicts'][0]['hash'], 'family': 'fixture'})
        self.ledger.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'private provenance'):
            self.check()

    def test_private_caller_requires_full_ledger(self):
        self.public_ledger()
        with self.assertRaisesRegex(ValueError, 'unsupported'):
            ratchet.reviews.read_ledger(self.root, private=True)

    def test_resolved_verdict_keeps_only_the_canonical_register_path(self):
        self.write_reviews()
        self.assertEqual(0, self.check()[0])
        data = json.loads(self.ledger.read_text())
        data['verdicts'][0]['register'] = 'src/Example.cs'
        self.ledger.write_text(json.dumps(data))
        with self.assertRaises(ValueError):
            self.check()

    def register_file(self):
        self.write_reviews()
        self.register.write_text('# Structure debt\n\n## src/Example.cs\n\nPending split.\n')
        ratchet.reviews.refresh_register(self.root, ['src/Example.cs'], ratchet.lex)

    def test_register_requires_its_own_section_update_each_time(self):
        self.register_file()
        self.assertEqual(0, self.check()[0])
        self.put(long_method().replace('Work();', 'Changed();', 1))
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('REGISTER review required', output)
        self.register.write_text(self.register.read_text() + '\n## src/Other.cs\n\nOther review.\n')
        (self.root / 'src/Other.cs').write_text('class Other {}')
        ratchet.reviews.refresh_register(self.root, ['src/Other.cs'], ratchet.lex)
        self.assertEqual(1, self.check()[0])
        ratchet.reviews.refresh_register(self.root, ['src/Example.cs'], ratchet.lex)
        self.assertEqual(0, self.check()[0])
        self.put(long_method().replace('Work();', 'ChangedAgain();', 1))
        self.assertEqual(1, self.check()[0])

    def test_register_exception_is_bound_to_current_content_and_requires_reason(self):
        self.register_file()
        self.put(long_method().replace('Work();', 'Changed();', 1))
        data = json.loads(self.ledger.read_text())
        data['register_reviews'] = [{'path': 'src/Example.cs', 'family': 'openai',
                                    'hash': ratchet.reviews.content_hash(self.source.read_text(), ratchet.lex),
                                    'reason': 'Extraction is owned by the current split lane.'}]
        self.ledger.write_text(json.dumps(data))
        self.assertEqual(0, self.check()[0])
        self.put(long_method().replace('Work();', 'ChangedAgain();', 1))
        self.assertEqual(1, self.check()[0])
        data['register_reviews'][0]['reason'] = ''
        self.ledger.write_text(json.dumps(data))
        with self.assertRaises(ValueError):
            self.check()

    def test_register_exception_requires_known_family(self):
        self.register_file()
        data = json.loads(self.ledger.read_text())
        data['register_reviews'] = [{'path': 'src/Example.cs', 'family': 'unrecognized',
                                    'hash': ratchet.reviews.content_hash(self.source.read_text(), ratchet.lex),
                                    'reason': 'The extraction remains assigned.'}]
        self.ledger.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'reviewer family'):
            self.check()
        data['register_reviews'][0]['family'] = 'anthropic+openai'
        self.ledger.write_text(json.dumps(data))
        self.assertEqual(0, self.check()[0])

    def test_register_hash_ignores_comments_and_line_endings_not_literals(self):
        self.register_file()
        original = self.source.read_text()
        self.source.write_bytes(('// Export-safe comment\r\n' + original.replace('\n', '\r\n')).encode())
        self.assertEqual(0, self.check()[0])
        digest = ratchet.reviews.content_hash
        self.assertEqual(digest('class A { string Name = "a b"; }', ratchet.lex),
                         digest('#if FLAG\nclass A{string Name="a b";} // comment\n#endif', ratchet.lex))
        self.assertNotEqual(digest('class A { string Name = "a b"; }', ratchet.lex),
                            digest('class A { string Name = "ab"; }', ratchet.lex))
        self.assertNotEqual(digest("class A { char Value = 'a'; }", ratchet.lex),
                            digest("class A { char Value = 'b'; }", ratchet.lex))
        self.assertNotEqual(digest('class A { int Value = x + ++y; }', ratchet.lex),
                            digest('class A { int Value = x++ + y; }', ratchet.lex))

    def test_export_shaped_tree_has_no_docs_dependency(self):
        self.write_reviews()
        exported = self.root / 'tools/ci/structure-debt.md'
        if self.register != exported:
            self.register.rename(exported)
            (self.root / 'docs').rmdir()
        self.assertFalse((self.root / 'docs').exists())
        try:
            self.assertEqual(0, self.check()[0])
        except ValueError as error:
            self.fail('Export-shaped checker failed: ' + str(error))

    def test_deepseek_non_split_requires_escalation(self):
        for verdict in ('FALSE-POSITIVE', 'REASON-RECORDED'):
            with self.subTest(verdict=verdict):
                self.put('// One held transaction.\n' + long_method())
                self.write_reviews(verdict)
                data = json.loads(self.ledger.read_text())
                for family in ('deepseek', 'deepseek+deepseek', 'unrecognized'):
                    data['verdicts'][0]['family'] = family
                    self.ledger.write_text(json.dumps(data))
                    with self.assertRaises(ValueError):
                        self.check()
                data['verdicts'][0]['family'] = 'openai+deepseek'
                self.ledger.write_text(json.dumps(data))
                self.assertEqual(0, self.check()[0])
        self.write_reviews('SPLIT-PENDING')
        data = json.loads(self.ledger.read_text())
        data['verdicts'][0]['family'] = 'deepseek'
        self.ledger.write_text(json.dumps(data))
        self.assertEqual(0, self.check()[0])

    def test_pending_verdict_requires_register_section(self):
        self.write_reviews('SPLIT-PENDING')
        self.assertEqual(0, self.check()[0])
        self.register.write_text('# Structure debt\n')
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('pending verdict has no section', output)

    def test_duplicate_sections_do_not_hide_unreviewed_debt(self):
        self.register_file()
        current = self.register.read_text()
        self.register.write_text('# Structure debt\n\n## src/Example.cs\n\nUnreviewed debt.\n\n' + current)
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('REGISTER review required', output)

    def test_shift_and_comparison_do_not_hide_declarators(self):
        for before, after in (
                ('enum Example {\n' + '    // padding\n' * 301 + 'A = 1 << 2, B\n}\n',
                 'A = 1 << 2, Added, B'),
                ('class Example {\n' + '    // padding\n' * 301 + 'bool a = x < y, b;\n}\n',
                 'bool a = x < y, added, b;')):
            with self.subTest(after=after):
                self.source.write_text(before)
                self.seed_current()
                self.write_reviews()
                old = 'A = 1 << 2, B' if before.startswith('enum') else 'bool a = x < y, b;'
                self.source.write_text(before.replace(old, after))
                self.assertEqual(1, self.check()[0])

    def test_generic_commas_are_not_field_declarations(self):
        for declaration in ('Map<First, Second> a, b;',
                            'object a = Make<First, Second, Third>(), b;'):
            with self.subTest(declaration=declaration):
                before = 'class Example {\n' + '    // padding\n' * 301 + declaration + '\n}\n'
                self.source.write_text(before)
                self.seed_current()
                self.write_reviews()
                self.source.write_text(before.replace('First', 'Other').replace('Second', 'Another'))
                self.assertEqual(0, self.check()[0])
                self.source.write_text(before.replace(', b;', ', added, b;'))
                self.assertEqual(1, self.check()[0])

    def test_type_attributes_preserve_reason_anchor(self):
        self.source.write_text('// One declaration contract.\n[Serializable]\n[Example]\npublic class Example {\n' +
                               '    // padding\n' * 301 + '}\n')
        self.seed_current()
        self.write_reviews('REASON-RECORDED')
        self.assertEqual(0, self.check()[0])
        self.source.write_text(self.source.read_text().replace('// One declaration contract.', '//'))
        self.assertEqual(1, self.check()[0])

    def test_reviewed_file_rename_uses_existing_lineage(self):
        self.put(long_method(extra=300))
        self.seed_current()
        self.write_reviews()
        renamed = self.root / 'src/Example.Part.cs'
        self.source.rename(renamed)
        renamed.write_text(renamed.read_text().replace('        Work();', '        Work();\n        Work();', 1))
        self.assertEqual(0, self.check()[0])

    def test_fresh_verdict_at_renamed_path_uses_lineage(self):
        self.put(long_method(extra=300))
        self.seed_current()
        self.source.rename(self.root / 'src/Example.Part.cs')
        self.source = self.root / 'src/Example.Part.cs'
        self.put(long_method(extra=400))
        self.assertEqual(1, self.check()[0])
        self.write_reviews()
        self.assertTrue(all(row['key'][0] == 'src/Example.Part.cs'
                            for row in json.loads(self.ledger.read_text())['verdicts']))
        self.assertEqual(0, self.check()[0])

    def test_missing_registered_source_reports_review_failure(self):
        self.register_file()
        self.source.rename(self.root / 'src/Example.Part.cs')
        try:
            code, output = self.check()
        except FileNotFoundError as error:
            self.fail('Missing source was not a register finding: ' + str(error))
        self.assertEqual(1, code)
        self.assertIn('listed source missing', output)

    def test_ambiguous_generic_spacing_requires_receipt_review(self):
        self.put(long_method() + '    List<List<int>> Items;\n')
        self.seed_current()
        self.register_file()
        self.source.write_text(self.source.read_text().replace('int>>', 'int> >'))
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('REGISTER review required', output)

    def test_duplicate_refresh_refuses_explicitly_and_atomically(self):
        self.register_file()
        self.register.write_text(self.register.read_text() + '\n## src/Example.cs\n\nDuplicate debt.\n')
        before = self.register.read_bytes()
        with self.assertRaisesRegex(ValueError, 'duplicate structure register section: src/Example.cs'):
            ratchet.reviews.refresh_register(self.root, ['src/Example.cs'], ratchet.lex)
        self.assertEqual(before, self.register.read_bytes())

    def test_register_refresh_unknown_path_is_atomic(self):
        self.register_file()
        before = self.register.read_bytes()
        with self.assertRaises(ValueError):
            ratchet.reviews.refresh_register(self.root, ['src/Example.cs', 'src/Missing.cs'], ratchet.lex)
        self.assertEqual(before, self.register.read_bytes())


class ReviewNegativeControls(unittest.TestCase):
    """Remove one enforcement decision and require its public regression to fail."""

    def rejected_mutation(self, target, name, replacement, test):
        result = unittest.TestResult()
        with mock.patch.object(target, name, replacement):
            RatchetChecks(test).run(result)
        self.assertEqual([], result.errors, 'A broken fixture is not negative evidence')
        self.assertTrue(result.failures, 'The regression did not detect the removed rule')
        print('EXPECTED MUTATION REJECTED:', name, '|', test)

    def test_missing_verdict_control(self):
        self.rejected_mutation(ratchet, 'size_review', lambda *args: 0,
                               'test_new_long_method_fails')

    def test_material_growth_control(self):
        self.rejected_mutation(ratchet.reviews, 'current_verdict',
                               lambda rows, unit: rows[0]['verdict'] if rows else None,
                               'test_review_allows_small_growth_but_not_cumulative_material_growth')

    def test_rolling_anchor_control(self):
        previous = {}
        original = ratchet.reviews.current_verdict
        def rolling(rows, unit):
            anchors = [dict(row, size=previous.get(tuple(row['key']), row['size'])) for row in rows]
            verdict = original(anchors, unit)
            if verdict:
                for row in rows:
                    previous[tuple(row['key'])] = unit['size']
            return verdict
        self.rejected_mutation(ratchet.reviews, 'current_verdict', rolling,
                               'test_successive_small_edits_do_not_roll_the_review_anchor')

    def test_incomplete_member_inventory_control(self):
        def incomplete(masked, owner, kind, opening, closing, members):
            return [(member.start, owner + ':' + member.key()) for member in members]
        for test in ('test_enum_gained_value_invalidates_review',
                     'test_comma_declaration_gained_field_invalidates_review'):
            with self.subTest(test=test):
                self.rejected_mutation(ratchet, 'declared_members', incomplete, test)

    def test_nested_local_inventory_control(self):
        original = ratchet.reviews.describe
        def empty_locals(source, start, end, members, lex, *args, **kwargs):
            return original(source, start, end, [], lex, *args, **kwargs)
        self.rejected_mutation(ratchet.reviews, 'describe', empty_locals,
                               'test_nested_local_requires_its_own_current_review')

    def test_partial_span_control(self):
        self.rejected_mutation(ratchet, 'members_in_spans',
                               lambda members, spans: [key for _, key in members],
                               'test_partial_review_excludes_intervening_sibling')

    def test_operator_token_control(self):
        self.rejected_mutation(ratchet.reviews, 'CODE_TOKEN', re.compile(r'\w+|[^\w\s]'),
                               'test_register_hash_ignores_comments_and_line_endings_not_literals')

    def test_missing_reason_control(self):
        self.rejected_mutation(ratchet.reviews, 'preceding_comment', lambda *args: True,
                               'test_removed_reason_invalidates_exact_content_verdict')

    def test_generic_spacing_fail_closed_control(self):
        self.rejected_mutation(ratchet.reviews, 'CODE_TOKEN', re.compile(r'\w+|[^\w\s]'),
                               'test_ambiguous_generic_spacing_requires_receipt_review')

    def test_export_location_control(self):
        self.rejected_mutation(ratchet.reviews, 'REGISTER', Path('docs/structure-debt.md'),
                               'test_export_shaped_tree_has_no_docs_dependency')

    def test_escalation_control(self):
        self.rejected_mutation(ratchet.reviews, 'read_ledger',
                               lambda root: json.loads((root / ratchet.reviews.LEDGER).read_text()),
                               'test_deepseek_non_split_requires_escalation')

    def test_pending_section_control(self):
        original = ratchet.reviews.check_register
        def ignore_pending(root, ledger, lex):
            return original(root, dict(ledger, verdicts=[]), lex)
        self.rejected_mutation(ratchet.reviews, 'check_register', ignore_pending,
                               'test_pending_verdict_requires_register_section')

    def test_duplicate_section_control(self):
        original = ratchet.reviews.register_sections
        def last_section_only(text):
            return {section[0]: section for section in original(text)}.values()
        self.rejected_mutation(ratchet.reviews, 'register_sections', last_section_only,
                               'test_duplicate_sections_do_not_hide_unreviewed_debt')

    def test_expression_separator_control(self):
        original = ratchet.lex.split_top
        self.rejected_mutation(ratchet.lex, 'split_top',
                               lambda text, sep=',', angles=True: original(text, sep),
                               'test_shift_and_comparison_do_not_hide_declarators')

    def test_generic_separator_control(self):
        self.rejected_mutation(ratchet.lex, 'match_angle', lambda *args: -1,
                               'test_generic_commas_are_not_field_declarations')

    def test_attribute_anchor_control(self):
        self.rejected_mutation(ratchet, 'type_reason_start',
                               lambda masked, start: masked.rfind('\n', 0, start) + 1,
                               'test_type_attributes_preserve_reason_anchor')

    def test_rename_identity_control(self):
        original = ratchet.canonical_key
        def without_lineage(key, renames=None):
            return original(key)
        self.rejected_mutation(ratchet, 'canonical_key', without_lineage,
                               'test_reviewed_file_rename_uses_existing_lineage')

    def test_fresh_rename_lookup_control(self):
        original = ratchet.canonical_key
        def old_ledger_lookup(key, renames=None):
            # Ledger keys are JSON lists; scanned unit keys are tuples.
            return original(key, None if isinstance(key, list) else renames)
        self.rejected_mutation(ratchet, 'canonical_key', old_ledger_lookup,
                               'test_fresh_verdict_at_renamed_path_uses_lineage')

    def test_register_family_control(self):
        self.rejected_mutation(ratchet.reviews, 'review_families', lambda value: set(value.split('+')),
                               'test_register_exception_requires_known_family')

    def test_duplicate_refresh_control(self):
        original = ratchet.reviews.register_sections
        def first_section_only(text):
            seen = set()
            for section in original(text):
                if section[0] not in seen:
                    seen.add(section[0])
                    yield section
        self.rejected_mutation(ratchet.reviews, 'register_sections', first_section_only,
                               'test_duplicate_refresh_refuses_explicitly_and_atomically')

    def test_missing_registered_source_control(self):
        original = ratchet.reviews.check_register
        def unreadable(root, ledger, lex):
            for relative, *_ in ratchet.reviews.register_sections((root / ratchet.reviews.REGISTER).read_text()):
                ratchet.reviews.register_source(root, relative)
            return original(root, ledger, lex)
        self.rejected_mutation(ratchet.reviews, 'check_register', unreadable,
                               'test_missing_registered_source_reports_review_failure')

    def test_unreviewed_register_control(self):
        self.rejected_mutation(ratchet.reviews, 'check_register', lambda *args: 0,
                               'test_register_requires_its_own_section_update_each_time')



class TrustedRatchetChecks(unittest.TestCase):
    """Public entry-point fixtures with real, temporary Git history; hosted only."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='trusted-ratchet-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'src' / 'Example.cs'
        self.source.parent.mkdir()
        self.put('Shipped message one.')
        for relative in (*ratchet.trust.ENFORCEMENT, 'tests/SyncShark.Engine.Tests/Layout/LiteralsChecks.cs'):
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('fixture\n', encoding='utf-8')
        self.baseline = self.root / ratchet.trust.BASELINE
        with self.baseline.open('w', encoding='utf-8') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)
        ratchet.identity.write_manifest(self.root, self.root / ratchet.trust.MANIFEST,
                                        ratchet.lex, ratchet.type_declarations, ratchet.safe_key)
        (self.root / ratchet.reviews.LEDGER).write_text(
            json.dumps({'version': 1, 'verdicts': [], 'register_reviews': []}), encoding='utf-8')
        (self.root / ratchet.reviews.REGISTER).write_text('# Debt\n', encoding='utf-8')
        # The shipped source exceeds its old committed baseline; this is inherited debt.
        self.put('Shipped message one.', 'Shipped message two.')
        self.git('init', '-q')
        self.commit()
        self.live = self.git('rev-parse', 'HEAD')
        self.git('tag', 'v1.0.0')

    def git(self, *args):
        return ratchet.trust.git(self.root, *args)

    def commit(self, message='Fixture'):
        self.git('add', '-A')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=noreply', 'commit', '-qm',
                 message + '\n\nCo-Authored-By: Claude Code <noreply@anthropic.com>')

    def put(self, *messages):
        self.source.write_text('namespace Example;\nclass Example { void Text() { ' +
                               ' '.join('Console.WriteLine(' + json.dumps(s) + ');' for s in messages) +
                               ' } }\n', encoding='utf-8')

    def artifact(self, tag='v1.0.0', commit=None):
        with tempfile.TemporaryDirectory(prefix='literal-release-') as directory:
            release = Path(directory) / 'release'
            with contextlib.redirect_stdout(io.StringIO()):
                ratchet.trust.snapshot(self.root, tag, commit or self.live, release)
            return list(artifact_rows('release', release)) + list(artifact_rows('candidate', self.root))

    def check(self, tag='v1.0.0', commit=None, artifact=True):
        output, error = io.StringIO(), io.StringIO()
        argv = ['check-spec-ratchet.py', '--root', str(self.root), '--live-tag', tag,
                '--live-commit', commit or self.live]
        if artifact is not False:
            path = self.root / 'literal-sites.jsonl'
            try:
                rows = self.artifact(tag, commit) if artifact is True else artifact
            except ValueError:
                rows = []  # The entry point still diagnoses unavailable release evidence.
            path.write_text(''.join(json.dumps(row) + '\n' for row in rows), encoding='utf-8')
            argv += ['--literal-sites', str(path)]
        with mock.patch.dict(os.environ), mock.patch.object(sys, 'argv', argv), contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
            os.environ.pop('RATCHET_LITERAL_SITES', None)
            code = ratchet.main()
        return code, output.getvalue() + error.getvalue()

    def test_literal_producer_changes_require_policy_review(self):
        for name in ('LiteralsChecks.cs', 'LiteralSites.cs'):
            relative = 'tests/SyncShark.Engine.Tests/Layout/' + name
            (self.root / relative).write_text('changed fixture\n', encoding='utf-8')
        code, output = self.check()
        self.assertEqual(0, code, output)
        for name in ('LiteralsChecks.cs', 'LiteralSites.cs'):
            self.assertIn('POLICY REVIEW changed tests/SyncShark.Engine.Tests/Layout/' + name, output)

    def test_optional_policy_archive_and_review(self):
        for relative in ratchet.trust.OPTIONAL_POLICY:
            for change in ('unchanged', 'changed', 'added', 'removed'):
                with self.subTest(path=relative, change=change):
                    fixture = TrustedRatchetChecks()
                    fixture.setUp()
                    try:
                        path = fixture.root / relative
                        path.parent.mkdir(parents=True, exist_ok=True)
                        tag = 'v1.0.0'
                        if change != 'added':
                            path.write_text('shipped policy\n', encoding='utf-8')
                            fixture.commit()
                            fixture.live = fixture.git('rev-parse', 'HEAD')
                            tag = 'v1.0.1'
                            fixture.git('tag', tag)
                        if change in ('changed', 'added'):
                            path.write_text('candidate policy\n', encoding='utf-8')
                        elif change == 'removed':
                            path.unlink()
                        code, output = fixture.check(tag)
                        self.assertEqual(0, code, output)
                        if change == 'unchanged':
                            self.assertNotIn('POLICY REVIEW changed ' + relative, output)
                        else:
                            verb = 'removed' if change == 'removed' else 'changed'
                            self.assertIn('POLICY REVIEW ' + verb + ' ' + relative, output)
                        self.assertNotIn('trusted file missing: ' + relative, output)
                    finally:
                        fixture.doCleanups()

    def test_literal_artifact_missing_is_incomplete(self):
        code, output = self.check(artifact=False)
        self.assertEqual(2, code, output)
        self.assertIn('INCOMPLETE: literal artifact: missing artifact', output)

    def test_literal_artifact_incomplete_rows_both_trees(self):
        for tree in ('candidate', 'release'):
            for defect in ('hash', 'missing site', 'extra site', 'duplicate site', 'missing file', 'extra file'):
                with self.subTest(tree=tree, defect=defect):
                    rows = self.artifact()
                    row = next(row for row in rows if row['tree'] == tree)
                    if defect == 'hash':
                        row['hash'] = 'stale'
                    elif defect == 'missing site':
                        row['sites'].pop()
                    elif defect == 'extra site':
                        row['sites'].append(dict(line=999, text='"extra"', ordinal=0, permitted=True))
                    elif defect == 'duplicate site':
                        row['sites'].append(row['sites'][0])
                    elif defect == 'missing file':
                        rows.remove(row)
                    else:
                        rows.append(dict(row, path='src/Extra.cs'))
                    code, output = self.check(artifact=rows)
                    self.assertEqual(2, code, output)
                    self.assertIn('INCOMPLETE: literal artifact:', output)

    def test_permitted_schema_fragments_are_excluded(self):
        schema = self.root / 'src/SyncShark.Engine/Storage/StateSchema.cs'
        schema.parent.mkdir(parents=True)
        schema.write_text('class StateSchema { const string Bound = "SELECT 1 FROM synced_entry WHERE pair_id = $p"; }', encoding='utf-8')
        rows = self.artifact()
        row = next(row for row in rows if row['path'].endswith('/StateSchema.cs'))
        self.assertEqual(1, len(row['sites']))
        row['sites'][0]['permitted'] = True
        self.assertEqual(0, self.check(artifact=rows)[0])
        row['sites'][0]['permitted'] = False
        code, output = self.check(artifact=rows)
        self.assertEqual(1, code, output)
        self.assertIn('NEW FINDING user-strings', output)

    def test_occurrences_normalization_and_lexical_forms(self):
        self.source.write_bytes(('﻿// non-BMP: \U0001f642\r\nclass Example { string A = "same sentence here"; '
                                 'string B = "same sentence here"; string C = @"multi\r\nline sentence"; '
                                 'string D = """raw sentence here"""; '
                                 'object E = $"outer {Call("inner sentence here")} text"; '
                                 'object F = "utf8 sentence here"u8; }').encode('utf-8'))
        rows = self.artifact()
        row = next(row for row in rows if row['tree'] == 'candidate')
        self.assertEqual([0, 1], [site['ordinal'] for site in row['sites'][:2]])
        self.assertEqual(7, len(row['sites']))
        for site in row['sites']:
            site['permitted'] = True
        self.assertEqual(0, self.check(artifact=rows)[0])

    def test_live_measurement_not_committed_baseline(self):
        before = self.baseline.read_bytes()
        code, output = self.check()
        self.assertEqual(0, code, output)
        self.assertIn('INHERITED DEBT total user-strings 1 -> 2 delta=1', output)
        self.assertIn('INHERITED DEBT slice src/Example.cs', output)
        self.assertEqual(before, self.baseline.read_bytes())

    def test_new_string_fails_by_entity(self):
        self.put('Shipped message one.', 'Shipped message two.', 'A newly hard coded message.')
        code, output = self.check()
        self.assertEqual(1, code, output)
        self.assertIn('NEW FINDING user-strings | :Example.Example |', output)
        self.assertNotIn('A newly hard coded message.', output)

    def test_same_count_replacement_cannot_spend_removed_debt(self):
        self.put('A newly hard coded message.')
        code, output = self.check()
        self.assertEqual(1, code, output)
        self.assertIn('NEW FINDING user-strings', output)

    def test_unrelated_entity_cannot_spend_removed_debt(self):
        self.put()
        self.source.write_text(self.source.read_text().replace('class Example', 'class Other').replace(
            '{  }', '{ Console.WriteLine("Shipped message one."); }'), encoding='utf-8')
        code, output = self.check()
        self.assertEqual(1, code, output)
        self.assertIn('NEW FINDING user-strings | :Example.Other |', output)

    def test_candidate_baseline_addition_and_raise_fail(self):
        original = list(ratchet.baseline_rows(self.baseline))
        for rows in ([(key, value + 1) for key, value in original],
                     original + [(('src/Other.cs', 'user-strings', '', ''), 1)]):
            with self.subTest(rows=rows), self.baseline.open('w', encoding='utf-8') as stream:
                for row in sorted(rows, key=ratchet.order_key):
                    ratchet.write_row(stream, row)
            code, output = self.check()
            self.assertEqual(1, code, output)
            self.assertIn('FAIL candidate baseline added/raised', output)

    def test_candidate_manifest_change_is_reported_not_used(self):
        (self.root / ratchet.trust.MANIFEST).write_text('', encoding='utf-8')
        code, output = self.check()
        self.assertEqual(0, code, output)
        self.assertIn('POLICY REVIEW changed tools/ci/spec-baseline-files.jsonl', output)

    def test_missing_tag_or_mismatched_pin_is_incomplete(self):
        for tag, commit in (('v9.0.0', self.live), ('v1.0.0', '0' * 40)):
            with self.subTest(tag=tag, commit=commit):
                code, output = self.check(tag, commit)
                self.assertEqual(2, code, output)
                self.assertIn('INCOMPLETE:', output)

    def test_missing_trusted_file_is_incomplete(self):
        (self.root / ratchet.trust.MANIFEST).unlink()
        self.commit()
        self.live = self.git('rev-parse', 'HEAD')
        self.git('tag', 'v1.0.1')
        code, output = self.check('v1.0.1')
        self.assertEqual(2, code, output)
        self.assertIn('trusted file missing:', output)

    def test_semantic_entity_rename_and_lowering_pass(self):
        self.source.rename(self.source.with_name('Example.Moved.cs'))
        self.baseline.write_text('', encoding='utf-8')
        code, output = self.check()
        self.assertEqual(0, code, output)
        self.assertIn('POLICY REVIEW changed tools/ci/spec-baseline.jsonl', output)

    def test_old_version_stamp_and_waiver_cannot_accept_growth(self):
        (self.root / 'Directory.Build.props').write_text('<Project><Version>0.3.82</Version></Project>', encoding='utf-8')
        (self.root / 'tools/ci/ratchet-exceptions.json').write_text(json.dumps([
            {'version': '0.3.82', 'category': 'user-strings', 'accepted_total': 999, 'reason': 'fixture'}]), encoding='utf-8')
        self.put('New message not shipped.')
        code, output = self.check()
        self.assertEqual(1, code, output)
        self.assertIn('NEW FINDING user-strings', output)
        self.assertNotIn('EXCEPTION user-strings', output)

    def test_candidate_verdict_and_register_clear_their_findings(self):
        self.source.write_text(self.source.read_text().replace('class Example {',
                               'class Example {\n' + long_method()), encoding='utf-8')
        code, output = self.check()
        self.assertEqual(1, code, output)
        self.assertIn('REVIEW REQUIRED', output)
        units = []
        def collect(key, unit):
            units.append(dict(key=list(key), hash=unit['hash'], size=unit['size'],
                              members=unit['members'], verdict='SPLIT-PENDING', family='openai',
                              register=ratchet.reviews.REGISTER.as_posix()))
        list(ratchet.scan(self.root, collect))
        (self.root / ratchet.reviews.LEDGER).write_text(
            json.dumps({'version': 1, 'verdicts': units, 'register_reviews': []}), encoding='utf-8')
        (self.root / ratchet.reviews.REGISTER).write_text('# Debt\n\n## src/Example.cs\n\nPending split.\n', encoding='utf-8')
        code, output = self.check()
        self.assertEqual(1, code, output)
        self.assertIn('REGISTER review required:', output)
        ratchet.reviews.refresh_register(self.root, ['src/Example.cs'], ratchet.lex)
        code, output = self.check()
        self.assertEqual(0, code, output)
        self.assertIn('REVIEW SPLIT-PENDING', output)
        self.assertNotIn('REGISTER review required:', output)
        for relative in ratchet.trust.POLICY:
            self.assertIn('POLICY REVIEW changed ' + relative, output)

    def test_same_type_constant_rename_passes_but_new_content_fails(self):
        source = ('namespace Example;\nclass Example {\n'
                  'const string Before = "Shipped message one.";\n'
                  'const string Second = "Shipped message two.";\n}\n')
        self.source.write_text(source, encoding='utf-8')
        self.commit()
        self.live = self.git('rev-parse', 'HEAD')
        self.git('tag', 'v1.0.1')
        renamed = source.replace('Before', 'After')
        self.source.write_text(renamed, encoding='utf-8')
        code, output = self.check('v1.0.1')
        self.assertEqual(0, code, output)
        self.assertNotIn('NEW FINDING', output)
        self.source.write_text(renamed.replace('\n}', '\nconst string Added = "A newly hard coded message.";\n}'), encoding='utf-8')
        code, output = self.check('v1.0.1')
        self.assertEqual(1, code, output)
        self.assertIn('NEW FINDING user-strings | :Example.Example |', output)
        self.assertIn('trusted findings=1 comparison=FAIL', output)

    def test_missing_candidate_baseline_keeps_all_diagnostics(self):
        self.baseline.unlink()
        self.source.write_text(self.source.read_text().replace('class Example {',
                               'class Example {\n' + long_method()), encoding='utf-8')
        (self.root / ratchet.reviews.REGISTER).write_text('# Debt\n\n## src/Example.cs\n\nPending split.\n', encoding='utf-8')
        code, output = self.check()
        self.assertEqual(1, code, output)
        self.assertIn('FAIL removed required evidence ' + ratchet.trust.BASELINE, output)
        self.assertIn('REGISTER review required:', output)
        self.assertIn('REVIEW REQUIRED', output)
        self.assertIn('comparison=FAIL', output)

    def test_removed_ledger_register_or_enforcement_test_fails(self):
        for relative in (*ratchet.trust.POLICY, 'tools/ci/test-spec-ratchet.py'):
            path = self.root / relative
            before = path.read_bytes()
            try:
                path.unlink()
                code, output = self.check()
                self.assertEqual(1, code, output)
                self.assertIn('FAIL removed required evidence ' + relative, output)
            finally:
                path.write_bytes(before)


if __name__ == '__main__':
    unittest.main()
