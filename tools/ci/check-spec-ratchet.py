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
Strings, tunables and Microsoft.Data references retain repository totals. The
trusted entry point also compares entity-owned content fingerprints, so removing
unrelated debt cannot finance new findings. Literal values are never printed.
Microsoft.Data.* is counted outside Storage. T1 owns edges.

The baseline was seeded once from 832e0009. There is deliberately no bootstrap
CLI. Reviewed candidate lowering is checked against immutable release rows.
Source is processed one file at a time; a temporary SQLite index orders comparison.
Members use project/namespace/type/signature, never file paths or line numbers.
Class sizes pair descending per-type declaration sizes. Size findings trigger
content-anchored review; literals and type-layout findings retain their ratchets.
File categories follow one-to-one type/content matches from the committed file
identity manifest. The trusted entry point requires the pinned release Git objects.
Baseline paths remain provenance only for members; original spelling retains
rename lineage. '~' splits identifiers to
respect the repository privacy guard without storing literal text or hash IDs.
The release owner supplies a pinned live tag outside candidate policy. Its
measured inventory is the comparison; committed baselines are never raised.
"""
import argparse
import collections
from contextlib import closing
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import types
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
trust_spec = importlib.util.spec_from_file_location('spec_trust', HERE / 'spec_trust.py')
trust = importlib.util.module_from_spec(trust_spec)
trust_spec.loader.exec_module(trust)
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


def text_count(source, masked, spans, path, finding=None, permitted=()):
    count = 0
    for start, end in spans:
        if start in permitted:
            continue
        literal = source[start:end]
        value = literal.removesuffix('u8').removesuffix('U8').lstrip('$@"').rstrip('"').strip()
        if not value or not re.search(r'[A-Za-z]', value):
            continue
        prefix = masked[max(0, start - 250):start]
        line_start = source.rfind('\n', 0, start) + 1
        line_end = source.find('\n', end)
        line = masked[line_start:line_end if line_end >= 0 else len(source)]
        if LOG.search(line):
            continue
        if re.match(r'^[\^\\]|\(\?<', value) or value.startswith(('http://', 'https://')):
            continue
        if re.search(r'(?:GetProperty|TryGetProperty|JsonPropertyName|ToString|Text|Monitor|'
                     r'NotInV2|NotInV2Text)\s*\(\s*$', prefix):
            continue
        # Include CLI flags and case vocabulary (the inventory labels these command).
        if value.startswith('--') or re.search(r'\bcase\s*$', prefix):
            count += 1
            if finding is not None:
                finding(start, end)
            continue
        if re.fullmatch(r'[a-z][a-z0-9]*(?:[-._:][a-z0-9]+)*:?', value):
            continue
        notice = re.search(r'Notice|Reasons?|Vocabulary|Presentation|Refusal|FailureText|ScanFailureText', path.name)
        if DISPLAY.search(prefix) or notice or (' ' in value and len(value) > 12):
            count += 1
            if finding is not None:
                finding(start, end)
    return count


def tunable_name(name):
    return any(TUNABLE.fullmatch(stem) for token in lex.IDENT.finditer(name)
               for word in re.split(r'_|(?<=[a-z])(?=[A-Z])', token[0].lstrip('@'))
               for stem in (word, re.sub(r'(?i)s$', '', word), re.sub(r'(?i)es$', '', word), re.sub(r'(?i)ies$', 'y', word)))


# Sourced fixed facts (std 2.3/2.4), each bound to its repo-relative file and its declaration site, named by the
# parsed owner type chain and member: 'Type.Member' for a field or const, 'Type.Member[Attribute].Argument' for an
# attribute argument on that member (a nested type is a member of its outer type); a key naming more than one site in
# the file clears nothing. A method local is never a fact (R123: an exact reviewed receipt binds one instead). The
# initializer must equal the entry
# token for token (spec_reviews.CODE_TOKEN). An entry without a source clears nothing; the cross-family review checks
# that the source states the fact. Another file, owner, member, attribute, site or value stays counted.
ENGINE = 'src/SyncShark.Engine/'
RELEASE_FACTS = '08a9d06b2 docs/known-facts.md '  # the released 0.3.95 known-facts reference
PROTOCOL_FACTS = {
    (ENGINE + 'ContentHash.cs', 'ContentHash.DropboxBlockSize'): (
        '4 * 1024 * 1024', RELEASE_FACTS + 'D11: 4,194,304-byte content-hash blocks (Dropbox content-hash reference)'),
    (ENGINE + 'Dropbox/CredentialStore.cs', 'CredentialStore.MaximumTokenBytes'): (
        '2560', 'wincred.h CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512 bytes'),
    (ENGINE + 'Dropbox/ConnectionListFormat.cs', 'ConnectionListFormat.MaximumAppendRecords'): (
        '100', 'released append format physical record maximum, 08a9d06b2 src/SyncShark.Core/AppendOnlyJsonStore.cs PhysicalMaximumRecords'),
    (ENGINE + 'Dropbox/DropboxApi.cs', 'DropboxApi.DateResolutionSeconds'): (
        '1', 'RFC 9110 section 5.6.7: HTTP-date has one-second resolution'),
    (ENGINE + 'Dropbox/DropboxApi.cs', 'DropboxApi.LongpollClientAllowanceSeconds'): (
        '90', RELEASE_FACTS + 'D12 and D16: up to 90 s longpoll jitter, socket allowance = requested timeout + 90 s'),
    (ENGINE + 'Dropbox/DropboxApi.cs', 'DropboxApi.LongpollMaxSeconds'): (
        '480', RELEASE_FACTS + 'D12: files/list_folder/longpoll timeout 30-480 s'),
    (ENGINE + 'Dropbox/RateLimit.cs', 'RateLimit.MinimumLevel'): (
        '1', 'D33 as cited on the constant: one busy period halves the admission level, never below one'),
    (ENGINE + 'Dropbox/ResponseStream.cs', 'ResponseStream.ReadAllowanceSeconds'): (
        '120', RELEASE_FACTS + 'D15: dropbox-sdk-java HttpRequestor.DEFAULT_READ_TIMEOUT_MILLIS, 2 min'),
    (ENGINE + 'Dropbox/Routes.cs', 'Routes.BytesPerMiB'): (
        '1024L * 1024L', 'IEC 80000-13: 1 MiB = 2^20 bytes'),
    (ENGINE + 'Edge/PipeFrame.cs', 'PipeFrame.MaximumPayload'): (
        '1024 * 1024', 'released host frame maximum, 08a9d06b2 src/SyncShark.Core/V2/Adapters/AutoSyncHostProtocol.cs MaximumFrameBytes'),
    (ENGINE + 'Local/DirectoryListing.cs', 'DirectoryListing.EaSizeOffset'): (
        '64', 'ntifs.h FILE_ID_BOTH_DIR_INFORMATION: EaSize follows the 64 bytes of preceding fields'),
    (ENGINE + 'Local/DirectoryListing.cs', 'DirectoryListing.MaxNameCharacters'): (
        '255', 'learn.microsoft.com/windows/win32/fileio/filesystem-functionality-comparison: NTFS maximum file name length 255 Unicode characters'),
    (ENGINE + 'Local/IdentityPaths.cs', 'IdentityPaths.FileIdDescriptor[StructLayout].Size'): (
        '24', 'winbase.h FILE_ID_DESCRIPTOR: dwSize, Type and a 16-byte union, 24 bytes'),
    (ENGINE + 'Local/IdentityPaths.cs', 'IdentityPaths.StatusBufferOverflow'): (
        'unchecked((int)0x80000005)', 'ntstatus.h STATUS_BUFFER_OVERFLOW = 0x80000005'),
    (ENGINE + 'Local/InUse.cs', 'InUse.ProcessInfo.Name[MarshalAs].SizeConst'): (
        '256', 'restartmanager.h RM_PROCESS_INFO.strAppName[CCH_RM_MAX_APP_NAME + 1], CCH_RM_MAX_APP_NAME = 255'),
    (ENGINE + 'Local/InUse.cs', 'InUse.ProcessInfo.Service[MarshalAs].SizeConst'): (
        '64', 'restartmanager.h RM_PROCESS_INFO.strServiceShortName[CCH_RM_MAX_SVC_NAME + 1], CCH_RM_MAX_SVC_NAME = 63'),
    (ENGINE + 'Local/UsnJournal.cs', 'UsnJournal.ErrorInvalidUserBuffer'): (
        '1784', 'winerror.h ERROR_INVALID_USER_BUFFER = 1784'),
    (ENGINE + 'Local/JournalDevice.cs', 'JournalDevice.JournalStateBufferBytes'): ('80', 'winioctl.h USN_JOURNAL_DATA_V2 is 80 bytes'),
    (ENGINE + 'Local/JournalDevice.cs', 'JournalDevice.JournalStateBytes'): ('56', 'winioctl.h USN_JOURNAL_DATA_V0 is 56 bytes'),
    (ENGINE + 'Local/UsnJournal.cs', 'UsnJournal.ReadJournalDataBytes'): ('40', 'winioctl.h READ_USN_JOURNAL_DATA_V0 is 40 bytes'),
    (ENGINE + 'Local/UsnJournal.cs', 'UsnJournal.UsnRecordHeaderBytes'): ('60', 'winioctl.h USN_RECORD_V2: FileName starts at offset 60'),
    (ENGINE + 'Storage/DeletedNamesCodec.cs', 'DeletedNamesCodec.DeletedBlockRows'): (
        '5000', 'released v16 DDL CHECK(name_count BETWEEN 1 AND 5000), 08a9d06b2 src/SyncShark.Engine/Storage/StateSchema.cs:554'),
}
ASSIGNED = re.compile(r'\b(@?\w+)\s*=\s*(?![=>])')


def initializer(text):
    """The assigned expression up to its own end: a top-level comma or semicolon, a closing bracket or the text end."""
    depth = 0
    for i, char in enumerate(text):
        if char in '([{':
            depth += 1
        elif char in ')]}':
            if depth == 0:
                return text[:i]
            depth -= 1
        elif char in ',;' and depth == 0:
            return text[:i]
    return text


def depth_zero(text, end):
    return sum((c in '([{') - (c in ')]}') for c in text[:end]) == 0


def declaration_sites(masked, owner, member, sites):
    """Record one parsed member's declaration sites: name start -> (declaration, initializer)."""
    head = member.start
    while masked[head] == '[':
        close = lex.match_close(masked, head, '[', ']')
        text = masked[head + 1:close]
        for argument in ASSIGNED.finditer(text):
            opening = text.rfind('(', 0, argument.start())
            attribute = re.search(r'(\w+)\s*$', text[:opening]) if opening >= 0 else None
            if (attribute and depth_zero(text, attribute.start()) and
                    depth_zero(text[opening + 1:], argument.start() - opening - 1)):
                key = owner + '.' + member.name + '[' + attribute[1] + '].' + argument[1]
                sites[head + 1 + argument.start()] = (key, initializer(text[argument.end():]))
        head = close + 1
        while masked[head].isspace():
            head += 1
    if member.kind in ('field', 'const'):
        text = masked[head:member.end]
        for name in ASSIGNED.finditer(text):
            if depth_zero(text, name.start()):
                sites[head + name.start()] = (owner + '.' + name[1], initializer(text[name.end():]))


def member_index(masked, path):
    """Every parsed non-type member, in source order, and the declaration sites a protocol fact can name."""
    declarations = sorted(type_declarations(masked), key=lambda t: t[2])
    members, sites = [], {}
    for name, kind, start, opening, closing in declarations:
        if opening == closing or kind in ('enum', 'delegate'):
            continue
        owner = '.'.join([p[0] for p in declarations if p[3] < start < p[4]] + [name])
        for member in lex.parse_members(str(path), masked, masked, name, [(start, opening, closing)]):
            if member.kind != 'type':
                members.append(member)
            declaration_sites(masked, owner, member, sites)
    names = collections.Counter(key for key, _ in sites.values())  # an ambiguous key names no single site
    return sorted(members, key=lambda m: m.start), {p: s for p, s in sites.items() if names[s[0]] == 1}


def enclosing(members, position):
    return next((m for m in members if m.start <= position < m.end), None)


DIRECTIVE = re.compile(r'(?m)^[ \t]*#')  # any preprocessor line; content_hash skips directives (R127, R7-4)


def ledger_rows(root):
    path = root / reviews.LEDGER
    return json.loads(path.read_text(encoding='utf-8-sig'))['verdicts'] if path.is_file() else []


def sink_member(root, row, sources):
    """The one member a sink row names, when its whole text still has the reviewed hash and holds no directive."""
    relative = row['key'][0]
    if relative not in sources:
        sources[relative] = (root / relative).read_text(encoding='utf-8-sig') if (root / relative).is_file() else None
    source = sources[relative]
    if source is None:
        return None
    members, _ = member_index(lex.mask(source, []), Path(relative))
    found = [m for m in members if m.owner == row['key'][2] and m.key() == row['key'][3]]
    if len(found) != 1 or DIRECTIVE.search(source[found[0].start:found[0].end]):
        return None
    member = found[0]
    return (member, members) if reviews.content_hash(source[member.start:member.end], lex) == row['hash'] else None


def split_digest(value):
    digest = hashlib.sha256(json.dumps(value).encode()).hexdigest()
    return '~'.join(digest[i:i + 12] for i in range(0, len(digest), 12))


def sink_set(root, rows):
    """R127 (R7-2): the hash of every reviewed sink row together, or None when any sink's body has changed. Every
    user-strings receipt and every sink row binds it (field 'sinks'), so an edit to any sink voids them all. R134 (Sol
    r8 B2): each entry also binds the content_hash of the sink's whole declaring file, so an overload added there (which
    can rebind an unchanged call) or any other edit to that file voids every sink and receipt too."""
    sources, entries = {}, []
    for row in rows:
        if row['key'][1] == reviews.SINK:
            if sink_member(root, row, sources) is None:
                return None
            entries.append(json.dumps([row['key'], row['hash'], row.get('parameter'), row.get('symbol'),
                                       reviews.content_hash(sources[row['key'][0]], lex)]))
    return split_digest(sorted(entries))


def const_readers(root):
    """R134 (Sol r8 B1): readers(name, declaring path) -> the sorted [path, content_hash] of every other repo .cs file
    that names that identifier inside a string literal or a nameof, the way a reflection consumer (GetField("Name"))
    reads a private const from outside its file. A private const receipt binds these files too (site_identities)."""
    files = collections.defaultdict(list)
    for path in sorted(root.rglob('*.cs')):
        parts = path.relative_to(root).parts
        if {'bin', 'obj', '.git'} & set(parts) or path.is_symlink():
            continue
        source = path.read_text(encoding='utf-8-sig')
        spans = []
        masked = lex.mask(source, spans)
        words = {w for a, b in spans for w in re.findall(r'\w+', source[a:b])}
        words.update(re.findall(r'\bnameof\s*\(\s*(?:[\w.]+\.)?(\w+)\s*\)', masked))
        for word in words:
            files[word].append(path)
    hashes = {}

    def readers(name, declaring):
        found = []
        for path in files.get(name, ()):
            relative = path.relative_to(root).as_posix()
            if relative != declaring:
                if relative not in hashes:
                    hashes[relative] = reviews.content_hash(path.read_text(encoding='utf-8-sig'), lex)
                found.append([relative, hashes[relative]])
        return found
    return readers


def reviewed_findings(root):
    """Exact-site reviewed false positives for a counted literal category (R122-R127): a FALSE-POSITIVE row of the
    verdict ledger keyed [path, category, owner type, Member.key()], with hash = the site's content_hash, occurrence =
    its ordinal among the same site text in that member, use = the content_hash of the whole enclosing member (of the
    whole file for a private const, site_identities), a written reason and, for user-strings, sinks = the current
    sink_set. Read without validation so a release ledger loads too; compare() validates the candidate ledger."""
    rows = ledger_rows(root)
    current, _ = accepted_sinks(root, rows)
    return {tuple(row['key']) + (row['hash'], row['occurrence'], row['use']) for row in rows
            if row['key'][1] in reviews.RECEIPT_CATEGORIES and row['verdict'] == 'FALSE-POSITIVE' and
            row.get('reason', '').strip() and type(row.get('occurrence')) is int and isinstance(row.get('use'), str) and
            (row['key'][1] != 'user-strings' or current is not None and row.get('sinks') == current)}


def site_identities(source, masked, relative, category, sites, readers=None):
    """Each site's receipt identity, in source order: (path, category, owner, Member.key(), hash, occurrence, use).
    R127: a member holding a preprocessor directive has none (R7-4); a field has none and a const has one only when it
    is private in a file with no partial type and no directive, its use being the whole file's hash (R7-1). R134 (B1):
    when another repo file names the const (const_readers), its use binds those files' hashes as well; a caller with no
    readers (None) gets no const identity, so it clears no const receipt."""
    members, _ = member_index(masked, Path(relative))
    whole = None if re.search(r'\bpartial\b', masked) or DIRECTIVE.search(source) else reviews.content_hash(source, lex)
    seen = collections.Counter()
    for start, end in sorted(sites):
        member = enclosing(members, start)
        if member is None or member.kind == 'field' or DIRECTIVE.search(source[member.start:member.end]):
            continue
        if member.kind == 'const':
            if member.access != 'private' or whole is None or readers is None:
                continue
            others = readers(member.name, relative)
            use = split_digest([whole, others]) if others else whole
        else:
            use = reviews.content_hash(source[member.start:member.end], lex)
        digest = reviews.content_hash(source[start:end], lex)
        occurrence = seen[(member.start, digest)]
        seen[(member.start, digest)] += 1
        yield start, (relative, category, member.owner, member.key(), digest, occurrence, use)


def reviewed_starts(receipts, source, masked, spans, relative, category, permitted=(), readers=None):
    """Starts of the counted sites that match exactly one receipt; a receipt matching no site or several clears none, and
    a copied literal, any edit to its member or a renamed member counts."""
    mine = {receipt for receipt in receipts if receipt[:2] == (relative, category)}
    if not mine:
        return set()
    sites = []
    if category == 'user-strings':
        text_count(source, masked, spans, Path(relative), lambda a, b: sites.append((a, b)), permitted)
    else:
        tunable_count(masked, Path(relative), lambda a, b: sites.append((a, b)))
    matched = collections.defaultdict(list)
    for start, identity_ in site_identities(source, masked, relative, category, sites, readers):
        if identity_ in mine:
            matched[identity_].append(start)
    return {starts[0] for starts in matched.values() if len(starts) == 1}


def accepted_sinks(root, rows):
    """R138 (Sol r10 R10-1): (sink set, [(row, member, parameter)]) when every sink row is an eligible reviewed SQL
    permission: FALSE-POSITIVE, unchanged (sink_set), naming a symbol and its own method's parameter, the reviewed
    reason 'parameter <p> is used only as SQL text' and sinks = the computed set. When any one is not, (None, []):
    no sink and no set-bound string receipt grants anything. sink_set stays the raw digest for authoring."""
    current = sink_set(root, rows)
    if current is None:
        return None, []
    accepted, sources = [], {}
    for row in rows:
        if row['key'][1] != reviews.SINK:
            continue
        helper, _, parameter = str(row.get('parameter', '')).rpartition('.')
        member, _ = sink_member(root, row, sources)  # every sink row matched while computing current
        if (row['verdict'] != 'FALSE-POSITIVE' or member.name != helper or row.get('sinks') != current or
                parameter not in member.param_names or not isinstance(row.get('symbol'), str) or
                'parameter ' + parameter + ' is used only as SQL text' not in row.get('reason', '')):
            return None, []
        accepted.append((row, member, parameter))
    return current, accepted


def sql_sinks(root):
    """Reviewed SQL sinks (R124, R127): the literals owner reports an argument bound to a parameter of a method declared
    in source as [path, offset, symbol, parameter], offset being the code-point offset of the method's identifier and
    symbol its Roslyn documentation ID (R7-3). It is SQL only when its accepted sink row (accepted_sinks) names that
    symbol and parameter and its member holds the offset. Any edit to any sink, or any ineligible sink row, voids every
    sink. Returns a predicate over the reported sink."""
    valid = collections.defaultdict(list)
    for row, member, parameter in accepted_sinks(root, ledger_rows(root))[1]:
        valid[(row['key'][0], row['symbol'], parameter)].append(member)

    def reaches(sink):
        relative, offset, symbol, parameter = sink
        return any(m.start <= offset < m.end for m in valid.get((relative, symbol, parameter), ()))
    return reaches


def protocol_fact(path, sites, match, facts):
    site = sites.get(match.start())
    entry = facts.get((path.as_posix(), site[0])) if site else None
    return (entry is not None and bool(entry[1].strip()) and
            reviews.CODE_TOKEN.findall(site[1]) == reviews.CODE_TOKEN.findall(entry[0]))


# Every number in a call or a tunable-named assignment counts (R124, R127): no index, bracket, accumulator-seed or
# one-holder role is inferred. A genuine non-policy number is cleared only by an exact reviewed receipt.
def tunable_count(masked, path, finding=None, facts=None, reviewed=()):
    """path is repo-relative: protocol facts are bound to their file."""
    if path.name in ('V2Settings.cs', 'EngineSettings.cs'):
        return 0
    facts = PROTOCOL_FACTS if facts is None else facts
    _, declared = member_index(masked, path)
    # Count sites, not numeric tokens: 1024 * 1024 is one default, not two.
    sites = {}
    for match in CALL.finditer(masked):
        end = lex.match_close(masked, match.end() - 1, '(', ')')
        if NUMBER.search(masked, match.end(), end):
            sites[('call', match.start())] = end + 1
    for match in re.finditer(r'\b(\w+)\s*=\s*(?!=|>)([^;{}\n]*)', masked):
        if (tunable_name(match[1]) and not NON_TUNABLE.search(match[1]) and
                NUMBER.search(masked, match.start(2), match.end(2)) and not protocol_fact(path, declared, match, facts)):
            sites[('default', match.start())] = match.end()
    for match in re.finditer(r'\b(?:[\w]+\.)*(count|elapsed|entries|attempts|size|age|depth|bytes)\s*[<>]=?\s*(\d[\d_]*)', masked, re.I):
        if match[2] not in ('0', '1'):
            sites[('threshold', match.start())] = match.end()
    for match in re.finditer(r'\b(?:V2Settings|EngineSettings)\s*\.\s*Default\b', masked):
        sites[('global', match.start())] = match.end()
    sites = {key: end for key, end in sites.items() if key[1] not in reviewed}
    if finding is not None:
        for (_, start), end in sorted(sites.items()):
            finding(start, end)
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


def file_rows(path, relative, review_unit=None, permissions=None, receipts=(), readers=None):
    source = path.read_text(encoding='utf-8-sig')
    spans = []
    masked = lex.mask(source, spans)
    declarations = sorted(type_declarations(masked), key=lambda t: t[2])
    rows = {}
    type_prefix = identity.type_prefix(relative, masked)

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
    permitted = set(permissions(relative)) if permissions else set()
    permitted |= reviewed_starts(receipts, source, masked, spans, relative, 'user-strings', permitted, readers)
    tunables = reviewed_starts(receipts, source, masked, spans, relative, 'magic-tunables', readers=readers)
    for category, value in (('user-strings', text_count(source, masked, spans, path, permitted=permitted)),
                            ('magic-tunables', tunable_count(masked, Path(relative), reviewed=tunables))):
        if value:
            add(category, '', '', value)
    if 'Storage' not in Path(relative).parts:
        count = len(re.findall(r'\bMicrosoft\s*\.\s*Data\s*\.', masked))
        if count:
            add('data-outside-storage', '', '', count)
    yield from sorted(rows.items(), key=order_key)


def scan(root, review_unit=None, permissions=None):
    src = root / 'src'
    if not src.is_dir():
        raise ValueError('source directory missing')
    receipts = reviewed_findings(root)
    readers = const_readers(root)
    # Per-directory sorting, then merge immediate files and child directories to
    # preserve lexical path order without materialising the entire source tree.
    def walk(folder):
        for child in sorted(folder.iterdir(), key=lambda p: p.name + ('/' if p.is_dir() else '')):
            if child.is_symlink():
                raise ValueError('symlink in source tree')
            if child.is_dir() and child.name not in ('bin', 'obj', '.git'):
                yield from walk(child)
            elif child.is_file() and child.suffix == '.cs':
                yield from file_rows(child, child.relative_to(root).as_posix(), review_unit, permissions, receipts, readers)
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


def identity_manifest(baseline):
    return baseline.with_name(baseline.stem + '-files.jsonl')


def canonical_rows(rows, connection, table, renames=None):
    """Disk-backed ordering: source files stream; no repository-sized Python maps.

    Class-size is a multiset of oversized declarations of the same type. Pair
    largest with largest, so partial moves cannot create or enlarge an allowance.
    Member identity is project/namespace/type/signature, independent of its file.
    Baseline file spelling is retained for diagnostics and rename lineage.
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


def compare(root, baseline, permissions=None):
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
            current = iter(canonical_rows(scan(root, collect, permissions), connection, 'current_rows', renames))
            old = iter(canonical_rows(baseline_rows(baseline), connection, 'old_rows'))
            return compare_rows(current, old, connection, verdicts, register_failures)


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


def compare_rows(current, old, connection, verdicts=None, register_failures=0):
    a, b = next(current, None), next(old, None)
    failures, improved = register_failures, 0
    verdicts = verdicts or {}
    totals = collections.Counter()
    for original, delta in connection.execute(
            'SELECT original, SUM(delta) FROM ('
            'SELECT original, value AS delta FROM current_rows WHERE category IN ' + COUNTED_SQL +
            ' UNION ALL SELECT original, -value AS delta FROM old_rows WHERE category IN ' + COUNTED_SQL +
            ') GROUP BY original HAVING SUM(delta) != 0 ORDER BY original'):
        print('INFO file delta', ' | '.join(json.loads(original)), '{:+d}'.format(delta))
    while a is not None or b is not None:
        if b is None or (a is not None and json.dumps(a[0]) < json.dumps(b[0])):
            print('NEW', ' | '.join(a[0]), a[1], '| candidate:', a[2][0])
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
                print('GROWN', ' | '.join(a[0]), str(b[1]) + ' -> ' + str(a[1]), '| candidate:', a[2][0])
            failures += size_review(a, connection, verdicts, a[1] > b[1])
            if a[1] < b[1]:
                print('improved', ' | '.join(a[0]), str(b[1]) + ' -> ' + str(a[1]))
                improved += 1
            a, b = next(current, None), next(old, None)
    for category, count in sorted(totals.items()):
        print(category, count, 'baseline sites/files')
    print('FAIL' if failures else 'PASS', 'regressions=' + str(failures), 'improved=' + str(improved))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=HERE.parents[1])
    parser.add_argument('--literal-sites', type=Path, default=os.environ.get('RATCHET_LITERAL_SITES'))
    parser.add_argument('--live-tag', default=os.environ.get('RATCHET_LIVE_TAG'))
    parser.add_argument('--live-commit', default=os.environ.get('RATCHET_LIVE_COMMIT'))
    parser.add_argument('--refresh-register', nargs='+', metavar='SOURCE_PATH',
                        help='refresh explicitly reviewed sections using the shared code hash')
    args = parser.parse_args()
    try:
        if args.refresh_register:
            reviews.refresh_register(args.root.resolve(), args.refresh_register, lex)
            return 0
        return trust.check(args.root.resolve(), args.live_tag, args.live_commit, types.SimpleNamespace(**globals()), args.literal_sites)
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError, sqlite3.Error) as error:
        print('INCOMPLETE:', error, file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
