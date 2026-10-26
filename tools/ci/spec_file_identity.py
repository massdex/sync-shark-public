"""History-free, conservative one-to-one identity matching for snapshot CI.

The committed manifest describes the original baseline source, not private Git
history. Source text is never stored. Digests are split into short pieces for the
privacy guard. A missing file can transfer its existing ceilings only to a new
path: never a copy, never two destinations. Ambiguous best matches fail closed.
"""
import hashlib
import json
import re


def describe(relative, source, lex, declarations):
    masked = lex.mask(source)
    types = sorted(declarations(masked), key=lambda t: t[2])
    namespace = re.search(r'\bnamespace\s+([\w.]+)\s*[;{]', masked)
    project = relative.split('/')[1] if len(relative.split('/')) > 2 else ''
    prefix = project + ':' + (namespace[1] + '.' if namespace else '')
    owners = sorted({prefix + t[0] for t in types
                     if not any(p[3] < t[2] < p[4] for p in types)})
    lines = {re.sub(r'\s+', '', line) for line in masked.splitlines()}
    # Drop brace-only and other trivial lines which make unrelated files alike.
    features = sorted({hashlib.sha256(line.encode()).hexdigest()[:12]
                       for line in lines if len(line) >= 8})
    digest = hashlib.sha256(re.sub(r'\s+', '', masked).encode()).hexdigest()
    return {'path': relative, 'types': owners,
            'fingerprint': [digest[i:i + 12] for i in range(0, len(digest), 12)],
            'features': features}


def source_files(root):
    def walk(folder):
        for child in sorted(folder.iterdir(), key=lambda p: p.name + ('/' if p.is_dir() else '')):
            if child.is_symlink():
                raise ValueError('symlink in source tree')
            if child.is_dir() and child.name not in ('bin', 'obj', '.git'):
                yield from walk(child)
            elif child.is_file() and child.suffix == '.cs':
                yield child
    yield from walk(root / 'src')


def write_manifest(root, path, lex, declarations, safe_key):
    """Fixture/one-time generation helper, deliberately not a ratchet CLI option."""
    with path.open('w', encoding='utf-8', newline='\n') as stream:
        for file in source_files(root):
            row = describe(file.relative_to(root).as_posix(), file.read_text(encoding='utf-8-sig'), lex, declarations)
            write_identity(stream, row, safe_key)


def write_identity(stream, row, safe_key):
    row = dict(row)
    row['path'] = safe_key(row['path'])
    row['types'] = [safe_key(owner) for owner in row['types']]
    stream.write(json.dumps(row, ensure_ascii=True) + '\n')


def match_paths(root, manifest, connection, lex, declarations, safe_key):
    for table in ('baseline_files', 'present_files'):
        connection.execute('CREATE TABLE ' + table +
                           ' (path TEXT PRIMARY KEY, types TEXT, fingerprint TEXT, features TEXT)')
    with manifest.open(encoding='utf-8') as stream:
        for line in stream:
            row = json.loads(line)
            connection.execute('INSERT INTO baseline_files VALUES (?, ?, ?, ?)',
                               (row['path'], json.dumps(row['types']), json.dumps(row['fingerprint']),
                                json.dumps(row['features'])))
    for file in source_files(root):
        row = describe(file.relative_to(root).as_posix(), file.read_text(encoding='utf-8-sig'), lex, declarations)
        connection.execute('INSERT INTO present_files VALUES (?, ?, ?, ?)',
                           (safe_key(row['path']), json.dumps([safe_key(t) for t in row['types']]),
                            json.dumps(row['fingerprint']), json.dumps(row['features'])))
    connection.execute('CREATE TABLE candidates (old TEXT, new TEXT, score REAL)')
    missing = 'SELECT * FROM baseline_files WHERE path NOT IN (SELECT path FROM present_files) ORDER BY path'
    appeared = 'SELECT * FROM present_files WHERE path NOT IN (SELECT path FROM baseline_files) ORDER BY path'
    for old_path, old_types, old_digest, old_features in connection.execute(missing):
        old_set = set(json.loads(old_features))
        for new_path, new_types, new_digest, new_features in connection.execute(appeared):
            new_set = set(json.loads(new_features))
            overlap = len(old_set & new_set) / max(1, len(old_set | new_set))
            same_type = old_types != '[]' and old_types == new_types
            if old_digest == new_digest:
                score = 3.0
            elif same_type:
                score = 1.0 + overlap
            elif overlap >= 0.5:
                score = overlap
            else:
                continue
            connection.execute('INSERT INTO candidates VALUES (?, ?, ?)', (old_path, new_path, score))
    # Mutual unique best matches; ties are not resolved by filename or iteration order.
    result = {}
    for old_path, new_path in connection.execute(
            'SELECT a.old, a.new FROM candidates a WHERE NOT EXISTS ('
            'SELECT 1 FROM candidates b WHERE '
            '((b.old=a.old AND b.new!=a.new) OR (b.new=a.new AND b.old!=a.old)) '
            'AND b.score>=a.score) ORDER BY a.new'):
        result[new_path] = old_path
    return result
