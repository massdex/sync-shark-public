"""Hosted contract fixtures for native output adaptation and inventory identity."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile

module_path = Path(__file__).resolve().parents[2] / 'spec_inventory.py'
spec = importlib.util.spec_from_file_location('inventory', module_path)
inventory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inventory)


def check_identity(root):
    (root / 'Old.cs').write_text('  class A { int M() => 1; }\n', encoding='utf-8')
    (root / 'Moved.cs').write_text('class A { int M() => 1; }\r\n', encoding='utf-8')
    (root / 'Changed.cs').write_text('class A { int M() => 2; }\n', encoding='utf-8')
    old, moved, changed = [inventory.finding('R', 'fixture', p) for p in ('Old.cs', 'Moved.cs', 'Changed.cs')]
    for row in (old, moved, changed):
        inventory.region_identity(root, row)
    assert old['partialFingerprints']['inventory.regionHash'] == moved['partialFingerprints']['inventory.regionHash']
    assert old['partialFingerprints']['inventory.regionHash'] != changed['partialFingerprints']['inventory.regionHash']
    assert 'partialFingerprints' not in inventory.finding('R', 'unbound')
    (root / 'Unicode.cs').write_text(chr(0x10400) + 'value', encoding='utf-8')
    utf16 = inventory.finding('R', 'utf16', 'Unicode.cs')
    codepoints = inventory.finding('R', 'codepoints', 'Unicode.cs')
    utf16['locations'][0]['physicalLocation']['region'].update(startColumn=3, endColumn=8)
    codepoints['locations'][0]['physicalLocation']['region'].update(startColumn=2, endColumn=7)
    inventory.region_identity(root, utf16)
    inventory.region_identity(root, codepoints, 'unicodeCodePoints')
    assert utf16['partialFingerprints'] == codepoints['partialFingerprints']
    print('PASS 5a location identity: normalized region stable; moves and changed code new; semantic migration deferred')


def check_missing(root):
    tool = dict(name='missing', version='fixture', format='sarif', reports=['absent.sarif'],
                inputs=['Old.cs'], exit=-1, acceptedExits=[0])
    run, coverage = inventory.tool_run(root, root, tool, ['Old.cs'])
    assert run['properties']['status'] == 'INCOMPLETE'
    assert coverage[0]['status'] == 'INCOMPLETE' and coverage[0]['coveredFiles'] == 0
    tool['exit'] = 0
    run, _ = inventory.tool_run(root, root, tool, ['Old.cs'])
    assert run['properties']['status'] == 'INCOMPLETE', 'Successful exit cannot excuse absent output'
    (root / 'empty.sarif').write_text(json.dumps({'runs': [{'results': []}]}), encoding='utf-8')
    tool['reports'] = ['empty.sarif']
    run, coverage = inventory.tool_run(root, root, tool, ['Old.cs'])
    assert run['properties']['status'] == 'COMPLETE' and coverage[0]['coveredFiles'] == 1
    tool['failure'] = 'missing INCOMPLETE after 1.25 seconds: native timeout'
    run, coverage = inventory.tool_run(root, root, tool, ['Old.cs'])
    assert run['properties']['status'] == 'INCOMPLETE' and tool['failure'] in run['properties']['errors']
    assert coverage[0]['status'] == 'INCOMPLETE'
    (root / 'powershell.json').write_text(json.dumps([
        dict(RuleName='P', Message='locationless', ScriptPath=str(root / 'Old.cs'), Line=None, Severity=1),
        dict(RuleName='P', Message='located', ScriptPath=str(root / 'Old.cs'), Line=1, Severity=1, IsSuppressed=True)]), encoding='utf-8')
    rows, _ = inventory.powershell(root, root / 'powershell.json')
    assert len(rows) == 3 and 'identityStatus' in rows[0]['properties']
    assert 'partialFingerprints' in rows[1] and rows[2]['ruleId'] == 'suppression/diagnostic'
    assert rows[1]['partialFingerprints']['inventory.regionHash'] == rows[2]['partialFingerprints']['inventory.regionHash']
    print('PASS missing executable/output, zero findings, and native locationless diagnostics')


def check_diagnostics(output):
    native = json.loads((output / 'Fixture.sarif').read_text(encoding='utf-8-sig'))
    results = [r for run in native['runs'] for r in run.get('results', [])]
    rules = {r['ruleId'] for r in results}
    expected = {'SA1402', 'SA1649', 'CA1849', 'VSTHRD002', 'VSTHRD103', 'CA2016', 'CA1031', 'CA1501', 'CA1502', 'CA1505', 'CA1506', 'RCS1075', 'CS1570', 'CS1591'}
    assert expected <= rules, 'Missing native diagnostics: ' + str(sorted(expected - rules))
    suppressed = {r['ruleId'] for r in results if r.get('suppressions')}
    assert {'CA1031', 'CS1591'} <= suppressed, 'Attribute and pragma suppression evidence must be retained'
    paths = [loc['physicalLocation']['artifactLocation']['uri'].replace('\\', '/')
             for r in results for loc in r.get('locations', [])]
    for area in ('apps', 'tests', 'tools'):
        assert any('/cases/' + area + '/' in path for path in paths), 'No native finding under ' + area
    candidates = (output / 'suppressions.txt').read_text(encoding='utf-8-sig')
    fixture = Path(__file__).parent / 'cases/suppression-syntaxes.txt'
    relative = fixture.relative_to(Path(__file__).resolve().parents[4]).as_posix()
    for number, line in enumerate(fixture.read_text().splitlines()[1:], start=2):
        anchored = f'{relative}:{number}:{line}'
        assert anchored in candidates, 'Suppression candidate missing: ' + anchored
    assembled = json.loads((output / 'inventory.sarif').read_text())
    failed = [run['tool']['driver']['name'] for run in assembled['runs']
              if run.get('properties', {}).get('status') == 'INCOMPLETE']
    assert not failed, 'Native execution/report failures: ' + str(failed)
    print('PASS all requested analyzer rules, scope locations, suppression candidates, and native tool execution')


if __name__ == '__main__':
    output = Path(sys.argv[1])
    failures = []
    with tempfile.TemporaryDirectory(dir=output) as temporary:
        for check in (check_identity, check_missing):
            try:
                check(Path(temporary))
            except Exception as error:
                failures.append(check.__name__ + ': ' + repr(error))
    for check in (check_diagnostics,):
        try:
            check(output)
        except Exception as error:
            failures.append(check.__name__ + ': ' + repr(error))
    if failures:
        raise AssertionError('; '.join(failures))
