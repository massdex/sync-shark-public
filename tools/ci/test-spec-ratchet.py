"""Small isolated regression tests; python -I tools/ci/test-spec-ratchet.py."""
import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
import sys

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('ratchet', HERE / 'check-spec-ratchet.py')
ratchet = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ratchet)


def long_method(name='Run', extra=0):
    return '    public void ' + name + '()\n    {\n' + '        Work();\n' * (41 + extra) + '    }\n'


class RatchetChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='spec-ratchet-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'src').mkdir()
        self.source = self.root / 'src' / 'Example.cs'
        self.baseline = self.root / 'baseline.jsonl'
        self.put(long_method())
        with self.baseline.open('w', encoding='utf-8') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)

    def put(self, members):
        self.source.write_text('namespace Example;\npublic class Example\n{\n' + members + '}\n', encoding='utf-8')

    def check(self, update=False):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = ratchet.compare(self.root, self.baseline, update)
        return code, output.getvalue()

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
        self.assertEqual(0, self.check(update=True)[0])
        self.assertEqual('', self.baseline.read_text(encoding='utf-8'))

    def test_update_refuses_additions_without_mutating_baseline(self):
        before = self.baseline.read_bytes()
        self.put(long_method('Added'))  # simultaneous removal cannot finance addition
        code, output = self.check(update=True)
        self.assertEqual(1, code)
        self.assertIn('REFUSED', output)
        self.assertEqual(before, self.baseline.read_bytes())

    def test_existing_method_cannot_grow(self):
        before = self.baseline.read_bytes()
        self.put(long_method(extra=1))
        self.assertEqual(1, self.check(update=True)[0])
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

    def test_shortened_method_lowers_baseline(self):
        self.put(long_method(extra=2))
        with self.baseline.open('w', encoding='utf-8') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)
        self.put(long_method())
        code, output = self.check(update=True)
        self.assertEqual(0, code)
        self.assertIn('improved', output)
        self.assertEqual(list(ratchet.scan(self.root)), list(ratchet.baseline_rows(self.baseline)))

    def test_long_local_function_cannot_hide_inside_a_shrinking_outer(self):
        self.put(long_method(extra=20))
        with self.baseline.open('w', encoding='utf-8') as stream:
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

    def test_long_names_are_privacy_safe_and_reversible(self):
        name = 'Long' * 15
        key = ratchet.safe_key(name)
        self.assertNotIn(name, key)
        self.assertEqual(name, ratchet.order_key(((key,), 1))[0])


if __name__ == '__main__':
    unittest.main()
