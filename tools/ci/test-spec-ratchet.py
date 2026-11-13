"""Small isolated regression tests; python -I tools/ci/test-spec-ratchet.py."""
import contextlib
import importlib.util
import io
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
        with self.baseline.open('w', encoding='utf-8', newline='\n') as stream:
            for row in ratchet.scan(self.root):
                ratchet.write_row(stream, row)
        self.put(long_method())
        code, output = self.check(update=True)
        self.assertEqual(0, code)
        self.assertIn('improved', output)
        self.assertEqual(list(ratchet.scan(self.root)), list(ratchet.baseline_rows(self.baseline)))

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
        self.assertEqual(0, self.check(update=True)[0])
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

    # --- release-scoped total exception ---

    def put_release(self, version='0.3.82', released=(), pending=()):
        (self.root / 'Directory.Build.props').write_text(
            "<Project><PropertyGroup><Version Condition=\"'$(Version)' == ''\">" +
            version + "</Version></PropertyGroup></Project>", encoding='utf-8')
        rows = ['| ' + row + ' | ' + 'a' * 40 + ' |' for row in released]
        rows += ['| ' + row + ' | -- |' for row in pending]
        if rows:
            (self.root / 'VERSION-MAP.md').write_text(
                '# map\n\n| version | commit |\n|---|---|\n' +
                ''.join(row + '\n' for row in rows), encoding='utf-8')

    def put_exceptions(self, entries):
        path = self.root / ratchet.EXCEPTIONS
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(entries), encoding='utf-8')

    @staticmethod
    def exception(version='0.3.82', category='user-strings', total=2):
        return {'version': version, 'category': category, 'accepted_total': total,
                'reason': 'accepted for release 0.3.82 pending removal of the underlying items'}

    def check_main(self, *extra):
        argv = ['check-spec-ratchet.py', '--root', str(self.root),
                '--baseline', str(self.baseline)] + list(extra)
        output, error = io.StringIO(), io.StringIO()
        with mock.patch.object(sys, 'argv', argv), \
                contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
            code = ratchet.main()
        return code, output.getvalue(), error.getvalue()

    def counted_total(self, category):
        return sum(value for key, value in ratchet.baseline_rows(self.baseline)
                   if key[1] == category)

    def grow_user_strings(self):
        self.put('    void M() { Console.WriteLine("Please retry this operation."); }\n')
        self.seed_current()
        self.put('    void M() { Console.WriteLine("Please retry this operation."); '
                 'Console.WriteLine("Please inspect the next operation."); }\n')

    def test_exception_at_ceiling_passes_with_visible_line(self):
        self.grow_user_strings()
        self.put_release()
        self.put_exceptions([self.exception(total=2)])
        code, output = self.check()
        self.assertEqual(0, code)
        self.assertIn('EXCEPTION user-strings 2 <= 2', output)

    def test_exception_above_ceiling_fails(self):
        self.grow_user_strings()
        self.put_release()
        self.put_exceptions([self.exception(total=1)])
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertNotIn('EXCEPTION user-strings', output)

    def test_exception_version_mismatch_exits_two(self):
        self.grow_user_strings()
        self.put_release(version='0.3.99')
        self.put_exceptions([self.exception(version='0.3.82', total=2)])
        code, output, error = self.check_main()
        self.assertEqual(2, code)
        self.assertIn('not the current release', error)

    def test_exception_for_released_version_exits_two(self):
        self.grow_user_strings()
        self.put_release(version='0.3.82', released=('0.3.82',))
        self.put_exceptions([self.exception(total=2)])
        code, output, error = self.check_main()
        self.assertEqual(2, code)
        self.assertIn('already released', error)

    def test_pending_version_map_row_does_not_expire(self):
        self.grow_user_strings()
        self.put_release(version='0.3.82', pending=('0.3.82',))
        self.put_exceptions([self.exception(total=2)])
        code, output, error = self.check_main()
        self.assertEqual(0, code)
        self.assertIn('EXCEPTION user-strings 2 <= 2', output)

    def test_exception_does_not_cover_size_rows(self):
        self.put_release()
        self.put_exceptions([self.exception(total=5)])
        self.put(long_method(extra=1))
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertIn('GROWN', output)

    def test_exception_does_not_cover_counted_category_without_entry(self):
        self.put('    void M() { var a = new Microsoft.Data.Sqlite.SqliteConnection(); }\n')
        self.seed_current()
        self.put('    void M() { var a = new Microsoft.Data.Sqlite.SqliteConnection(); '
                 'var b = new Microsoft.Data.Sqlite.SqliteConnection(); }\n')
        self.put_release()
        self.put_exceptions([self.exception(category='user-strings', total=99)])
        code, output = self.check()
        self.assertEqual(1, code)
        self.assertNotIn('EXCEPTION data-outside-storage', output)

    def test_update_baseline_does_not_raise_counted_slice(self):
        self.grow_user_strings()
        before = self.counted_total('user-strings')
        self.put_release()
        self.put_exceptions([self.exception(total=2)])
        code, output = self.check(update=True)
        self.assertEqual(0, code)
        self.assertLessEqual(self.counted_total('user-strings'), before)

    def test_constant_moved_between_files_passes(self):
        constant = '    const int RetryLimit = 20;\n'
        self.put(constant)
        self.seed_current()
        before = self.baseline.read_bytes()
        self.put('')
        (self.root / 'src' / 'SettingsKeys.cs').write_text(
            'class SettingsKeys {\n' + constant + '}\n', encoding='utf-8')
        code, output = self.check(update=True)
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
        code, output = self.check(update=True)
        self.assertEqual(0, code)
        self.assertIn('improved @total | user-strings', output)
        self.assertNotIn('user-strings', self.baseline.read_text())
        for key, value in ratchet.baseline_rows(self.baseline):
            self.assertLessEqual(value, before[key])

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
                self.assertEqual(0, self.check(update=True)[0])
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
                self.assertEqual(expected, self.check(update=True)[0])
                self.assertEqual(original, self.ledger.read_bytes())

    def test_review_does_not_raise_existing_size_ceiling(self):
        before = self.baseline.read_bytes()
        self.write_reviews()
        self.put(long_method(extra=1))
        self.assertEqual(0, self.check(update=True)[0])
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
        for case, failure in result.failures:
            print('NEGATIVE CONTROL:', name, '|', case.id(), '\n' + failure)

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

    def test_exception_at_ceiling_control(self):
        self.rejected_mutation(ratchet, 'exception_applies', lambda *args: False,
                               'test_exception_at_ceiling_passes_with_visible_line')

    def test_exception_above_ceiling_control(self):
        def loosened(accepted, category, value):
            entry = accepted.get(category)
            if entry is None or value > entry['accepted_total'] + 1:
                return False
            print('EXCEPTION', category, value, '<=', entry['accepted_total'], '|', entry['reason'])
            return True
        self.rejected_mutation(ratchet, 'exception_applies', loosened,
                               'test_exception_above_ceiling_fails')

    def test_exception_version_control(self):
        self.rejected_mutation(ratchet, 'assert_exception_scope', lambda *args: None,
                               'test_exception_version_mismatch_exits_two')

    def test_exception_released_control(self):
        self.rejected_mutation(ratchet, 'assert_exception_scope', lambda *args: None,
                               'test_exception_for_released_version_exits_two')

    def test_pending_row_control(self):
        def loose(root):
            path = root / ratchet.VERSION_MAP
            return set(re.findall(r'^\|\s*([0-9]+(?:\.[0-9]+)*)\s*\|',
                                  path.read_text(encoding='utf-8-sig'), re.M))
        self.rejected_mutation(ratchet, 'released_versions', loose,
                               'test_pending_version_map_row_does_not_expire')

    def test_exception_category_control(self):
        self.rejected_mutation(ratchet, 'total_excepted', lambda row, value, accepted: True,
                               'test_exception_does_not_cover_size_rows')

    def test_exception_missing_entry_control(self):
        def all_counted(root):
            return {category: {'version': '0.3.82', 'category': category,
                               'accepted_total': 10 ** 9, 'reason': 'control'}
                    for category in ratchet.COUNTED}
        self.rejected_mutation(ratchet, 'total_exceptions', all_counted,
                               'test_exception_does_not_cover_counted_category_without_entry')


if __name__ == '__main__':
    unittest.main()
