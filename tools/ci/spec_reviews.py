"""Content-bound structural review evidence for the snapshot-safe spec ratchet."""
import hashlib
import json
from pathlib import Path
import re

SIZE_CATEGORIES = ('long-member', 'class-size', 'file-size')
VERDICTS = ('SPLIT-PENDING', 'REASON-RECORDED', 'FALSE-POSITIVE')
REGISTER = Path('tools/ci/structure-debt.md')
LEDGER = Path('tools/ci/spec-verdicts.json')
# Exact-site literal receipts (R124): key, site hash, occurrence and whole-member hash; every other row keeps key/hash.
RECEIPT_CATEGORIES = ('user-strings', 'magic-tunables')
SINK = 'sql-sink'  # a reviewed helper parameter that is only SQL text, bound to its whole member's hash
# One owner of the fields each public row may carry, by kind; the exporter projects through it.
COMMON_FIELDS = ('key', 'hash', 'size', 'members', 'verdict')
SIZE_FIELDS = COMMON_FIELDS + ('register',)
PUBLIC_FIELDS = {
    'long-member': SIZE_FIELDS, 'class-size': SIZE_FIELDS, 'file-size': SIZE_FIELDS,
    'magic-tunables': COMMON_FIELDS + ('occurrence', 'use', 'reason'),
    'user-strings': COMMON_FIELDS + ('occurrence', 'use', 'reason', 'sinks'),
    SINK: COMMON_FIELDS + ('parameter', 'symbol', 'reason', 'sinks'),
}
REGISTER_FIELDS = ('path', 'hash')
DIGEST = re.compile(r'(?:[a-f0-9]{12}~){5}[a-f0-9]{4}')
RECEIPT = re.compile(r'<!-- spec-source: ([a-f0-9~]+) -->')
# shortcut: generic close spacing is lexical; require receipt review until compiler tokens are available.
CODE_TOKEN = re.compile(r'\w+|>>>=|>>>|<<=|>>=|\?\?=|=>|->|\?\?|\?\.|\?\['
                        r'|\+\+|--|&&|\|\||==|!=|<=|>=|[+*/%&|^!-]=|<<|>>|::|\.\.|[^\w\s]')


def content_hash(source, lex):
    """SHA-256 of LF-normalized code, excluding comments/directives, retaining literals.

    Whitespace between code tokens is canonical; literal bytes (except CRLF) are
    significant. Split hex is the existing manifest's privacy-safe representation.
    """
    source = source.replace('\r\n', '\n').replace('\r', '\n')
    spans = []
    masked = lex.mask(source, spans, preserve_chars=True)
    tokens = []
    end = 0
    for start, stop in sorted(spans):
        if start < end:
            continue  # Interpolated literals may include nested literal spans.
        tokens.extend(CODE_TOKEN.findall(masked[end:start]))
        tokens.append(source[start:stop])
        end = stop
    tokens.extend(CODE_TOKEN.findall(masked[end:]))
    digest = hashlib.sha256(json.dumps(tokens, ensure_ascii=True).encode()).hexdigest()
    return '~'.join(digest[i:i + 12] for i in range(0, len(digest), 12))


def preceding_comment(source, start):
    prefix = source[:start].rstrip()
    if prefix.endswith('*/'):
        opening = prefix.rfind('/*')
        return opening >= 0 and bool(prefix[opening + 2:-2].strip(' *\r\n\t'))
    lines = prefix.splitlines()
    line = lines[-1].lstrip() if lines else ''
    return line.startswith('//') and bool(line.lstrip('/').strip())


def describe(source, start, end, members, lex, reason_start=None, parts=None):
    text = '\n'.join(source[a:b] for a, b in parts) if parts is not None else source[start:end]
    return {'hash': content_hash(text, lex), 'size': len(text.splitlines()),
            'members': sorted(set(members)),
            'reason': preceding_comment(source, start if reason_start is None else reason_start)}


def review_families(value):
    families = set(value.split('+'))
    if not families <= {'anthropic', 'openai', 'deepseek'}:
        raise ValueError('invalid reviewer family')
    return families


def read_ledger(root, private=False):
    path = root / LEDGER
    if not path.exists():
        return {'version': 1, 'verdicts': [], 'register_reviews': []}
    return validate_ledger(json.loads(path.read_text(encoding='utf-8-sig')), private)


def validate_ledger(data, private=False):
    if data['version'] not in ((1,) if private else (1, 2)):
        raise ValueError('unsupported structural verdict ledger version')
    public = data['version'] == 2
    seen = set()
    for row in data['verdicts']:
        key = row['key']
        if public and set(row) - set(PUBLIC_FIELDS.get(row['key'][1], ())):
            raise ValueError('public ledger carries private provenance')
        families = set() if public else review_families(row['family'])
        if (len(key) != 4 or not all(isinstance(x, str) for x in key)
                or row['verdict'] not in VERDICTS
                or type(row['size']) is not int or row['size'] < 1
                or not isinstance(row['members'], list)
                or not all(isinstance(x, str) for x in row['members'])
                or not DIGEST.fullmatch(row['hash'])):
            raise ValueError('invalid structural verdict')
        if not public and row['verdict'] != 'SPLIT-PENDING' and families <= {'deepseek'}:
            raise ValueError('non-split size verdict requires cross-family escalation')
        identity = (tuple(key), row['hash'])
        if key[1] in RECEIPT_CATEGORIES:
            # A literal receipt also names its occurrence and member hash, so one hash may bind several exact sites.
            if (row['verdict'] != 'FALSE-POSITIVE' or type(row.get('occurrence')) is not int or row['occurrence'] < 0
                    or not isinstance(row.get('use'), str) or not DIGEST.fullmatch(row['use'])
                    or not isinstance(row.get('reason'), str) or not row['reason'].strip()):
                raise ValueError('invalid literal receipt')
            identity += (row['occurrence'], row['use'])
        elif key[1] == SINK:
            if (row['verdict'] != 'FALSE-POSITIVE' or not isinstance(row.get('parameter'), str)
                    or not re.fullmatch(r'@?\w+\.@?\w+', row['parameter'])
                    or not isinstance(row.get('reason'), str) or not row['reason'].strip()
                    or not isinstance(row.get('symbol'), str) or not row['symbol'].startswith('M:')):
                raise ValueError('invalid SQL sink receipt')
            identity += (row['parameter'],)
        if key[1] in ('user-strings', SINK) and not DIGEST.fullmatch(str(row.get('sinks', ''))):
            raise ValueError('a user-strings receipt or SQL sink must bind the sink set')  # R127 (R7-2)
        if identity in seen:
            raise ValueError('duplicate structural verdict identity/hash')
        seen.add(identity)
        if row['verdict'] == 'SPLIT-PENDING':
            if row.get('register') != REGISTER.as_posix():
                raise ValueError('pending verdict must link to the structure register')
        elif 'register' in row:
            raise ValueError('only a pending verdict may carry the register path')
    for row in data['register_reviews']:
        if not isinstance(row.get('path'), str) or not row['path'].strip():
            raise ValueError('register exception requires a string source path')
        if not isinstance(row.get('hash'), str) or not DIGEST.fullmatch(row['hash']):
            raise ValueError('register exception requires a valid content hash')
        if public:
            if set(row) != set(REGISTER_FIELDS):
                raise ValueError('public ledger carries private provenance')
            continue
        review_families(row['family'])
        if not isinstance(row.get('reason'), str) or not row['reason'].strip():
            raise ValueError('register exception requires a reason and reviewer family')
    return data


def public_ledger(data):
    """Project a validated private ledger to its public form, keeping only each kind's published fields."""
    validate_ledger(data, True)
    result = {'version': 2, 'verdicts': [], 'register_reviews': []}
    for row in data['verdicts']:
        fields = PUBLIC_FIELDS[row['key'][1]]
        result['verdicts'].append({field: row[field] for field in fields if field in row})
    for row in data['register_reviews']:
        result['register_reviews'].append({field: row[field] for field in REGISTER_FIELDS if field in row})
    validate_ledger(result, False)
    return result


def current_verdict(records, unit):
    for row in records:
        # Growth is measured from the reviewed content, not a moving baseline.
        if unit['size'] * 10 > row['size'] * 11 or set(unit['members']) - set(row['members']):
            continue
        if row['verdict'] == 'REASON-RECORDED' and not unit['reason']:
            continue
        return row['verdict']
    return None


def register_sections(text):
    headings = list(re.finditer(r'^## (src/[^\r\n]+)\s*$', text, re.M))
    for index, heading in enumerate(headings):
        end = headings[index + 1].start() if index + 1 < len(headings) else len(text)
        yield heading[1].strip(), heading.start(), end, text[heading.start():end]


def register_source(root, relative):
    path = root / relative
    if not path.resolve().is_relative_to((root / 'src').resolve()) or path.is_symlink():
        raise ValueError('invalid structure register path: ' + relative)
    return path.read_text(encoding='utf-8-sig')


def check_register(root, ledger, lex, sink=None):
    path = root / REGISTER
    if not path.exists():
        if ledger['verdicts'] or ledger['register_reviews']:
            raise ValueError('structure register missing')
        return 0
    failures = 0
    sections = list(register_sections(path.read_text(encoding='utf-8-sig')))
    paths = {relative for relative, *_ in sections}
    pending = {row['key'][0] for row in ledger['verdicts'] if row['verdict'] == 'SPLIT-PENDING'}

    def record(relative, reason):
        # The gate reuses this rule through `sink`; CI keeps the printed form.
        if sink is None:
            print('REGISTER review required:', relative, reason)
        else:
            sink.append((relative, reason))

    for relative in sorted(pending - paths):
        record(relative, '(pending verdict has no section)')
        failures += 1
    for relative, _, _, section in sections:
        receipt = RECEIPT.search(section)
        try:
            source = register_source(root, relative)
        except FileNotFoundError:
            record(relative, '(listed source missing; reconcile its section)')
            failures += 1
            continue
        digest = content_hash(source, lex)
        exception = any(row['path'] == relative and row['hash'] == digest
                        for row in ledger['register_reviews'])
        if not exception and (receipt is None or receipt[1] != digest):
            record(relative, '(update its section or record a content-bound review reason)')
            failures += 1
    return failures


def refresh_register(root, paths, lex):
    """Refresh only explicitly named sections after their debt has been reviewed."""
    path = root / REGISTER
    text = path.read_text(encoding='utf-8-sig')
    wanted = set(paths)
    edits = []
    seen = set()
    for relative, start, end, section in register_sections(text):
        if relative in seen:
            raise ValueError('duplicate structure register section: ' + relative)
        seen.add(relative)
        if relative not in wanted:
            continue
        digest = content_hash(register_source(root, relative), lex)
        receipt = '<!-- spec-source: ' + digest + ' -->'
        if RECEIPT.search(section):
            section = RECEIPT.sub(receipt, section)
        else:
            heading, rest = section.split('\n', 1)
            section = heading + '\n\n' + receipt + '\n' + rest
        edits.append((start, end, section))
        wanted.remove(relative)
    if wanted:
        raise ValueError('paths absent from structure register: ' + ', '.join(sorted(wanted)))
    for start, end, section in reversed(edits):
        text = text[:start] + section + text[end:]
    path.write_text(text, encoding='utf-8', newline='\r\n')


def _main(argv):
    if len(argv) != 3:
        raise SystemExit('usage: spec_reviews.py <private-ledger> <public-ledger>')
    source, target = (Path(argument) for argument in argv[1:])
    data = json.loads(source.read_text(encoding='utf-8-sig'))
    # Keep non-ASCII text literal: the final-byte literal guard reads decoded text, not JSON escapes.
    target.write_bytes((json.dumps(public_ledger(data), indent=2, ensure_ascii=False) + '\n').encode('utf-8'))


if __name__ == '__main__':
    import sys
    _main(sys.argv)
