"""C#-aware masking and class-member parsing for the spec ratchet (stdlib only).
Adapted from the engineering audit lexer; no external packages.

mask(src) returns a string of the same length in which comments, preprocessor lines, string and char literal
text are blanked to spaces (newlines kept), while interpolation holes stay as code. All offsets are preserved.
"""
import re

IDENT = re.compile(r'@?[A-Za-z_][A-Za-z0-9_]*')


def mask(src, literals=None):
    n = len(src)
    out = list(src)

    def blank(a, b):
        for k in range(a, b):
            if out[k] not in '\r\n':
                out[k] = ' '

    def scan_string(i):
        # i at the first char of the literal prefix ($, @ or ")
        j = i
        dollars = 0
        verbatim = False
        while j < n and src[j] in '$@':
            if src[j] == '$':
                dollars += 1
            else:
                verbatim = True
            j += 1
        q = 0
        while j + q < n and src[j + q] == '"':
            q += 1
        if not verbatim and q >= 3:  # raw string literal
            k = j + q
            blank(i, k)
            while k < n:
                if src[k] == '"':
                    r = 0
                    while k + r < n and src[k + r] == '"':
                        r += 1
                    if r >= q:
                        blank(k, k + q)
                        return k + q
                    blank(k, k + r)
                    k += r
                    continue
                if dollars and src[k] == '{':
                    r = 0
                    while k + r < n and src[k + r] == '{':
                        r += 1
                    if r >= dollars:
                        blank(k, k + r)
                        k = scan_code(k + r, hole=True)
                        blank(k, k + dollars)
                        k += dollars
                        continue
                    blank(k, k + r)
                    k += r
                    continue
                blank(k, k + 1)
                k += 1
            return k
        if not verbatim and q == 2:  # empty regular string
            blank(i, j + 2)
            return j + 2
        k = j + 1
        blank(i, k)
        while k < n:
            c = src[k]
            if verbatim:
                if c == '"':
                    if k + 1 < n and src[k + 1] == '"':
                        blank(k, k + 2)
                        k += 2
                        continue
                    blank(k, k + 1)
                    return k + 1
            else:
                if c == '\\':
                    blank(k, k + 2)
                    k += 2
                    continue
                if c == '"':
                    blank(k, k + 1)
                    return k + 1
                if c == '\n':  # unterminated: stop
                    return k
            if dollars and c == '{':
                if k + 1 < n and src[k + 1] == '{':
                    blank(k, k + 2)
                    k += 2
                    continue
                blank(k, k + 1)
                k = scan_code(k + 1, hole=True)
                blank(k, k + 1)
                k += 1
                continue
            if dollars and c == '}' and k + 1 < n and src[k + 1] == '}':
                blank(k, k + 2)
                k += 2
                continue
            blank(k, k + 1)
            k += 1
        return k

    def scan_code(i, hole=False):
        depth = 0
        paren = 0
        line_start = True
        while i < n:
            c = src[i]
            nx = src[i + 1] if i + 1 < n else ''
            if c == '\n':
                line_start = True
                i += 1
                continue
            if c in ' \t\r':
                i += 1
                continue
            if c == '#' and line_start and not hole:
                e = src.find('\n', i)
                e = n if e < 0 else e
                blank(i, e)
                i = e
                continue
            line_start = False
            if c == '/' and nx == '/':
                e = src.find('\n', i)
                e = n if e < 0 else e
                blank(i, e)
                i = e
                continue
            if c == '/' and nx == '*':
                e = src.find('*/', i + 2)
                e = n if e < 0 else e + 2
                blank(i, e)
                i = e
                continue
            if c == '"' or (c in '$@' and re.match(r'[$@]{1,6}"', src[i:i + 8])):
                end = scan_string(i)
                if literals is not None:
                    literals.append((i, end))
                i = end
                continue
            if c == "'":
                k = i + 1
                if k < n and src[k] == '\\':
                    k += 2
                    while k < n and src[k] != "'":
                        k += 1
                else:
                    k += 1
                if k < n and src[k] == "'":
                    blank(i, k + 1)
                    i = k + 1
                    continue
                i += 1
                continue
            if hole:
                if c in '([':
                    paren += 1
                elif c in ')]':
                    paren -= 1
                elif c == '{':
                    depth += 1
                elif c == '}':
                    if depth == 0:
                        return i
                    depth -= 1
                elif c == ':' and depth == 0 and paren == 0 and nx != ':' and src[i - 1] != ':':
                    # format specifier: text up to the closing brace
                    e = src.find('}', i)
                    e = n if e < 0 else e
                    blank(i, e)
                    i = e
                    continue
            if c == '@' and i + 1 < n and (src[i + 1].isalpha() or src[i + 1] == '_'):
                i += 1  # verbatim identifier
                continue
            i += 1
        return i

    scan_code(0)
    return ''.join(out)


def match_close(m, i, open_c, close_c):
    """m[i] == open_c; returns index of the matching close char."""
    d = 0
    n = len(m)
    while i < n:
        c = m[i]
        if c == open_c:
            d += 1
        elif c == close_c:
            d -= 1
            if d == 0:
                return i
        i += 1
    return n - 1


def match_angle(m, i):
    d = 0
    while i < len(m):
        if m[i] == '<':
            d += 1
        elif m[i] == '>':
            d -= 1
            if d == 0:
                return i
        elif m[i] in ';{}=':
            return -1
        i += 1
    return -1


MODIFIERS = {'public', 'private', 'protected', 'internal', 'static', 'readonly', 'const', 'sealed', 'override',
             'virtual', 'abstract', 'async', 'new', 'partial', 'extern', 'unsafe', 'volatile', 'required', 'file',
             'implicit', 'explicit'}
TYPE_KW = re.compile(r'\b(class|struct|interface|enum|record|delegate)\b')
ACCESS = ('public', 'private', 'protected', 'internal')


class Member:
    __slots__ = ('file', 'owner', 'kind', 'name', 'iface', 'static', 'access', 'mods', 'arity', 'params', 'ret',
                 'start', 'end', 'chunk_start', 'head_end', 'body_start', 'line', 'text', 'masked', 'header',
                 'readonly', 'accessors', 'nested_kind', 'dest', 'param_names')

    def key(self):
        q = (self.iface + '.') if self.iface else ''
        s = 'static ' if self.static else ''
        if self.kind in ('method', 'ctor'):
            ar = '<%d>' % self.arity if self.arity else ''
            return '%s%s%s%s(%s)' % (s, q, self.name, ar, ','.join(self.params))
        if self.kind == 'indexer':
            return '%s%sthis[%s]' % (s, q, ','.join(self.params))
        return '%s%s%s' % (s, q, self.name)

    def token(self):
        return (self.iface + '.' + self.name) if self.iface else self.name


def split_top(s, sep=','):
    parts, d, cur = [], 0, []
    for c in s:
        if c in '<([{':
            d += 1
        elif c in '>)]}':
            d -= 1
        if c == sep and d == 0:
            parts.append(''.join(cur))
            cur = []
        else:
            cur.append(c)
    if ''.join(cur).strip():
        parts.append(''.join(cur))
    return parts


def norm(s):
    s = re.sub(r'\s+', ' ', s).strip()
    s = re.sub(r'\s*([<>,\[\]\(\)\?])\s*', r'\1', s)
    return s.replace(',', ', ')


def param_info(ptext):
    types, names = [], []
    for p in split_top(ptext):
        p = p.strip()
        if not p:
            continue
        p = re.sub(r'^\[[^\]]*\]\s*', '', p)
        p = split_top(p, '=')[0].strip()
        m = re.match(r'(.*?)(@?[A-Za-z_]\w*)\s*$', p, re.S)
        if not m:
            types.append(norm(p))
            names.append('')
            continue
        types.append(norm(m.group(1)))
        names.append(m.group(2))
    return types, names


def parse_header(h):
    """h is masked header text (attributes stripped by caller); returns dict."""
    info = {'mods': [], 'kind': None, 'name': None, 'iface': '', 'arity': 0, 'params': [], 'param_names': [],
            'ret': '', 'nested_kind': ''}
    rest = h.strip()
    while True:
        m = re.match(r'(\w+)\s+', rest)
        if m and m.group(1) in MODIFIERS:
            info['mods'].append(m.group(1))
            rest = rest[m.end():]
            continue
        break
    tk = TYPE_KW.match(rest)
    if tk and tk.group(1) != 'delegate':
        info['kind'] = 'type'
        info['nested_kind'] = tk.group(1)
        after = rest[tk.end():].strip()
        after = re.sub(r'^(class|struct)\s+', '', after)  # record class/struct
        m = IDENT.match(after)
        info['name'] = m.group(0) if m else '?'
        g = after[m.end():] if m else ''
        if g.lstrip().startswith('<'):
            info['arity'] = len(split_top(g[g.index('<') + 1:match_angle(g, g.index('<'))]))
        return info
    if tk and tk.group(1) == 'delegate':
        info['kind'] = 'type'
        info['nested_kind'] = 'delegate'
        pi = rest.find('(')
        m = list(IDENT.finditer(rest[:pi]))
        info['name'] = m[-1].group(0) if m else '?'
        return info
    # find first top-level '(' (method), else property/field
    d = 0
    paren_at = -1
    k = 0
    while k < len(rest):
        c = rest[k]
        if c in '<[':
            d += 1
        elif c in '>]':
            d -= 1
        elif c == '(' and d == 0:
            prev = rest[:k].rstrip()
            pm = re.search(r'(@?[A-Za-z_]\w*)$', prev)
            if (pm and pm.group(1) not in MODIFIERS and pm.group(1) != 'new') or prev.endswith('>'):
                paren_at = k
                break
            k = match_close(rest, k, '(', ')') + 1
            continue
        elif c == '=' and d == 0:
            break
        k += 1
    if paren_at >= 0:
        before = rest[:paren_at].rstrip()
        close = match_close(rest, paren_at, '(', ')')
        info['params'], info['param_names'] = param_info(rest[paren_at + 1:close])
        arity = 0
        if before.endswith('>'):
            # generic method name<T>
            dd = 0
            for k in range(len(before) - 1, -1, -1):
                if before[k] == '>':
                    dd += 1
                elif before[k] == '<':
                    dd -= 1
                    if dd == 0:
                        arity = len(split_top(before[k + 1:-1]))
                        before = before[:k].rstrip()
                        break
        m = re.search(r'((?:[A-Za-z_]\w*(?:<[^()]*?>)?\.)*)(@?[A-Za-z_]\w*|operator\s*\S+)$', before)
        name = m.group(2)
        qual = m.group(1)[:-1] if m.group(1) else ''
        ret = before[:m.start()].strip()
        if qual and ret == '' and name:
            pass
        info['name'] = name
        info['arity'] = arity
        if qual and ret:
            info['iface'] = norm(qual)
        elif qual and not ret:
            # e.g. constructor can't be qualified; treat as part of return type
            info['iface'] = norm(qual)
        info['ret'] = norm(ret)
        info['kind'] = 'ctor' if ret == '' else 'method'
        return info
    # property / field / event / indexer
    m = re.search(r'\bthis\s*\[', rest)
    if m:
        close = match_close(rest, m.end() - 1, '[', ']')
        info['kind'] = 'indexer'
        info['name'] = 'this[]'
        info['params'], info['param_names'] = param_info(rest[m.end():close])
        q = rest[:m.start()].strip()
        info['ret'] = norm(q)
        return info
    if rest.startswith('event '):
        info['mods'].append('event')
        rest = rest[6:]
    head = re.split(r'=(?!>)|=>|\{|;', rest, 1)[0].rstrip()
    m = re.search(r'((?:[A-Za-z_]\w*(?:<[^()]*?>)?\.)*)(@?[A-Za-z_]\w*)\s*$', head)
    info['name'] = m.group(2) if m else '?'
    qual = m.group(1)[:-1] if m and m.group(1) else ''
    if qual:
        info['iface'] = norm(qual)
    info['ret'] = norm(head[:m.start()] if m else head)
    info['kind'] = 'member'
    return info


def find_type_bodies(masked, type_name):
    """Yield (decl_start, open_brace, close_brace) for every top-level declaration of type_name."""
    for m in re.finditer(r'\b(class|struct|record|interface)\s+' + re.escape(type_name) + r'\b', masked):
        # only top level (brace depth 0 at m.start())
        if masked.count('{', 0, m.start()) - masked.count('}', 0, m.start()) != 0:
            continue
        ob = masked.find('{', m.end())
        cb = match_close(masked, ob, '{', '}')
        ls = masked.rfind('\n', 0, m.start()) + 1
        yield ls, ob, cb


def parse_members(path, src, masked, type_name, bodies=None):
    members = []
    for decl_start, ob, cb in (find_type_bodies(masked, type_name) if bodies is None else bodies):
        i = ob + 1
        # chunk starts after the opening brace line
        nl = src.find('\n', ob)
        chunk = nl + 1 if nl >= 0 and nl < cb else ob + 1
        while True:
            # skip whitespace in masked (comments/directives are already spaces)
            j = i
            while j < cb and masked[j] in ' \t\r\n':
                j += 1
            if j >= cb:
                break
            start = j
            k = j
            # attributes
            while masked[k] == '[':
                k = match_close(masked, k, '[', ']') + 1
                while masked[k] in ' \t\r\n':
                    k += 1
            hstart = k
            seen_eq = False
            pd = 0
            end = None
            body_start = None
            head_end = None
            while k < cb:
                c = masked[k]
                if c in '([':
                    pd += 1
                elif c in ')]':
                    pd -= 1
                elif pd == 0 and c == '=' and masked[k + 1] != '=' and masked[k - 1] not in '=!<>':
                    if not seen_eq:
                        head_end = k
                        body_start = k
                    seen_eq = True
                elif pd == 0 and c == ';':
                    if head_end is None:
                        head_end = k
                    end = k + 1
                    break
                elif pd == 0 and c == '{':
                    if seen_eq:
                        k = match_close(masked, k, '{', '}') + 1
                        continue
                    if head_end is None:
                        head_end = k
                        body_start = k
                    k = match_close(masked, k, '{', '}') + 1
                    # property initializer or trailing ';' (e.g. record/struct body never)
                    t = k
                    while t < cb and masked[t] in ' \t\r\n':
                        t += 1
                    if t < cb and masked[t] == '=' and masked[t + 1] != '>':
                        seen_eq = True
                        k = t + 1
                        continue
                    if t < cb and masked[t] == ';':
                        end = t + 1
                    else:
                        end = k
                    break
                k += 1
            if end is None:
                end = cb
            header = masked[hstart:head_end]
            info = parse_header(header)
            mem = Member()
            mem.file = path
            mem.owner = type_name
            mem.kind = info['kind']
            mem.name = info['name']
            mem.iface = info['iface']
            mem.mods = info['mods']
            mem.static = 'static' in mem.mods or 'const' in mem.mods
            acc = [x for x in mem.mods if x in ACCESS]
            mem.access = ' '.join(acc) if acc else ('' if mem.iface else 'private')
            mem.arity = info['arity']
            mem.params = info['params']
            mem.param_names = info['param_names']
            mem.ret = info['ret']
            mem.nested_kind = info['nested_kind']
            mem.readonly = 'readonly' in mem.mods or 'const' in mem.mods
            mem.start = start
            mem.end = end
            mem.head_end = head_end
            mem.body_start = body_start
            mem.chunk_start = chunk
            mem.line = src.count('\n', 0, start) + 1
            mem.text = src[start:end]
            mem.masked = masked[start:end]
            mem.header = header
            mem.accessors = ''
            if mem.kind == 'member' and body_start is not None and masked[body_start] == '{':
                inner = masked[body_start:]
                mem.accessors = ' '.join(re.findall(r'\b(get|set|init)\b', inner[:match_close(inner, 0, '{', '}')]))
                mem.kind = 'property'
            elif mem.kind == 'member' and body_start is not None and masked[body_start:body_start + 2] == '=>':
                mem.kind = 'property'
                mem.accessors = 'get'
            elif mem.kind == 'member':
                mem.kind = 'event' if 'event' in mem.mods else ('const' if 'const' in mem.mods else 'field')
            members.append(mem)
            nl = src.find('\n', end)
            chunk = nl + 1 if 0 <= nl < cb else end
            i = end
    return members


def line_col(src, off):
    line = src.count('\n', 0, off) + 1
    col = off - (src.rfind('\n', 0, off) + 1) + 1
    return line, col
