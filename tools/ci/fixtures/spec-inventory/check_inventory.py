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
    current = {'runs': [{'results': [old, moved, changed]}]}
    inventory.compare(current, {'runs': [{'results': [old]}]})
    assert [r['baselineState'] for r in (old, moved, changed)] == ['unchanged', 'new', 'new']
    assert 'partialFingerprints' not in inventory.finding('R', 'unbound')
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
    print('PASS missing executable/output remain incomplete; valid zero-findings report credits evaluated inputs')


def check_metrics(output):
    tree = inventory.ET.parse(output / 'Fixture.metrics.xml')
    partial = [t for t in tree.findall('.//NamedType') if t.attrib['Name'] == 'Together']
    assert len(partial) == 1, 'Partial declarations must aggregate into one semantic type'
    values = {m.attrib['Name']: int(m.attrib['Value']) for m in partial[0].findall('./Metrics/Metric')}
    assert values['SourceLines'] > 300, values
    assert all(len((output / f'Partial.{part}.cs').read_text().splitlines()) < 300 for part in ('Left', 'Right'))
    names = [n.attrib['Name'] for n in tree.findall('.//Namespace')]
    assert 'Fixture.Apps' in names and 'Fixture.Tests' in names
    methods = [m.attrib['Name'] for m in tree.findall('.//Method')]
    assert len([m for m in methods if 'Overload(' in m]) == 2
    fields = tree.findall('.//Field')
    assert any('Factory' in f.attrib['Name'] for f in fields), 'Lambda-containing field missing'
    assert tree.findall('.//Event'), 'Event initializer metrics missing'
    rows, _ = inventory.metrics(output, output / 'Fixture.metrics.xml')
    symbols = [r['partialFingerprints']['inventory.symbol'] for r in rows]
    assert 'Fixture.Apps.Example' in symbols and 'Fixture.Tests.Example' in symbols
    assert all('inventory.regionHash' not in r['partialFingerprints'] for r in rows)
    (output / 'metric-calibration.json').write_text(json.dumps([
        {'symbol': r['partialFingerprints']['inventory.symbol'], 'metrics': r['properties']['metrics'],
         'missingMetrics': r['properties']['missingMetrics']} for r in rows], indent=2), encoding='utf-8')
    print('PASS native metrics: partial aggregation, namespaces, overloads, field/event/lambda initializers')


if __name__ == '__main__':
    output = Path(sys.argv[1])
    failures = []
    with tempfile.TemporaryDirectory(dir=output) as temporary:
        for check in (check_identity, check_missing):
            try:
                check(Path(temporary))
            except Exception as error:
                failures.append(check.__name__ + ': ' + repr(error))
    try:
        check_metrics(output)
    except Exception as error:
        failures.append('check_metrics: ' + repr(error))
    if failures:
        raise AssertionError('; '.join(failures))
