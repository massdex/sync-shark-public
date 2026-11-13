#!/usr/bin/env python3
"""Deterministic pre-review checks that reuse the spec-ratchet code.

Read-only: never edits a tracked file. Imports the ratchet's own register,
content-hash, type-declaration and unit-measurement code, and receives the
CheckRegistry index from the gate's single parser, so no rule or parser is
copied here. Run with python -I.

Exit 0 when every selected check passes, 1 when any finding is printed, 2 on bad
input. Findings are one FAIL line each, with a precise file:line where one
exists, plus the exact action that clears it.
"""
import argparse
import collections
import importlib.util
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent


def _load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


lex = _load('spec_cslex', 'spec_cslex.py')
reviews = _load('spec_reviews', 'spec_reviews.py')
ratchet = _load('spec_ratchet', 'check-spec-ratchet.py')

REFRESH_HINT = 'python -I tools/ci/check-spec-ratchet.py --refresh-register '
# Reviewed exemptions. Each value is (path, indirect): indirect is False for a
# plain dead wrapper (gone when its declaration is gone), or the name of the
# action-list method that must still contain it. A new exemption is only possible
# by editing this reviewed set, so a lane cannot silence its own method with data.
REVIEWED_EXEMPTIONS = {
    ('ExcludedSavedRowChecks', 'PrefixFirstRun'): ('tests/SyncShark.Engine.Tests/Run/ExcludedSavedRowChecks.cs', False),
    ('ExcludedSavedRowChecks', 'PrefixUnchanged'): ('tests/SyncShark.Engine.Tests/Run/ExcludedSavedRowChecks.cs', False),
    ('ExcludedSavedRowChecks', 'PrefixChangedNeighbor'): ('tests/SyncShark.Engine.Tests/Run/ExcludedSavedRowChecks.cs', False),
    ('ExcludedSavedRowChecks', 'PrefixSavedOnly'): ('tests/SyncShark.Engine.Tests/Run/ExcludedSavedRowChecks.cs', False),
    ('ExcludedSavedRowChecks', 'PrefixBothAbsent'): ('tests/SyncShark.Engine.Tests/Run/ExcludedSavedRowChecks.cs', False),
    ('ExcludedSavedRowChecks', 'PrefixExactName'): ('tests/SyncShark.Engine.Tests/Run/ExcludedSavedRowChecks.cs', False),
    ('ExcludedSavedRowChecks', 'PrefixPerPair'): ('tests/SyncShark.Engine.Tests/Run/ExcludedSavedRowChecks.cs', False),
    # Wired only through ScenarioChecks.OfflineCases(), the hosted-CI path. Stale
    # if registered directly or dropped from that list (a direct call does not count).
    ('ScenarioChecks', 'HarnessSafety'): ('tests/SyncShark.Engine.Tests/Scenarios/ScenarioChecks.cs', 'OfflineCases'),
}
CHECK_REGISTRY = 'tests/SyncShark.Engine.Tests/CheckRegistry.cs'
PUBLIC_STATIC_VOID = re.compile(r'public\s+static\s+void\s+([A-Za-z_]\w*)\s*\(\s*\)')


def git(repo, *args):
    result = subprocess.run(['git', '-C', str(repo), *args], capture_output=True)
    if result.returncode != 0:
        raise ValueError('git ' + ' '.join(args) + ' failed: ' + result.stderr.decode('utf-8', 'replace').strip())
    return result.stdout.decode('utf-8-sig')


def changed_entries(repo, base, head):
    """Changed files with rename detection: (status, new path, old path or None)."""
    entries = []
    for line in git(repo, 'diff', '--name-status', '--find-renames', base + '..' + head).splitlines():
        if not line.strip():
            continue
        parts = line.split('\t')
        status = parts[0]
        if status.startswith('R') and len(parts) >= 3:
            entries.append({'status': 'R', 'path': parts[2], 'old': parts[1]})
        else:
            entries.append({'status': status[0], 'path': parts[1], 'old': None})
    return entries


def changed_src(repo, base, head):
    return [entry['path'] for entry in changed_entries(repo, base, head)
            if entry['path'].startswith('src/') and entry['status'] != 'D']


def head_text(repo, rev, relative):
    if not rev:
        return None
    result = subprocess.run(['git', '-C', str(repo), 'show', rev + ':' + relative], capture_output=True)
    return result.stdout.decode('utf-8-sig') if result.returncode == 0 else None


def section_lines(repo):
    path = repo / reviews.REGISTER
    if not path.exists():
        return {}, []
    text = path.read_text(encoding='utf-8-sig')
    lines = {}
    sections = list(reviews.register_sections(text))
    for relative, start, _end, _section in sections:
        lines[relative] = text.count('\n', 0, start) + 1
    return lines, sections


def check_receipt(repo, base, head):
    """Reuse the ratchet's check_register rule; report only changed src files."""
    findings = []
    ledger = reviews.read_ledger(repo)
    sink = []
    reviews.check_register(repo, ledger, lex, sink)
    if not sink:
        return findings
    # A renamed src file's register section still names the old path; include it.
    changed = set()
    for entry in changed_entries(repo, base, head):
        if entry['status'] == 'D':
            continue
        if entry['path'].startswith('src/'):
            changed.add(entry['path'])
        if entry['old'] and entry['old'].startswith('src/'):
            changed.add(entry['old'])
    lines, _sections = section_lines(repo)
    for relative, reason in sink:
        if relative not in changed:
            continue
        findings.append('FAIL receipt ' + reviews.REGISTER.as_posix() + ':' + str(lines.get(relative, 1))
                        + ' ' + relative + ': ' + reason)
        findings.append('  fix: ' + REFRESH_HINT + relative)
    return findings


def measure_units(relative, text):
    """Reuse the ratchet's unit measurement: the debt units a file declares."""
    if text is None:
        return {}
    units = {}
    with tempfile.NamedTemporaryFile('w', suffix='.cs', delete=False, encoding='utf-8', newline='') as handle:
        handle.write(text.replace('\r\n', '\n').replace('\r', '\n'))
        temp = handle.name
    try:
        def collect(key, unit):
            units[ratchet.canonical_key(key)] = unit
        for _row in ratchet.file_rows(Path(temp), relative, collect):
            pass
    finally:
        try:
            Path(temp).unlink()
        except OSError:
            pass
    return units


def _declaration_line(text, name):
    """The declaration line of a member or type, not a call site."""
    if text is None:
        return None
    pattern = re.compile(
        r'^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|async|sealed|readonly|'
        r'override|virtual|new|unsafe|partial|extern)\s+)+[\w<>\[\],?\s\.]*?\b' + re.escape(name)
        + r'\s*(?:<[^>()]*>)?\s*\(')
    for index, line in enumerate(text.splitlines(), 1):
        if pattern.search(line):
            return index
    return None


def check_grown(repo, base, head):
    findings = []
    ledger = reviews.read_ledger(repo)
    verdicts = collections.defaultdict(list)
    for row in ledger['verdicts']:
        verdicts[ratchet.canonical_key(row['key'])].append(row)
    for entry in changed_entries(repo, base, head):
        if entry['status'] == 'D' or not entry['path'].startswith('src/'):
            continue
        relative = entry['path']
        head_source = head_text(repo, head, relative)
        if head_source is None:
            continue
        head_units = measure_units(relative, head_source)
        if not head_units:
            continue  # No debt unit here; its base cannot grow one.
        base_source = head_text(repo, base, entry['old'] or relative)
        base_units = measure_units(relative, base_source)
        for identity, unit in head_units.items():
            old = base_units.get(identity)
            if old is not None and unit['size'] <= old['size']:
                continue
            if reviews.current_verdict(verdicts.get(identity, []), unit):
                continue
            grow = 'new' if old is None else str(old['size']) + ' -> ' + str(unit['size'])
            findings.append('FAIL grown ' + _grown_location(relative, identity, head_source)
                            + ' ' + identity[1] + ' (' + grow + ' lines) has no current spec-verdicts.json verdict')
            findings.append('  fix: record a spec-verdicts.json verdict for this unit (or split it); '
                            + 'python -I tools/ci/check-spec-ratchet.py shows the ratchet view')
    return findings


def _grown_location(relative, identity, text):
    category, owner = identity[1], identity[2]
    if category == 'file-size':
        return relative + ' (whole file)'
    name = owner.rsplit('.', 1)[-1] if category == 'class-size' else identity[3]
    name = re.sub(r'^static\s+', '', name)
    name = re.match(r'[A-Za-z_]\w*', name)
    line = _declaration_line(text, name.group(0)) if name else None
    owner_label = (owner + ' ') if owner else ''
    return relative + ((':' + str(line)) if line else '') + ' ' + owner_label + identity[3]


def _type_regions(masked):
    """Top-level type regions from the ratchet's own declaration finder."""
    return [{'name': name, 'opening': opening, 'closing': closing}
            for name, _kind, _start, opening, closing in ratchet.type_declarations(masked)]


def _innermost(regions, position):
    best = None
    for region in regions:
        if region['opening'] < position < region['closing']:
            if best is None or (region['closing'] - region['opening']) < (best['closing'] - best['opening']):
                best = region
    return best


def _namespace_last(masked):
    match = re.search(r'\bnamespace\s+([\w.]+)\s*[;{]', masked)
    return match.group(1).split('.')[-1] if match else ''


def _by_class_method(methods):
    index = collections.defaultdict(list)
    for token in methods:
        parts = token.split('.')
        if len(parts) >= 2:
            index[(parts[-2], parts[-1])].append(token)
    return index


def _is_wired(by_cm, namespace_last, owner, name):
    """Only a real CheckRegistry token counts; namespace disambiguates same names."""
    for token in by_cm.get((owner, name), []):
        parts = token.split('.')
        if parts[-2:] != [owner, name]:
            continue
        if len(parts) == 2 or parts[-3] == namespace_last:
            return True
    return False


def _method_body(masked, method):
    """The body text of a method: its block, or an expression-bodied statement."""
    match = re.search(r'static[^;{}()]*\b' + re.escape(method) + r'\s*\(', masked)
    if match is None:
        return None
    opening = masked.index('(', match.start())
    closing = lex.match_close(masked, opening, '(', ')')
    index = closing + 1
    while index < len(masked) and masked[index].isspace():
        index += 1
    if index < len(masked) and masked[index] == '{':
        end = lex.match_close(masked, index, '{', '}')
        return masked[index:end + 1]
    if masked[index:index + 2] == '=>':
        end = masked.find(';', index)
        return masked[index:end if end >= 0 else len(masked)]
    return None


def check_exemptions(repo, by_cm):
    """Stale reviewed exemptions fail: registered now, or the wiring is gone."""
    findings = []
    cache = {}
    for (owner, name), (relative, indirect) in REVIEWED_EXEMPTIONS.items():
        if (owner, name) in by_cm:
            findings.append('FAIL exemption stale: ' + owner + '.' + name
                            + ' is now registered in CheckRegistry; remove it from the reviewed exemptions')
            continue
        if relative not in cache:
            path = repo / relative
            cache[relative] = path.read_text(encoding='utf-8-sig') if path.exists() else None
        text = cache[relative]
        if text is None:
            findings.append('FAIL exemption stale: ' + owner + '.' + name + ' file ' + relative
                            + ' no longer exists; remove the reviewed exemption')
            continue
        masked = lex.mask(text)
        declaration = next((match for match in PUBLIC_STATIC_VOID.finditer(masked) if match.group(1) == name), None)
        if declaration is None:
            findings.append('FAIL exemption stale: ' + owner + '.' + name + ' is no longer declared in '
                            + relative + '; remove the reviewed exemption')
            continue
        if indirect:
            # Search only the named action-list body, not the whole file: a
            # direct call elsewhere does not keep it running in hosted CI.
            body = _method_body(masked, indirect)
            if body is None or not re.search(r'\b' + re.escape(name) + r'\b', body):
                findings.append('FAIL exemption stale: ' + owner + '.' + name
                                + ' is no longer in ' + indirect + '() (it left its action list); '
                                + 'remove the reviewed exemption')
    return findings


def _witness_groups(by_cm, methods, witness):
    # shortcut: a witness wired only through an action list (e.g. OfflineCases)
    # reports "not registered"; model action-list members if a control ever targets one.
    parts = witness.split('.')
    if len(parts) < 2:
        return set()
    owner, method = parts[-2], parts[-1]
    namespace_last = parts[-3] if len(parts) >= 3 else None
    groups = set()
    for token in by_cm.get((owner, method), []):
        token_parts = token.split('.')
        if namespace_last and len(token_parts) >= 3 and token_parts[-3] != namespace_last:
            continue
        groups.update(methods.get(token, []))
    return groups


def check_witness(by_cm, methods, controls_path):
    findings = []
    controls = json.loads(Path(controls_path).read_text(encoding='utf-8-sig'))
    for name, spec in controls.items():
        witness = spec.get('witness', '')
        submitted = spec.get('groups') or ([spec['group']] if spec.get('group') else [])
        groups = _witness_groups(by_cm, methods, witness)
        if not groups:
            findings.append('FAIL witness ' + name + ' -> ' + str(witness)
                            + ' is not registered in CheckRegistry (register the witness)')
        elif not (groups & set(submitted)):
            findings.append('FAIL witness ' + name + ' -> ' + str(witness) + ' is in group(s) '
                            + ', '.join(sorted(groups)) + '; the control is submitted to '
                            + (', '.join(submitted) if submitted else 'no group')
                            + ' (submit the control to the witness group)')
    return findings


def _scan_files(repo, base, head):
    root = repo / 'tests/SyncShark.Engine.Tests'
    if base and head:
        entries = changed_entries(repo, base, head)
        if any(entry['path'] == CHECK_REGISTRY for entry in entries):
            candidates = list(root.rglob('*Checks*.cs'))
        else:
            candidates = [repo / entry['path'] for entry in entries
                          if entry['status'] != 'D'
                          and entry['path'].startswith('tests/SyncShark.Engine.Tests/')
                          and entry['path'].endswith('.cs') and 'Checks' in entry['path'].rsplit('/', 1)[-1]]
    else:
        candidates = list(root.rglob('*Checks*.cs'))
    return [path for path in candidates if '/bin/' not in path.as_posix() and '/obj/' not in path.as_posix()]


def check_tests(repo, base, head, registry_path, controls_path=None):
    findings = []
    methods = json.loads((Path(registry_path) if Path(registry_path).is_absolute()
                          else repo / registry_path).read_text(encoding='utf-8-sig'))
    by_cm = _by_class_method(methods)
    findings.extend(check_exemptions(repo, by_cm))
    for path in _scan_files(repo, base, head):
        text = path.read_text(encoding='utf-8-sig')
        masked = lex.mask(text)
        regions = _type_regions(masked)
        namespace_last = _namespace_last(masked)
        relative = path.relative_to(repo).as_posix()
        for match in PUBLIC_STATIC_VOID.finditer(masked):
            position = match.start()
            name = match.group(1)
            region = _innermost(regions, position)
            if region is None or not region['name'].endswith('Checks'):
                continue
            owner = region['name']
            if (owner, name) in REVIEWED_EXEMPTIONS:
                continue
            if _is_wired(by_cm, namespace_last, owner, name):
                continue
            line = text.count('\n', 0, position) + 1
            findings.append('FAIL register ' + relative + ':' + str(line) + ' ' + owner + '.' + name
                            + '() is not registered in CheckRegistry (add it to ' + CHECK_REGISTRY
                            + ' as ' + owner + '.' + name + ', unless it is wired through an action list)')
    if controls_path:
        findings.extend(check_witness(by_cm, methods, controls_path))
    return findings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', choices=('receipt', 'grown', 'tests'), required=True)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--base')
    parser.add_argument('--head', default='HEAD')
    parser.add_argument('--registry', help='JSON {Class.Method: [CheckRegistry group]} from the gate parser')
    parser.add_argument('--controls', help='JSON {control: {witness: Class.Method, groups: [...]}}')
    args = parser.parse_args()
    repo = args.repo.resolve()
    try:
        if args.check == 'tests':
            if not args.registry:
                raise ValueError('--registry is required for the tests check')
            findings = check_tests(repo, args.base, args.head, args.registry, args.controls)
        else:
            if not args.base:
                raise ValueError('--base is required for the ' + args.check + ' check')
            findings = check_receipt(repo, args.base, args.head) if args.check == 'receipt' \
                else check_grown(repo, args.base, args.head)
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError) as error:
        print('ERROR:', error, file=sys.stderr)
        return 2
    for line in findings:
        print(line)
    return 1 if any(line.startswith('FAIL ') for line in findings) else 0


if __name__ == '__main__':
    sys.exit(main())
