#!/usr/bin/env python3
"""Non-increasing engineering-spec debt; run with python -I.

Threshold source: full-spec-audit.md checklist 9/10 and M3 (standards 1.3):
functions >40 physical lines; files >300 physical lines. Class declarations
also use 300, per declaration/file, NOT an invented aggregate partial limit.
Attributes are excluded from function spans, as in the audit's scan_long.py.
No automatic exemption from a comment: justifications still require review.

M5/M6 (standards 2.1/2.2/2.4): repository-wide heuristic counts reuse the markers in
swarm/scripts/classify.py and scan_tunables.py behind strings.csv/tunables.csv.
They include exception text, CLI vocabulary, literal durations, tunable-named
initializers/defaults, threshold/call literals and global settings reads.
Strings, tunables and Microsoft.Data references use repository totals; per-file
changes are informational only. Existing per-file baseline slices are retained
for provenance; updates only remove/lower slices to match the new total. Counts
avoid brittle text identities and never store literal values. Replacement within
a count is invisible. Microsoft.Data.* is counted outside Storage. T1 owns edges.

The baseline was seeded once from 832e0009. There is deliberately no bootstrap
CLI. --update-baseline remains atomic and remove-only, even for reviewed sizes.
Source is processed one file at a time; a temporary SQLite index orders comparison.
Members use project/namespace/type/signature, never file paths or line numbers.
Class sizes pair descending per-type declaration sizes. Size findings trigger
content-anchored review; literals and type-layout findings retain their ratchets.
File categories follow one-to-one type/content matches from the committed file
identity manifest. No Git executable or history is needed at runtime.
Baseline paths remain provenance only for members; remove-only updates retain
original spelling so rename lineage survives subsequent runs. '~' splits identifiers to
respect the repository privacy guard without storing literal text or hash IDs.
Repository-total regressions may be accepted only by tools/ci/ratchet-exceptions.json
for the release version stamped in Directory.Build.props and only up to its recorded
total; an entry from another release fails, an entry whose version already appears in
CHANGELOG.md fails, and any further growth still fails.
"""
import argparse
import collections
from contextlib import closing
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import sqlite3

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('spec_cslex', HERE / 'spec_cslex.py')
lex = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lex)
identity_spec = importlib.util.spec_from_file_location("spec_file_identity", HERE / "spec_file_identity.py")
identity = importlib.util.module_from_spec(identity_spec)
identity_spec.loader.exec_module(identity)
review_spec = importlib.util.spec_from_file_location('spec_reviews', HERE / 'spec_reviews.py')
reviews = importlib.util.module_from_spec(review_spec)
review_spec.loader.exec_module(reviews)
METHOD_LIMIT = 40
SIZE_LIMIT = 300
TYPE = re.compile(r'\b(class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(@?[A-Za-z_]\w*)')
DELEGATE = re.compile(r'\bdelegate\s+[^;{}=]+?\b(@?[A-Za-z_]\w*)\s*(?:<[^;{}]*>)?\s*\(')
TUNABLE = re.compile(
    r'Limit|Maximum|Minimum|Max|Min|Timeout|Delay|Interval|Size|Capacity|Cap|Batch|Page|Retry|'
    r'Poll|Window|Threshold|Buffer|Worker|Attempt|Chunk|Quota|Budget|Age|Grace|Rows|Cache|'
    r'Seconds|Minutes|Milliseconds|Hours|Span|Lease|Ttl|Backoff|Concurrency|Tracked|Entries|'
    r'Payload|Retention|Sample|Reserve|Floor|Bytes|Depth|Slots|Connections|Operations|Quiet|'
    r'Deadline|Characters', re.I)
NON_TUNABLE = re.compile(r'Rule|Version|Kind|Mode|Format|Prefix|Key|Code|HexLength|Encoding', re.I)
CALL = re.compile(r'\b(?:TimeSpan\s*\.\s*From\w+|new\s+TimeSpan|Delay|Sleep|Wait|WaitAsync|'
                  r'Take|Skip|Timeout|Limit|Batch|Page|Buffer|Capacity|Retry|Poll|Interval|Clamp|'
                  r'Max|Min|Workers|Chunk|Rows|Size|Threshold|AddSeconds|AddMinutes|'
                  r'AddMilliseconds|AddHours|AddDays|SemaphoreSlim|CancellationTokenSource)\s*\(')
NUMBER = re.compile(r'(?<![\w.])(?:0[xX][\da-fA-F_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?[fFdDmMlLuU]*)(?!\w)')
SQL = re.compile(r'^(?:SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|PRAGMA|BEGIN|COMMIT|'
                 r'ROLLBACK|WITH|VACUUM|ANALYZE|SAVEPOINT|RELEASE|ATTACH|DETACH|AND|OR|WHERE|'
                 r'FROM|JOIN|LEFT|INNER|OUTER|LIMIT|OFFSET|GROUP|ORDER|CASE|WHEN|THEN|ELSE|'
                 r'END|INDEXED|ON|USING|SET|VALUES|NOT|IS|COLLATE|RETURNING)\b', re.I)
LOG = re.compile(r'RunLog\s*\.\s*Trace|RunTrace|\bTrace\s*\(|\.Trace\s*\(|'
                 r'RunOutput\s*\.\s*Observe|Diagnostics\.|Debug\.|_log\b|logger\b|\bLog\s*\(', re.I)
DISPLAY = re.compile(r'throw\s+new|:\s*base\s*\(|new\s+\w*Exception\s*\(|'
                     r'RunOutput\s*\.\s*Write|Console\.|sink\s*\(|progress\s*\(|'
                     r'ProgressGuard\.Report|new\s+Notice|\b(?:Detail|Title|Reason|Message|Help|'
                     r'Summary|Label)\s*=|\b(?:Warn|Refuse|Describe)\s*\(')


def safe_key(text):
    """Injective splitting: escape existing separators first, then split long runs."""
    text = text.replace('~', '~~')
    return re.sub(r'[A-Za-z0-9_-]{33,}|[0-9a-fA-F]{16,}',
                  lambda m: '~'.join(m[0][i:i + 12] for i in range(0, len(m[0]), 12)), text)


def type_declarations(masked):
    """Include nested types and bodyless records/delegates; namespaces are not types."""
    for match in TYPE.finditer(masked):
        if match[1] == 'record' and match[2] in ('in', 'is', 'with', 'as', 'switch', 'and', 'or', 'not'):
            continue  # contextual keyword used as a variable, not a declaration
        # 'where T : class' is a constraint, not a declaration.
        if masked[max(0, match.start() - 2):match.start()].strip() == ':':
            continue
        i = match.end()
        while i < len(masked):
            if masked[i] in '([':
                i = lex.match_close(masked, i, masked[i], ')' if masked[i] == '(' else ']') + 1
            elif masked[i] == '{':
                yield match[2], match[1], match.start(), i, lex.match_close(masked, i, '{', '}')
                break
            elif masked[i] == ';':
                yield match[2], match[1], match.start(), i, i
                break
            else:
                i += 1
    for match in DELEGATE.finditer(masked):
        end = masked.find(';', match.end())
        if end >= 0:
            yield match[1], 'delegate', match.start(), end, end


def text_count(source, masked, spans, path):
    count = 0
    for start, end in spans:
        literal = source[start:end]
        value = literal.lstrip('$@"').rstrip('"').strip()
        if not value or not re.search(r'[A-Za-z]', value):
            continue
        prefix = masked[max(0, start - 250):start]
        line_start = source.rfind('\n', 0, start) + 1
        line_end = source.find('\n', end)
        line = masked[line_start:line_end if line_end >= 0 else len(source)]
        if SQL.match(value) or LOG.search(line):
            continue
        if re.match(r'^[\^\\]|\(\?<', value) or value.startswith(('http://', 'https://')):
            continue
        if re.search(r'(?:GetProperty|TryGetProperty|JsonPropertyName|ToString|Text|Monitor|'
                     r'NotInV2|NotInV2Text)\s*\(\s*$', prefix):
            continue
        # Include CLI flags and case vocabulary (the inventory labels these command).
        if value.startswith('--') or re.search(r'\bcase\s*$', prefix):
            count += 1
            continue
        if re.fullmatch(r'[a-z][a-z0-9]*(?:[-._:][a-z0-9]+)*:?', value):
            continue
        notice = re.search(r'Notice|Reasons?|Vocabulary|Presentation|Refusal|FailureText|ScanFailureText', path.name)
        if DISPLAY.search(prefix) or notice or (' ' in value and len(value) > 12):
            count += 1
    return count


def tunable_count(masked, path):
    if path.name in ('V2Settings.cs', 'EngineSettings.cs'):
        return 0
    # Count sites, not numeric tokens: 1024 * 1024 is one default, not two.
    sites = set()
    for match in CALL.finditer(masked):
        end = lex.match_close(masked, match.end() - 1, '(', ')')
        if NUMBER.search(masked[match.end():end]):
            sites.add(('call', match.start()))
    for match in re.finditer(r'\b(\w+)\s*=\s*(?!=|>)([^;{}\n]*)', masked):
        if TUNABLE.search(match[1]) and not NON_TUNABLE.search(match[1]) and NUMBER.search(match[2]):
            sites.add(('default', match.start()))
    for match in re.finditer(r'\b(?:[\w]+\.)*(count|elapsed|entries|attempts|size|age|depth|bytes)\s*[<>]=?\s*(\d[\d_]*)', masked, re.I):
        if match[2] not in ('0', '1'):
            sites.add(('threshold', match.start()))
    for match in re.finditer(r'\b(?:V2Settings|EngineSettings)\s*\.\s*Default\b', masked):
        sites.add(('global', match.start()))
    return len(sites)


def local_functions(masked, member):
    """Named local functions need their own keys even when the outer method shrinks."""
    if masked[member.body_start] != '{':
        return
    body = masked[member.body_start + 1:member.end - 1]
    header = re.compile(r'(?m)(?:^|(?<=[;{}]))\s*((?:(?:static|async|unsafe)\s+)*'
                        r'[\w.<>?,\[\]]+\s+@?\w+(?:<[^(){};]+>)?\s*\()')
    for match in header.finditer(body):
        first = match.start(1)
        if body[first:].split(None, 1)[0] in ('return', 'throw', 'await', 'new', 'yield', 'else'):
            continue
        close = lex.match_close(body, match.end() - 1, '(', ')')
        opening = close + 1
        while opening < len(body) and body[opening].isspace():
            opening += 1
        if body[opening:opening + 5] == 'where':
            opening = body.find('{', opening)
        if opening < 0 or opening >= len(body) or body[opening] != '{':
            continue
        end = lex.match_close(body, opening, '{', '}')
        info = lex.parse_header(body[first:opening])
        generic = '<' + str(info['arity']) + '>' if info['arity'] else ''
        signature = info['name'] + generic + '(' + ','.join(info['params']) + ')'
        size = body.count('\n', first, end + 1) + 1
        yield signature, size, member.body_start + 1 + first, member.body_start + 2 + end


def declared_members(masked, owner, kind, opening, closing, members):
    """Inventory declarations, including enum values and comma-separated fields."""
    if kind == 'enum':
        for part in lex.split_top(masked[opening + 1:closing], angles=False):
            head = re.sub(r'^\s*(?:\[[^\]]*\]\s*)*', '', part)
            name = re.match(r'@?\w+', head)
            if name:
                yield opening + 1, owner + ':enum:' + name[0]
    for member in members:
        if member.kind not in ('field', 'const', 'event'):
            yield member.start, owner + ':' + member.key()
            continue
        for part in lex.split_top(masked[member.start:member.end].rstrip(';'), angles=False):
            head = lex.split_top(part, '=', angles=False)[0].strip()
            name = re.search(r'@?\w+$', head)
            if name:
                yield member.start, owner + ':field:' + name[0]


def members_in_spans(members, spans):
    return [key for pos, key in members if any(a < pos < b for a, b in spans)]


def type_reason_start(masked, start):
    """Include leading attribute lists in the type's reason anchor."""
    first = masked.rfind('\n', 0, start) + 1
    prefix = masked[:first].rstrip()
    while prefix.endswith(']'):
        depth = 1
        opening = len(prefix) - 2
        while opening >= 0 and depth:
            depth += (prefix[opening] == ']') - (prefix[opening] == '[')
            opening -= 1
        if depth:
            break
        first = opening + 1
        prefix = masked[:first].rstrip()
    attribute = masked[first:start].find('[')
    return first + attribute if attribute >= 0 else first


# One parsed inventory supplies member, partial-class and file evidence without reparsing.
def collect_review_units(source, masked, declarations, relative):
    """Assemble size-review evidence from this file's parsed declarations."""
    def make(category, owner, signature, size, start, end, members, reason_start=None, parts=None):
        unit = reviews.describe(source, start, end, members, lex, reason_start, parts)
        unit['size'] = size
        unit['members'] = [safe_key(member) for member in unit['members']]
        return category, owner, signature, size, unit

    class_sizes = collections.Counter()
    class_spans = {}
    all_members = []
    for name, kind, start, opening, closing in declarations:
        owner = '.'.join([p[0] for p in declarations if p[3] < start < p[4]] + [name])
        all_members.append((start, 'type:' + owner))
        size = source.count('\n', start, closing + 1) + 1
        if kind in ('class', 'record', 'record class'):
            class_sizes[owner] += size
            class_spans.setdefault(owner, []).append((start, closing + 1))
        members = (lex.parse_members(relative, source, masked, name, [(start, opening, closing)])
                   if opening != closing and kind not in ('enum', 'delegate') else [])
        all_members.extend(declared_members(masked, owner, kind, opening, closing, members))
        for member in members:
            if member.kind in ('type', 'field', 'const', 'event') or member.body_start is None:
                continue
            first = member.start
            while masked[first] == '[':
                first = lex.match_close(masked, first, '[', ']') + 1
                while masked[first].isspace():
                    first += 1
            size = source.count('\n', first, member.end) + 1
            locals_ = list(local_functions(masked, member))
            if size > METHOD_LIMIT:
                yield make('long-member', owner, member.key(), size, first, member.end,
                           [v[0] for v in locals_], member.start)
            for signature, size, local_start, local_end in locals_:
                if size > METHOD_LIMIT:
                    yield make('long-member', owner, member.key() + '::' + signature, size,
                               local_start, local_end,
                               [v[0] for v in locals_ if local_start < v[2] < local_end])
    for owner, size in sorted(class_sizes.items()):
        if size > SIZE_LIMIT:
            spans_ = class_spans[owner]
            first, last = spans_[0][0], spans_[-1][1]
            yield make('class-size', owner, '', size, first, last,
                       members_in_spans(all_members, spans_), type_reason_start(masked, first), spans_)
    size = len(source.splitlines())
    if size > SIZE_LIMIT:
        first = type_reason_start(masked, declarations[0][2]) if declarations else 0
        yield make('file-size', '', '', size, 0, len(source), [key for _, key in all_members], first)


def file_rows(path, relative, review_unit=None):
    source = path.read_text(encoding='utf-8-sig')
    spans = []
    masked = lex.mask(source, spans)
    declarations = sorted(type_declarations(masked), key=lambda t: t[2])
    rows = {}
    namespace = re.search(r'\bnamespace\s+([\w.]+)\s*[;{]', masked)
    project = relative.split('/')[1] if len(relative.split('/')) > 2 else ''
    type_prefix = project + ':' + (namespace[1] + '.' if namespace else '')

    def add(category, owner, signature, value):
        if category in ('long-member', 'class-size'):
            owner = type_prefix + owner
        key = tuple(safe_key(x) for x in (relative, category, owner, signature))
        if key in rows:
            raise ValueError('ambiguous declaration key: ' + ' | '.join(key))
        rows[key] = value
        return key

    top = [t for t in declarations if not any(p[3] < t[2] < p[4] for p in declarations)]
    top_names = {t[0] for t in top}
    if len(top_names) > 1:
        add('top-level-types', '', '', len(top_names))
    for name in sorted(top_names):
        if path.stem.split('.')[0] != name.lstrip('@'):
            add('file-type-name', name, '', 1)
    for category, owner, signature, value, unit in collect_review_units(source, masked, declarations, relative):
        key = add(category, owner, signature, value)
        if review_unit is not None:
            review_unit(key, unit)
    for category, value in (('user-strings', text_count(source, masked, spans, path)),
                            ('magic-tunables', tunable_count(masked, path))):
        if value:
            add(category, '', '', value)
    if 'Storage' not in Path(relative).parts:
        count = len(re.findall(r'\bMicrosoft\s*\.\s*Data\s*\.', masked))
        if count:
            add('data-outside-storage', '', '', count)
    yield from sorted(rows.items(), key=order_key)


def scan(root, review_unit=None):
    src = root / 'src'
    if not src.is_dir():
        raise ValueError('source directory missing')
    # Per-directory sorting, then merge immediate files and child directories to
    # preserve lexical path order without materialising the entire source tree.
    def walk(folder):
        for child in sorted(folder.iterdir(), key=lambda p: p.name + ('/' if p.is_dir() else '')):
            if child.is_symlink():
                raise ValueError('symlink in source tree')
            if child.is_dir() and child.name not in ('bin', 'obj', '.git'):
                yield from walk(child)
            elif child.is_file() and child.suffix == '.cs':
                yield from file_rows(child, child.relative_to(root).as_posix(), review_unit)
    # safe_key splitting can change sort order, so sort by original path on both
    # sides via order_key; only rows for a single file are buffered.
    yield from walk(src)


def order_key(row):
    # '~' is escaped before insertion; decode split markers and literal escapes.
    return tuple(s.replace('~~', '\0').replace('~', '').replace('\0', '~') for s in row[0])


def baseline_rows(path):
    previous = None
    with path.open(encoding='utf-8') as stream:
        for line in stream:
            row = json.loads(line)
            key, value = tuple(row['key']), row['value']
            if len(key) != 4 or not all(isinstance(x, str) for x in key) or type(value) is not int or value < 1:
                raise ValueError('invalid baseline entry')
            item = (key, value)
            order = order_key(item)
            if previous is not None and order <= previous:
                raise ValueError('baseline is not unique and sorted')
            previous = order
            yield item


def write_row(stream, row):
    stream.write(json.dumps({'key': row[0], 'value': row[1]}, ensure_ascii=True) + '\n')


COUNTED = ('user-strings', 'magic-tunables', 'data-outside-storage')
COUNTED_SQL = '(' + ','.join("'" + category + "'" for category in COUNTED) + ')'
EXCEPTIONS = 'tools/ci/ratchet-exceptions.json'
VERSION_FILE = 'Directory.Build.props'
CHANGELOG = 'CHANGELOG.md'


def identity_manifest(baseline):
    return baseline.with_name(baseline.stem + '-files.jsonl')


def release_version(root):
    """Release version stamped in the repository's build defaults."""
    path = root / VERSION_FILE
    if not path.exists():
        return None
    match = re.search(r'<Version\b[^>]*>\s*([^<\s]+)\s*</Version>',
                      path.read_text(encoding='utf-8-sig'))
    return match[1] if match else None


def released_versions(root):
    """Versions with a released heading in CHANGELOG.md; [Unreleased] has no digits."""
    path = root / CHANGELOG
    if not path.exists():
        return set()
    return set(re.findall(r'^## \[([0-9]+(?:\.[0-9]+)*)\]',
                          path.read_text(encoding='utf-8-sig'), re.M))


def assert_exception_scope(entry, version, released):
    """An exception is valid only for the current, not-yet-released version."""
    if entry.get('version') != version:
        raise ValueError('ratchet exception for release ' + str(entry.get('version')) +
                         ' is not the current release ' + version)
    if entry['version'] in released:
        raise ValueError('ratchet exception for release ' + entry['version'] +
                         ' is already released; remove it')


def total_exceptions(root):
    """Accepted repository-total ceilings for the current release only."""
    version = release_version(root)
    path = root / EXCEPTIONS
    if not path.exists() or version is None:
        return {}
    released = released_versions(root)
    accepted = {}
    for entry in json.loads(path.read_text(encoding='utf-8-sig')):
        if entry.get('category') not in COUNTED:
            raise ValueError('ratchet exception has unknown total category: ' + str(entry.get('category')))
        if type(entry.get('accepted_total')) is not int or entry['accepted_total'] < 1:
            raise ValueError('ratchet exception needs a positive accepted_total')
        if not str(entry.get('reason', '')).strip():
            raise ValueError('ratchet exception needs a reason')
        assert_exception_scope(entry, version, released)
        if entry['category'] in accepted:
            raise ValueError('duplicate ratchet exception for ' + entry['category'])
        accepted[entry['category']] = entry
    return accepted


def exception_applies(accepted, category, value):
    entry = accepted.get(category)
    if entry is None or value > entry['accepted_total']:
        return False
    print('EXCEPTION', category, value, '<=', entry['accepted_total'], '|', entry['reason'])
    return True


def total_excepted(row, value, accepted):
    """Only a counted total row is excepted, and only within its ceiling."""
    return row[0][1] in COUNTED and exception_applies(accepted, row[0][1], value)


def canonical_rows(rows, connection, table, renames=None):
    """Disk-backed ordering: source files stream; no repository-sized Python maps.

    Class-size is a multiset of oversized declarations of the same type. Pair
    largest with largest, so partial moves cannot create or enlarge an allowance.
    Member identity is project/namespace/type/signature, independent of its file.
    Baseline file spelling is retained for remove-only updates and rename lineage.
    """
    connection.execute('CREATE TABLE ' + table + ' (identity TEXT, value INTEGER, original TEXT, category TEXT)')
    for key, value in rows:
        category = key[1]
        identity = canonical_key(key, renames)
        connection.execute('INSERT INTO ' + table + ' VALUES (?, ?, ?, ?)',
                           (json.dumps(identity), value, json.dumps(key), category))
    connection.commit()
    previous = None
    rank = 0
    for identity, value, original in connection.execute(
            'SELECT identity, value, original FROM ' + table + ' WHERE category NOT IN ' + COUNTED_SQL +
            ' UNION ALL SELECT identity, SUM(value), MIN(original) FROM ' + table +
            ' WHERE category IN ' + COUNTED_SQL + ' GROUP BY identity ORDER BY identity, value DESC, original'):
        key = tuple(json.loads(identity))
        rank = rank + 1 if identity == previous else 0
        if rank and key[1] == 'long-member':
            raise ValueError('ambiguous type/member identity: ' + identity)
        previous = identity
        if key[1] == 'class-size':
            key = key[:3] + ('declaration-' + str(rank).zfill(6),)
        yield key, value, tuple(json.loads(original))


def compare(root, baseline, update=False):
    accepted = total_exceptions(root)
    ledger = reviews.read_ledger(root)
    register_failures = reviews.check_register(root, ledger, lex)
    with tempfile.TemporaryDirectory(prefix='spec-ratchet-index-') as directory:
        with closing(sqlite3.connect(str(Path(directory) / 'index.sqlite'))) as connection:
            renames = identity.match_paths(root, identity_manifest(baseline), connection,
                                           lex, type_declarations, safe_key)
            verdicts = collections.defaultdict(list)
            for row in ledger['verdicts']:
                verdicts[canonical_key(row['key'], renames)].append(row)
            connection.execute('CREATE TABLE review_units (original TEXT PRIMARY KEY, unit TEXT)')
            def collect(key, unit):
                unit['identity'] = canonical_key(key, renames)
                connection.execute('INSERT INTO review_units VALUES (?, ?)', (json.dumps(key), json.dumps(unit)))
            current = iter(canonical_rows(scan(root, collect), connection, 'current_rows', renames))
            old = iter(canonical_rows(baseline_rows(baseline), connection, 'old_rows'))
            return compare_rows(current, old, baseline, update, connection, verdicts, register_failures, accepted)


def canonical_key(key, renames=None):
    path, category, owner, signature = key
    if category in COUNTED:
        return '@total', category, '', ''
    if category in ('long-member', 'class-size'):
        return '@type', category, owner, signature
    return (renames or {}).get(path, path), category, owner, signature


def size_review(row, connection, verdicts, required):
    if row[0][1] not in reviews.SIZE_CATEGORIES:
        return int(required)
    unit = json.loads(connection.execute('SELECT unit FROM review_units WHERE original=?',
                                        (json.dumps(row[2]),)).fetchone()[0])
    records = verdicts.get(tuple(unit['identity']), [])
    if not required and not records:
        return 0  # Unchanged unreviewed baseline debt remains unchanged.
    verdict = reviews.current_verdict(records, unit)
    print('REVIEW', verdict or 'REQUIRED', '|', ' | '.join(row[2]), '|', unit['hash'])
    return int(verdict is None)


def compare_rows(current, old, baseline, update, connection, verdicts=None, register_failures=0, accepted=None):
    a, b = next(current, None), next(old, None)
    failures, improved = register_failures, 0
    verdicts = verdicts or {}
    accepted = accepted or {}
    totals = collections.Counter()
    remaining = collections.Counter()
    for original, delta in connection.execute(
            'SELECT original, SUM(delta) FROM ('
            'SELECT original, value AS delta FROM current_rows WHERE category IN ' + COUNTED_SQL +
            ' UNION ALL SELECT original, -value AS delta FROM old_rows WHERE category IN ' + COUNTED_SQL +
            ') GROUP BY original HAVING SUM(delta) != 0 ORDER BY original'):
        print('INFO file delta', ' | '.join(json.loads(original)), '{:+d}'.format(delta))
    pending = None
    try:
        if update:
            connection.execute('CREATE TABLE lowered (original TEXT PRIMARY KEY, value INTEGER)')
            pending = tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', newline='\n',
                                                 dir=baseline.parent, prefix='.spec-ratchet-', delete=False)
        while a is not None or b is not None:
            if b is None or (a is not None and json.dumps(a[0]) < json.dumps(b[0])):
                print('NEW', ' | '.join(a[0]), a[1])
                failures += size_review(a, connection, verdicts, True)
                totals[a[0][1]] += 1
                a = next(current, None)
            elif a is None or json.dumps(b[0]) < json.dumps(a[0]):
                print('improved', ' | '.join(b[0]), str(b[1]) + ' -> 0')
                improved += 1
                b = next(old, None)
            else:
                totals[a[0][1]] += 1
                if a[1] > b[1]:
                    print('GROWN', ' | '.join(a[0]), str(b[1]) + ' -> ' + str(a[1]))
                if a[1] > b[1] and total_excepted(a, a[1], accepted):
                    pass
                else:
                    failures += size_review(a, connection, verdicts, a[1] > b[1])
                if a[1] < b[1]:
                    print('improved', ' | '.join(a[0]), str(b[1]) + ' -> ' + str(a[1]))
                    improved += 1
                if pending is not None:
                    if a[0][1] in COUNTED:
                        remaining[a[0][1]] = a[1]
                    else:
                        connection.execute('INSERT INTO lowered VALUES (?, ?)', (json.dumps(b[2]), min(a[1], b[1])))
                a, b = next(current, None), next(old, None)
        if pending is not None:
            for key, value in baseline_rows(baseline):
                if key[1] in COUNTED:
                    # Historical per-file entries are allowance slices, not present
                    # locations. Keep/lower them in order; never add or raise one.
                    retained = min(value, remaining[key[1]])
                    remaining[key[1]] -= retained
                    if retained:
                        write_row(pending, (key, retained))
                    continue
                lowered = connection.execute('SELECT value FROM lowered WHERE original=?',
                                             (json.dumps(key),)).fetchone()
                if lowered is not None:
                    write_row(pending, (key, lowered[0]))
            pending.close()
            if failures:
                print('REFUSED baseline update: additions/increases are forbidden')
            else:
                os.replace(pending.name, baseline)
        for category, count in sorted(totals.items()):
            print(category, count, 'baseline sites/files')
        print('FAIL' if failures else 'PASS', 'regressions=' + str(failures), 'improved=' + str(improved))
        return 1 if failures else 0
    finally:
        if pending is not None:
            pending.close()
            if os.path.exists(pending.name):
                os.unlink(pending.name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=HERE.parents[1])
    parser.add_argument('--baseline', type=Path, default=HERE / 'spec-baseline.jsonl')
    parser.add_argument('--update-baseline', action='store_true')
    parser.add_argument('--refresh-register', nargs='+', metavar='SOURCE_PATH',
                        help='refresh explicitly reviewed sections using the shared code hash')
    args = parser.parse_args()
    try:
        if args.refresh_register:
            reviews.refresh_register(args.root.resolve(), args.refresh_register, lex)
            return 0
        return compare(args.root.resolve(), args.baseline.resolve(), args.update_baseline)
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError, sqlite3.Error) as error:
        print('ERROR:', error, file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
