"""Normalize native reports for a non-gating inventory, without parsing C#."""
import argparse
from collections import Counter
from functools import cache
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from urllib.parse import unquote, urlparse
import xml.etree.ElementTree as ET

LANGUAGES = {'.cs': 'C#', '.ps1': 'PowerShell', '.psm1': 'PowerShell',
             '.py': 'Python', '.c': 'native', '.cpp': 'native', '.h': 'native',
             '.zig': 'native', '.md': 'docs', '.txt': 'docs'}
SCHEMA = 'https://json.schemastore.org/sarif-2.1.0.json'
CPD_SOURCE = 'https://github.com/pmd/pmd/blob/pmd_releases/7.29.0/pmd-cs/src/main/java/net/sourceforge/pmd/lang/cs/'
IDENTITY_LIMIT = ('5a uses native metric symbols and diagnostic rule/path/normalized-region hashes. '
                  'Shared ratchet symbol/code-hash identity is deferred to step 5b; owner=coordinator.')


def tracked(root):
    raw = subprocess.check_output(['git', '-C', str(root), 'ls-files', '-z'])
    return [p for p in raw.decode().split('\0') if p]


def language(path):
    return LANGUAGES.get(Path(path).suffix.lower(), 'config/other')


def relative(root, value):
    value = unquote(value)
    if value.startswith('file:'):
        value = urlparse(value).path.lstrip('/') if sys.platform == 'win32' else urlparse(value).path
    path = Path(value)
    if not value or '..' in path.parts:
        return None
    try:
        return (root / path).resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return None


def identity(result):
    return tuple(sorted(result.get('partialFingerprints', {}).items()))


def compare(current, baseline):
    old = {identity(r) for run in baseline['runs'] for r in run.get('results', []) if identity(r)}
    for run in current['runs']:
        for result in run.get('results', []):
            key = identity(result)
            if key:
                result['baselineState'] = 'unchanged' if key in old else 'new'
            else:
                result.setdefault('properties', {})['comparisonStatus'] = 'INCOMPLETE'


def finding(rule, message, path=None, line=1, **properties):
    row = {'ruleId': rule, 'level': 'warning', 'message': {'text': message}, 'properties': properties}
    if path:
        physical = {'artifactLocation': {'uri': path}}
        if line:
            physical['region'] = {'startLine': max(1, int(line))}
        row['locations'] = [{'physicalLocation': physical}]
    return row


@cache
def source_lines(path):
    return path.read_text(encoding='utf-8-sig').splitlines(keepends=True)


def region_identity(root, result, column_kind='utf16CodeUnits'):
    """Use the native primary region, not inferred declarations or diagnostic text."""
    try:
        physical = result['locations'][0]['physicalLocation']
        file = relative(root, physical['artifactLocation']['uri'])
        if not file:
            raise ValueError('location outside scanned root')
        region = physical['region']
        first = region['startLine']
        last = region.get('endLine', first)
        lines = source_lines(root / file)
        if not 1 <= first <= last <= len(lines):
            raise ValueError('region outside source')
        selected = lines[first - 1:last]
        encoding, width = ('utf-32-le', 4) if column_kind == 'unicodeCodePoints' else ('utf-16-le', 2)
        if 'endColumn' in region:
            selected[-1] = selected[-1].encode(encoding)[:(region['endColumn'] - 1) * width].decode(encoding)
        if 'startColumn' in region:
            selected[0] = selected[0].encode(encoding)[(region['startColumn'] - 1) * width:].decode(encoding)
        text = ''.join(selected).replace('\r\n', '\n').replace('\r', '\n').strip()
        result['partialFingerprints'] = {'inventory.rule': result['ruleId'], 'inventory.path': file,
                                        'inventory.regionHash': hashlib.sha256(text.encode()).hexdigest()}
    except (OSError, UnicodeError, KeyError, IndexError, ValueError, TypeError) as error:
        result.setdefault('properties', {})['identityStatus'] = 'INCOMPLETE: ' + str(error)


def metrics(root, path):
    tree = ET.parse(path)
    rows, covered = [], set()
    targets = tree.findall('.//Target')
    if not targets:
        raise ValueError('Metrics report has no Target')
    for target in targets:
        for namespace in target.findall('.//Namespace'):
            for kind in namespace.findall('./Types/NamedType'):
                symbol = namespace.attrib['Name'] + '.' + kind.attrib['Name']
                for node, name in [(kind, symbol)] + [(m, symbol + '.' + m.attrib['Name'])
                                                    for m in kind.findall('./Members/*')]:
                    file = relative(root, node.attrib.get('File', ''))
                    if file:
                        covered.add(file)
                    values = {m.attrib['Name']: float(m.attrib['Value']) for m in node.findall('./Metrics/Metric')}
                    row = finding('metrics/' + node.tag, 'Native semantic metrics', file,
                                  node.attrib.get('Line', 1), metrics=values, project=target.attrib['Name'])
                    row['partialFingerprints'] = {'inventory.rule': row['ruleId'], 'inventory.symbol': name}
                    row['properties']['missingMetrics'] = sorted({'SourceLines', 'ExecutableLines'} - values.keys())
                    if row['properties']['missingMetrics']:
                        row['properties']['status'] = 'INCOMPLETE'
                    rows.append(row)
    return rows, covered


def native_sarif(root, path):
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    rows, covered = [], set()
    if not data['runs']:
        raise ValueError('SARIF has no tool run')
    for run in data['runs']:
        for result in run.get('results', []):
            if 'ruleId' not in result:
                result['ruleId'] = run['tool']['driver']['rules'][result['ruleIndex']]['id']
            result.pop('ruleIndex', None)
            for location in result.get('locations', []):
                artifact = location.get('physicalLocation', {}).get('artifactLocation', {})
                base = run.get('originalUriBaseIds', {}).get(artifact.get('uriBaseId'), {}).get('uri', '')
                file = relative(root, base + artifact.get('uri', ''))
                artifact.pop('uriBaseId', None)
                artifact['uri'] = file or 'external-input'
                if file:
                    covered.add(file)
            region_identity(root, result, run.get('columnKind', 'utf16CodeUnits'))
            rows.append(result)
            if result.get('suppressions'):
                suppressed = finding('suppression/diagnostic', 'Native tool reported a suppressed diagnostic',
                                     suppressedRule=result['ruleId'], suppressions=result['suppressions'])
                suppressed['locations'] = result.get('locations', [])
                region_identity(root, suppressed)
                rows.append(suppressed)
    return rows, covered


def powershell(root, path):
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    rows = []
    for row in data:
        result = finding(row['RuleName'], row['Message'], relative(root, row['ScriptPath']), row['Line'],
                         isSuppressed=row.get('IsSuppressed', False))
        region_identity(root, result)
        rows.append(result)
    return rows, set()


def suppressions(root, path):
    rows, covered = [], set()
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        file, number, text = line.split(':', 2)
        covered.add(file)
        row = finding('suppression/candidate', text, file, number,
                      classification='git grep candidate; includes comments and strings, not semantic confirmation')
        region_identity(root, row)
        rows.append(row)
    return rows, covered


def repository_checks(root, files):
    ignored = subprocess.check_output(['git', '-C', str(root), 'ls-files', '-ci', '--exclude-standard', '-z'])
    rows = [finding('repository/tracked-ignored', 'Tracked file matches ignore rules', p)
            for p in ignored.decode().split('\0') if p]
    # GitHub warns above 50 MiB; this inventory adds no rejection.
    for file in files:
        size = (root / file).stat().st_size
        if size > 50 * 1024 * 1024:
            rows.append(finding('repository/oversize', 'File exceeds GitHub 50 MiB warning size', file, bytes=size))
    return rows


def coverage(tool, files, covered, status, detail):
    denominators = Counter(language(p) for p in files)
    covered = set(covered).intersection(files)
    numerators = Counter(language(p) for p in covered)
    return [{'tool': tool, 'language': lang, 'denominator': count,
             'coveredFiles': numerators[lang],
             'notCoveredFiles': sorted(p for p in files if language(p) == lang and p not in covered),
             'status': 'INCOMPLETE' if status == 'INCOMPLETE' else status if numerators[lang] else 'NOT COVERED',
             'detail': detail} for lang, count in sorted(denominators.items())]


def tool_run(root, output, tool, files):
    results, covered, errors = [], set(), []
    for report in tool['reports']:
        try:
            parser = {'metrics': metrics, 'sarif': native_sarif, 'powershell': powershell,
                      'suppressions': suppressions}[tool['format']]
            scan_root = output / 'tracked-snapshot' if tool['name'] == 'gitleaks' else root
            rows, seen = parser(scan_root, output / report)
            results.extend(rows)
            covered.update(seen)
        except (OSError, ValueError, KeyError, TypeError, ET.ParseError) as error:
            errors.append(type(error).__name__ + ': ' + str(error))
        finally:
            source_lines.cache_clear()
    if tool.get('inputReport'):
        try:
            evaluated = json.loads((output / tool['inputReport']).read_text(encoding='utf-8-sig'))
            tool['inputs'] = [p for item in evaluated['Items']['Compile']
                              if (p := relative(root, item['FullPath'])) in files]
        except (OSError, ValueError, KeyError, TypeError) as error:
            errors.append('Compile input inventory unavailable: ' + str(error))
    if not tool['reports'] or tool['exit'] not in tool['acceptedExits']:
        errors.append('Tool failed, was missing, or produced no reports')
    if not errors:
        covered.update(tool.get('inputs', []))
    status = 'INCOMPLETE' if errors else 'COMPLETE'
    run = {'tool': {'driver': {'name': tool['name'], 'version': tool['version']}},
           'results': results, 'invocations': [{'executionSuccessful': not errors}],
           'properties': {'status': status, 'errors': errors, 'inputFiles': tool.get('inputs', [])}}
    return run, coverage(tool['name'], files, covered, status, '; '.join(errors))


def collect(root, output, baseline=None):
    files = tracked(root)
    manifest = json.loads((output / 'tools.json').read_text(encoding='utf-8-sig'))
    runs, cover = [], []
    for tool in manifest:
        run, covered = tool_run(root, output, tool, files)
        runs.append(run)
        cover.extend(covered)
    runs.append({'tool': {'driver': {'name': 'repository-inventory'}}, 'results': repository_checks(root, files)})
    cover.extend(coverage('repository-inventory', files, files, 'COMPLETE', 'Git tracked-ignored and GitHub oversize inventory'))
    cover.extend(coverage('C# literal-equivalent clones', files, [], 'NOT COVERED',
                          'PMD CPD 7.29.0 lacks C# literal anonymization; ' + CPD_SOURCE + 'CsLanguageModule.java; '
                          + CPD_SOURCE + 'cpd/CsCpdLexer.java; owner=coordinator; trigger=step 5b'))
    cover.extend(coverage('suppression semantics', files, [], 'NOT COVERED',
                          'Native suppressions and textual candidates retained; candidates are not semantic confirmation.'))
    cover.extend(coverage('separate lambda metrics', files, [], 'NOT COVERED',
                          'Metrics exposes declaring members, not independent lambda units; initializers remain in native member metrics.'))
    result = {'$schema': SCHEMA, 'version': '2.1.0', 'runs': runs,
              'properties': {'coverage': cover, 'status': 'INCOMPLETE', 'identityLimit': IDENTITY_LIMIT}}
    if baseline:
        compare(result, json.loads(baseline.read_text(encoding='utf-8-sig')))
    (output / 'inventory.sarif').write_text(json.dumps(result, ensure_ascii=True), encoding='utf-8')
    (output / 'coverage.json').write_text(json.dumps(cover, indent=2), encoding='utf-8')
    summary = {'tools': {r['tool']['driver']['name']: len(r['results']) for r in runs},
               'findingsByToolLanguage': {r['tool']['driver']['name']: dict(Counter(
                   language(x.get('locations', [{}])[0].get('physicalLocation', {}).get('artifactLocation', {}).get('uri', ''))
                   for x in r['results'])) for r in runs},
               'languages': dict(Counter(language(p) for p in files)),
               'sarifBytes': (output / 'inventory.sarif').stat().st_size, 'status': 'INCOMPLETE'}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--baseline', type=Path)
    args = parser.parse_args()
    collect(args.root.resolve(), args.output.resolve(), args.baseline)
