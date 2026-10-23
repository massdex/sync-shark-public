#!/usr/bin/env python3
"""Non-increasing engineering-spec debt; run with python -I.

Threshold source: full-spec-audit.md checklist 9/10 and M3 (standards 1.3):
functions >40 physical lines; files >300 physical lines. Class declarations
also use 300, per declaration/file, NOT an invented aggregate partial limit.
Attributes are excluded from function spans, as in the audit's scan_long.py.
No automatic exemption from a comment: justifications still require review.

M5/M6 (standards 2.1/2.2/2.4): per-file heuristic counts reuse the markers in
swarm/scripts/classify.py and scan_tunables.py behind strings.csv/tunables.csv.
They include exception text, CLI vocabulary, literal durations, tunable-named
initializers/defaults, threshold/call literals and global settings reads.
Counts avoid brittle text identities and never store literal values. They are
an approximation, not semantic proof: replacement within a count is invisible.
Microsoft.Data.* is forbidden outside a Storage directory. T1 owns other edges.

The baseline was seeded once from 832e0009. There is deliberately no bootstrap
CLI. --update-baseline is atomic and refuses ALL additions or increases. Source
is processed one file at a time; baseline comparison is a sorted streaming merge.
Keys use file/type/signature, never line numbers. '~' splits long identifiers to
respect the repository privacy guard without storing literal text or hash IDs.
"""
import argparse
import collections
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import tempfile

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('spec_cslex', HERE / 'spec_cslex.py')
lex = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lex)
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
        yield signature, size


def file_rows(path, relative):
    source = path.read_text(encoding='utf-8-sig')
    spans = []
    masked = lex.mask(source, spans)
    declarations = sorted(type_declarations(masked), key=lambda t: t[2])
    rows = {}

    def add(category, owner, signature, value):
        key = tuple(safe_key(x) for x in (relative, category, owner, signature))
        if key in rows:
            raise ValueError('ambiguous declaration key: ' + ' | '.join(key))
        rows[key] = value

    top = [t for t in declarations if not any(p[3] < t[2] < p[4] for p in declarations)]
    top_names = {t[0] for t in top}
    if len(top_names) > 1:
        add('top-level-types', '', '', len(top_names))
    for name in sorted(top_names):
        if path.stem.split('.')[0] != name.lstrip('@'):
            add('file-type-name', name, '', 1)
    class_sizes = collections.Counter()
    for name, kind, start, opening, closing in declarations:
        owner = '.'.join([p[0] for p in declarations if p[3] < start < p[4]] + [name])
        size = source.count('\n', start, closing + 1) + 1
        if kind in ('class', 'record', 'record class'):
            class_sizes[owner] += size
        if opening == closing or kind in ('enum', 'delegate'):
            continue
        for member in lex.parse_members(relative, source, masked, name, [(start, opening, closing)]):
            if member.kind in ('type', 'field', 'const', 'event') or member.body_start is None:
                continue
            first = member.start
            while masked[first] == '[':
                first = lex.match_close(masked, first, '[', ']') + 1
                while masked[first].isspace():
                    first += 1
            size = source.count('\n', first, member.end) + 1
            if size > METHOD_LIMIT:
                add('long-member', owner, member.key(), size)
            for signature, size in local_functions(masked, member):
                if size > METHOD_LIMIT:
                    add('long-member', owner, member.key() + '::' + signature, size)
    for owner, size in sorted(class_sizes.items()):
        if size > SIZE_LIMIT:
            add('class-size', owner, '', size)
    size = len(source.splitlines())
    if size > SIZE_LIMIT:
        add('file-size', '', '', size)
    for category, value in (('user-strings', text_count(source, masked, spans, path)),
                            ('magic-tunables', tunable_count(masked, path))):
        if value:
            add(category, '', '', value)
    if 'Storage' not in Path(relative).parts:
        count = len(re.findall(r'\bMicrosoft\s*\.\s*Data\s*\.', masked))
        if count:
            add('data-outside-storage', '', '', count)
    yield from sorted(rows.items(), key=order_key)


def scan(root):
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
                yield from file_rows(child, child.relative_to(root).as_posix())
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


def compare(root, baseline, update=False):
    current = iter(scan(root))
    old = iter(baseline_rows(baseline))
    a, b = next(current, None), next(old, None)
    failures = improved = 0
    totals = collections.Counter()
    pending = None
    try:
        if update:
            pending = tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', newline='\n',
                                                 dir=baseline.parent, prefix='.spec-ratchet-', delete=False)
        while a is not None or b is not None:
            if b is None or (a is not None and order_key(a) < order_key(b)):
                print('NEW', ' | '.join(a[0]), a[1])
                failures += 1
                totals[a[0][1]] += 1
                a = next(current, None)
            elif a is None or order_key(b) < order_key(a):
                print('improved', ' | '.join(b[0]), str(b[1]) + ' -> 0')
                improved += 1
                b = next(old, None)
            else:
                totals[a[0][1]] += 1
                if a[1] > b[1]:
                    print('GROWN', ' | '.join(a[0]), str(b[1]) + ' -> ' + str(a[1]))
                    failures += 1
                elif a[1] < b[1]:
                    print('improved', ' | '.join(a[0]), str(b[1]) + ' -> ' + str(a[1]))
                    improved += 1
                if pending is not None:
                    write_row(pending, a)
                a, b = next(current, None), next(old, None)
        if pending is not None:
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
    args = parser.parse_args()
    try:
        return compare(args.root.resolve(), args.baseline.resolve(), args.update_baseline)
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError) as error:
        print('ERROR:', error, file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
