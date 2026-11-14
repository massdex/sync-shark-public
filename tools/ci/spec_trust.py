"""Release-owned inputs and measured inventory for the spec ratchet."""
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import re
import sqlite3
import shutil
import subprocess
import tarfile
import tempfile

_spec = importlib.util.spec_from_file_location('spec_literal_sites', Path(__file__).with_name('spec_literal_sites.py'))
literal_sites = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(literal_sites)

BASELINE = 'tools/ci/spec-baseline.jsonl'
MANIFEST = 'tools/ci/spec-baseline-files.jsonl'
POLICY = ('tools/ci/spec-verdicts.json', 'tools/ci/structure-debt.md')
OPTIONAL_POLICY = ('docs/OWNERSHIP.md', 'tools/Test-PreReview.ps1')
ENFORCEMENT = ('tools/ci/check-spec-ratchet.py', 'tools/ci/spec_file_identity.py',
               'tools/ci/spec_cslex.py', 'tools/ci/spec_reviews.py',
               'tools/ci/test-spec-ratchet.py', '.github/workflows/sync-shark-ci.yml')


def git(root, *args):
    result = subprocess.run(['git', '-C', str(root), *args], capture_output=True)
    if result.returncode:
        raise ValueError('trusted Git evidence unavailable: ' + ' '.join(args[:2]))
    return result.stdout.decode('utf-8').strip()


def snapshot(root, tag, commit, target):
    """The caller supplies both owner-selected tag and immutable commit, never props."""
    if not tag or not re.fullmatch(r'v[0-9]+(?:\.[0-9]+){2}', tag):
        raise ValueError('release owner must supply RATCHET_LIVE_TAG')
    if not commit or not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('release owner must supply RATCHET_LIVE_COMMIT')
    resolved = git(root, 'rev-parse', '--verify', 'refs/tags/' + tag + '^{commit}')
    if resolved != commit:
        raise ValueError('live tag does not match the owner-pinned commit')
    optional = git(root, 'ls-tree', '-r', '--name-only', commit, '--', *OPTIONAL_POLICY).splitlines()
    archive = target.parent / 'release.tar'
    with archive.open('wb') as stream:
        result = subprocess.run(['git', '-C', str(root), 'archive', commit,
                                 'src', 'tools/ci', '.github/workflows', 'tests/SyncShark.Engine.Tests/Layout',
                                 *optional], stdout=stream,
                                stderr=subprocess.PIPE)
    if result.returncode:
        raise ValueError('trusted release archive unavailable')
    with tarfile.open(archive) as source:
        for member in source:
            destination = target / member.name
            if (not destination.resolve().is_relative_to(target.resolve()) or
                    not (member.isfile() or member.isdir())):
                raise ValueError('unsafe trusted archive member')
            if member.isdir():
                destination.mkdir(parents=True, exist_ok=True)
            else:
                destination.parent.mkdir(parents=True, exist_ok=True)
                with source.extractfile(member) as incoming, destination.open('wb') as output:
                    shutil.copyfileobj(incoming, output)
    print('TRUSTED RELEASE', tag, commit)


def policy_findings(root, trusted):
    """Report every candidate policy edit; candidate review evidence remains usable."""
    failures = 0
    paths = {BASELINE, MANIFEST, *POLICY, *ENFORCEMENT, *OPTIONAL_POLICY}
    for folder in ('tools/ci', '.github/workflows'):
        paths.update(p.relative_to(trusted).as_posix() for p in (trusted / folder).glob('*') if p.is_file())
        paths.update(p.relative_to(root).as_posix() for p in (root / folder).glob('*') if p.is_file())
    paths.update(('tests/SyncShark.Engine.Tests/Layout/LiteralsChecks.cs',
                  'tests/SyncShark.Engine.Tests/Layout/LiteralSites.cs'))
    for relative in sorted(paths):
        old, new = trusted / relative, root / relative
        if new.is_symlink():
            raise ValueError('candidate policy symlink: ' + relative)
        if relative in (BASELINE, MANIFEST, *POLICY, *ENFORCEMENT) and not old.is_file():
            raise ValueError('trusted file missing: ' + relative)
        old_bytes = old.read_bytes().replace(b'\r\n', b'\n') if old.is_file() else None
        new_bytes = new.read_bytes().replace(b'\r\n', b'\n') if new.is_file() else None
        if old_bytes != new_bytes:
            print('POLICY REVIEW', 'removed' if new_bytes is None else 'changed', relative)
        if new_bytes is None and relative in (BASELINE, MANIFEST, *POLICY, *ENFORCEMENT):
            print('FAIL removed required evidence', relative)
            failures += 1
    return failures


def baseline_findings(root, trusted, connection, ratchet):
    renames = ratchet.identity.match_paths(root, trusted / MANIFEST, connection,
                                          ratchet.lex, ratchet.type_declarations, ratchet.safe_key)
    connection.execute('CREATE TABLE allowances (identity TEXT PRIMARY KEY, value INTEGER)')
    # Baseline slices stay per entity, not totals: an unrelated removal cannot buy a raise.
    def key(row):
        path, category, owner, signature = row
        return json.dumps((renames.get(path, path), category, owner, signature))
    for row, value in ratchet.baseline_rows(trusted / BASELINE):
        connection.execute('INSERT INTO allowances VALUES (?, ?)', (key(row), value))
    failures = 0
    for row, value in ratchet.baseline_rows(root / BASELINE):
        prior = connection.execute('SELECT value FROM allowances WHERE identity=?', (key(row),)).fetchone()
        if prior is None or value > prior[0]:
            print('FAIL candidate baseline added/raised', ' | '.join(row), value)
            failures += 1
        connection.execute('DELETE FROM allowances WHERE identity=?', (key(row),))
    return failures


def findings(root, ratchet, connection, tree):
    """Stream content-bound sites owned by enclosing type, never member spelling or line."""
    for path in ratchet.identity.source_files(root):
        relative = path.relative_to(root).as_posix()
        source = path.read_text(encoding='utf-8-sig')
        spans = []
        masked = ratchet.lex.mask(source, spans)
        declarations = sorted(ratchet.type_declarations(masked), key=lambda t: t[2])
        prefix = ratchet.identity.type_prefix(relative, masked)
        owners = []
        for name, kind, start, opening, end in declarations:
            owner = prefix + '.'.join([t[0] for t in declarations if t[3] < start < t[4]] + [name])
            owners.append((start, end, owner))
        sites = []
        permitted = literal_sites.consume(connection, tree, relative, source, spans)
        ratchet.text_count(source, masked, spans, path, lambda a, b: sites.append(('user-strings', a, b)), permitted)
        ratchet.tunable_count(masked, path, lambda a, b: sites.append(('magic-tunables', a, b)))
        if 'Storage' not in Path(relative).parts:
            sites.extend(('data-outside-storage', m.start(), m.end())
                         for m in re.finditer(r'\bMicrosoft\s*\.\s*Data\s*\.', masked))
        for category, start, end in sites:
            enclosing = [(b - a, owner) for a, b, owner in owners if a <= start <= b]
            owner = min(enclosing)[1] if enclosing else prefix + '@file:' + relative
            digest = ratchet.reviews.content_hash(source[start:end], ratchet.lex)
            yield category, ratchet.safe_key(owner), digest, ratchet.safe_key(relative)


def compare_findings(root, trusted, connection, ratchet):
    connection.execute('CREATE TABLE sites (category TEXT, owner TEXT, hash TEXT, path TEXT, delta INTEGER)')
    connection.execute('CREATE INDEX site_identity ON sites(category, owner, hash)')
    for tree, label, delta in ((trusted, 'release', -1), (root, 'candidate', 1)):
        connection.executemany('INSERT INTO sites VALUES (?, ?, ?, ?, ?)',
                               ((*row, delta) for row in findings(tree, ratchet, connection, label)))
    literal_sites.finish(connection)
    failures = 0
    for category, owner, digest, path, count in connection.execute(
            'SELECT category, owner, hash, MIN(path), SUM(delta) FROM sites '
            'GROUP BY category, owner, hash HAVING SUM(delta)>0 ORDER BY category, owner, hash'):
        print('NEW FINDING', category, '|', owner, '|', digest, '|', path, '| count=' + str(count))
        failures += count
    return failures


def measured_baseline(trusted, path, ratchet, permissions=None):
    """Measure the shipped tree without changing its committed baseline or ceilings."""
    with closing(sqlite3.connect(str(path.parent / 'measured.sqlite'))) as connection:
        connection.execute('CREATE TABLE rows (key TEXT PRIMARY KEY, value INTEGER, old INTEGER)')
        for key, value in ratchet.baseline_rows(trusted / BASELINE):
            connection.execute('INSERT INTO rows VALUES (?, ?, ?)', (json.dumps(key), value, value))
        connection.execute('UPDATE rows SET value=0')
        for key, value in ratchet.scan(trusted, permissions=permissions):
            connection.execute('INSERT INTO rows VALUES (?, ?, 0) ON CONFLICT(key) DO UPDATE SET value=excluded.value',
                               (json.dumps(key), value))
        for key, value, old in connection.execute('SELECT key, value, old FROM rows ORDER BY key'):
            row = tuple(json.loads(key))
            if row[1] in ratchet.COUNTED and value != old:
                print('INHERITED DEBT slice', ' | '.join(row), str(old) + ' -> ' + str(value),
                      'delta=' + str(value - old))
        for category in ratchet.COUNTED:
            value, old = connection.execute('SELECT COALESCE(SUM(value),0), COALESCE(SUM(old),0) FROM rows '
                                            'WHERE json_extract(key, \'$[1]\')=?', (category,)).fetchone()
            print('INHERITED DEBT total', category, str(old) + ' -> ' + str(value), 'delta=' + str(value - old))
        connection.create_function('decoded', 1, lambda value: ratchet.order_key(((value,), 0))[0])
        order = ', '.join("decoded(json_extract(key, '$[" + str(i) + "]'))" for i in range(4))
        with path.open('w', encoding='utf-8', newline='\n') as stream:
            for key, value in connection.execute('SELECT key, value FROM rows WHERE value>0 ORDER BY ' + order):
                ratchet.write_row(stream, (tuple(json.loads(key)), value))
    ratchet.identity.write_manifest(trusted, ratchet.identity_manifest(path),
                                   ratchet.lex, ratchet.type_declarations, ratchet.safe_key)


def check(root, tag, commit, ratchet, artifact=None):
    if artifact is None:
        raise ValueError("literal artifact: missing artifact")
    with tempfile.TemporaryDirectory(prefix='spec-trusted-') as directory:
        home = Path(directory)
        trusted = home / 'release'
        snapshot(root, tag, commit, trusted)
        failures = policy_findings(root, trusted)
        with closing(sqlite3.connect(str(home / 'findings.sqlite'))) as connection:
            literal_sites.load(connection, artifact)
            if (root / BASELINE).is_file():
                failures += baseline_findings(root, trusted, connection, ratchet)
            failures += compare_findings(root, trusted, connection, ratchet)
            measured = home / 'measured.jsonl'
            measured_baseline(trusted, measured, ratchet,
                              lambda path: literal_sites.permitted(connection, 'release', path))
            comparison_failed = ratchet.compare(root, measured,
                                                lambda path: literal_sites.permitted(connection, 'candidate', path))
        failed = bool(failures or comparison_failed)
        print('FAIL' if failed else 'PASS', 'trusted findings=' + str(failures),
              'comparison=' + ('FAIL' if comparison_failed else 'PASS'))
        return int(failed)
